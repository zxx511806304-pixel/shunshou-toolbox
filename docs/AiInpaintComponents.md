# 本地 AI 修补组件

## 轻量 AI：MI-GAN

使用 Picsart AI Research (PAIR) 发布的 MI-GAN-512-Places2 ONNX Pipeline v2。官方项目 README 的 For Developers 部分直接链接第一作者 Andranik Sargsyan 的 Hugging Face 模型文件，软件使用同一文件的固定提交版本，不使用匿名重打包模型。

- 官方项目：<https://github.com/Picsart-AI-Research/MI-GAN>
- 项目源码提交：`2b793c5ece43f4253e32d4afc257120a5deed6f5`
- 权重发布仓库：<https://huggingface.co/andraniksargsyan/migan>
- 权重提交：`406830d0fa60666da0071c342ad2fbc8f30c5c64`
- 上游文件名：`migan_pipeline_v2.onnx`；软件中为 `model.onnx`
- 大小：28,079,181 字节
- SHA-256：`6f1f3530a1a2324b19752018ce756088b07973cda8d7d890034ace5c8a48c40b`
- 上述 SHA-256 来自作者模型仓库的 LFS 元数据，并在下载和复制后重新验证。

官方仓库的 `LICENSE` 与单独的 `LICENSE-WEIGHTS` 都是 MIT License，Copyright (c) 2024 Picsart AI Research (PAIR)。两份原文随组件保留在 `licenses` 文件夹，分别覆盖代码与预训练权重。MIT 允许使用、修改和分发，包括商业使用；分发时必须保留原始版权和许可声明。模型发布页也标记为 MIT。

输入是 RGB uint8 图像与 uint8 二值遮罩：255 表示保留区域，0 表示需要补全的区域。官方 ONNX pipeline 包含围绕遮罩裁剪、缩放至 512×512、归一化、推理和拼回原图等步骤；它支持任意分辨率输入，但很大的修补区域仍受模型内部处理尺寸影响。

这是图像补全网络。视频模式对本地视频逐帧推理，适合小范围水印和简单背景，不能把它宣传成具备时序一致性的深度视频生成模型。复杂运动或纹理可能闪烁，应先查看短片预览。CPU 与 GPU 都是在用户自己的电脑上计算，软件不上传视频。实际速度和可用 GPU 由运行组件在本机检查。

开发者通过 `scripts/Get-AiInpaintRuntime.ps1` 重建固定模型与许可文件；下载不会执行权重中的 Python 代码。模型推理使用独立 ONNX Runtime 进程，与 OCR 的运行库分离。

## 深度 AI：STTN

使用 ECCV 2020 项目 STTN 的时空注意力网络，由相邻视频帧提供区域补全的上下文。它是真正的多帧视频补全网络，不是把逐帧图像模型重新命名。软件通过单独构建步骤导出固定窗口的 ONNX 模型；发布包仅携带 ONNX、独立推理程序和许可，不携带 Python、PyTorch 或原始 checkpoint。

- 官方项目：<https://github.com/researchmm/STTN>
- 项目源码提交：`f39f62c5bbbe3e3eba084c487353a2c651bfdcde`
- checkpoint：官方 README 的作者 Google Drive 下载链接，文件 ID 为 `1ZAMV8547wmZylKRt5qR_tC5VlosXD4Wv`
- 原始文件名：`sttn.pth`；大小：66,252,587 字节
- 原始 checkpoint SHA-256：`25b0c2c30042d82efd1893bd42ec726764262d94115393a1718f8d65d2a7817b`
- 此哈希由原始作者链接的实际下载响应计算后固定，用于后续重建校验，不冒充上游单独发布的校验和。
- 源码归档、checkpoint 和许可的下载地址与 SHA-256 记录于 `scripts/sttn-runtime-lock.json`；最终 ONNX 的导出信息另外记录。

当前导出采用 opset 17，输入 `frames` 为 RGB float32 `[-1, 1]`，形状 `[1,5,3,240,432]`；`masks` 为 float32 `[1,5,1,240,432]`，1 表示补全、0 表示保留（与 MI-GAN 的 uint8 mask 定义不同）。输出为中心帧 float32 `[1,3,240,432]`。`sttn.export.json` 记录最终 ONNX 的 SHA-256、尺寸、依赖版本与数值验证结果。导出脚本检查 ONNX 与原始 PyTorch 模型的数值一致性，并验证相邻帧改变确实影响遮罩区域的输出。

STTN 官方项目使用 MIT License，README 直接提供预训练权重，项目没有另外声明禁止商业使用的权重条款。软件保留官方 `LICENSE` 原文（包含其原有版权行），不将原文替换成新的作者声明。此记录说明实际获取来源和随项目提供的许可，不代表模型效果或法律保证。

STTN 内部使用较低处理分辨率；借助相邻帧通常能改善时序连贯性，但不能保证补回原本被遮挡的真实内容。大面积遮挡、快速运动、复杂纹理以及场景切换仍可能出现模糊或不一致，应先查看短片预览。深度 AI 的内存和运算需求高于轻量 AI，具体表现由用户自己的硬件决定。

## 独立推理运行库

- ONNX Runtime DirectML 1.24.4：微软官方 [onnxruntime](https://github.com/microsoft/onnxruntime)，MIT License。私有运行进程避免与 OCR 所用 ONNX Runtime 相互加载冲突。
- Microsoft DirectML 1.15.4：微软官方 [DirectML](https://github.com/microsoft/DirectML)，随 NuGet 二进制提供的是 **Microsoft Software License Terms — Microsoft DirectX Machine Learning (DirectML)**。其第 1(a) 条允许在 Windows/Xbox 上使用，并在开发的机器学习应用中复制、分发该软件；二进制许可不是代码样例的 MIT 许可。
- `scripts/Build-AiRunner.ps1` 从固定 NuGet 依赖收集原始 LICENSE、第三方声明和依赖清单至 `licenses/runner`；保留 `LICENSE.txt` 与 `LICENSE-CODE.txt` 的区别。

CPU 与 DirectML GPU 推理均在本机进行。首次开发构建可能下载模型和运行组件，发布包准备完毕后，处理本地视频无需将视频上传到服务器。

开发重建时，先运行 `scripts/Get-SttnSource.ps1` 获取固定源码与 checkpoint，由 `scripts/Export-Sttn.py` 在隔离的开发环境导出，再运行 `scripts/Get-AiInpaintRuntime.ps1 -RequireSttn` 验证两个模型并刷新组件清单。该开关在深度模型缺失或导出元数据不匹配时中止，避免把尚未准备好的引擎打进发布包。完整运行库由 `scripts/Build-AiRunner.ps1` 单独构建。
