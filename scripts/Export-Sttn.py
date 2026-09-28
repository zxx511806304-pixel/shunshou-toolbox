"""Export the verified upstream STTN generator as a fixed five-frame temporal ONNX model.

Developer-only dependencies live in .tools/sttn-export-env. No Python or checkpoint is shipped.
The source/checkpoint must be acquired by the pinned STTN acquisition workflow first.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys

import numpy as np
import onnx
import onnxruntime as ort
import torch


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, default=Path(".tools/sources/sttn"))
    parser.add_argument("--checkpoint", type=Path, default=Path(".tools/downloads/sttn.pth"))
    parser.add_argument("--output", type=Path, default=Path("runtime/ai-inpaint/sttn.onnx"))
    args = parser.parse_args()
    expected_checkpoint = "25b0c2c30042d82efd1893bd42ec726764262d94115393a1718f8d65d2a7817b"
    if sha256(args.checkpoint) != expected_checkpoint:
        raise RuntimeError("STTN checkpoint hash does not match the reviewed official download")
    sys.path.insert(0, str(args.source.resolve()))
    from model.sttn import InpaintGenerator

    torch.set_num_threads(min(8, torch.get_num_threads()))
    torch.manual_seed(41)
    network = InpaintGenerator(init_weights=False)
    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=True)
    network.load_state_dict(checkpoint["netG"], strict=True)
    network.eval()
    for parameter in network.parameters():
        parameter.requires_grad_(False)

    class FiveFrameCenter(torch.nn.Module):
        def __init__(self, generator: torch.nn.Module):
            super().__init__()
            self.generator = generator

        def forward(self, frames: torch.Tensor, masks: torch.Tensor) -> torch.Tensor:
            # Temporal attention receives all five original frames. Only the center decoder is needed.
            features = self.generator.encoder((frames * (1 - masks)).reshape(5, 3, 240, 432))
            inferred = self.generator.infer(features, masks[0])
            return torch.tanh(self.generator.decoder(inferred[2:3]))

    model = FiveFrameCenter(network).eval()
    frames = torch.rand(1, 5, 3, 240, 432) * 2 - 1
    masks = torch.zeros(1, 5, 1, 240, 432)
    masks[:, :, :, 80:135, 170:240] = 1
    args.output.parent.mkdir(parents=True, exist_ok=True)
    print("Exporting real STTN five-frame attention model...", flush=True)
    with torch.no_grad():
        reference = model(frames, masks).numpy()
        changed_neighbors = frames.clone()
        changed_neighbors[:, [0, 1, 3, 4]] *= -1
        temporal_reference = model(changed_neighbors, masks).numpy()
        torch.onnx.export(
            model, (frames, masks), str(args.output), opset_version=17, dynamo=False,
            input_names=["frames", "masks"], output_names=["result"],
            do_constant_folding=True,
        )
    exported = onnx.load(args.output)
    onnx.checker.check_model(exported)
    for key, value in {
        "shunshou.model": "sttn-five-frame-center",
        "source": "https://github.com/researchmm/STTN",
        "source_commit": "f39f62c5bbbe3e3eba084c487353a2c651bfdcde",
        "context_frames": "5",
        "normalization": "RGB float32 [-1,1]; mask float32 1=hole 0=known",
    }.items():
        item = exported.metadata_props.add()
        item.key, item.value = key, value
    onnx.save(exported, args.output)
    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    session = ort.InferenceSession(str(args.output), sess_options=options, providers=["CPUExecutionProvider"])
    actual = session.run(None, {"frames": frames.numpy(), "masks": masks.numpy()})[0]
    error = float(np.max(np.abs(reference - actual)))
    influence = float(np.mean(np.abs(reference[:, :, 80:135, 170:240] - temporal_reference[:, :, 80:135, 170:240])))
    if error > 0.003:
        raise RuntimeError(f"ONNX parity failed, maximum absolute error {error}")
    if influence < 1e-5:
        raise RuntimeError("Adjacent frames did not influence the masked output")
    report = {
        "model": "STTN", "source_commit": "f39f62c5bbbe3e3eba084c487353a2c651bfdcde",
        "checkpoint_sha256": expected_checkpoint, "onnx_sha256": sha256(args.output),
        "onnx_bytes": args.output.stat().st_size, "torch": torch.__version__, "onnx": onnx.__version__, "onnxruntime": ort.__version__,
        "inputs": {"frames": [1, 5, 3, 240, 432], "masks": [1, 5, 1, 240, 432]},
        "output": [1, 3, 240, 432], "opset": 17, "parity_max_abs_error": error, "adjacent_frame_influence_mean_abs": influence,
    }
    args.output.with_suffix(".export.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2), flush=True)


if __name__ == "__main__":
    main()
