"""Prepare pinned standalone recovery libraries; requires Python 3.14 stdlib."""

import argparse
import bz2
import gzip
import hashlib
import io
import json
import lzma
import struct
import sys
import tarfile
from pathlib import Path


def verified_file(root, item):
    path = (root / item["file"]).resolve()
    if not path.is_relative_to(root.resolve()) or not path.is_file():
        raise ValueError(f"Missing source/package or invalid path: {path}")
    if hashlib.sha256(path.read_bytes()).hexdigest() != item["sha256"]:
        raise ValueError(f"SHA256 mismatch: {path}")
    return path


def rpm_members(path):
    """Read SRPM payloads without running any package build/install scripts."""
    data = path.read_bytes()

    def header_end(offset):
        if data[offset:offset + 3] != bytes.fromhex("8eade8"):
            raise ValueError(f"Invalid RPM header: {path}")
        count, size = struct.unpack(">II", data[offset + 8:offset + 16])
        return offset + 16 + count * 16 + size

    offset = (header_end(96) + 7) & ~7
    data = data[header_end(offset):]
    if data.startswith(b"\x1f\x8b"):
        data = gzip.decompress(data)
    elif data.startswith(b"BZh"):
        data = bz2.decompress(data)
    elif data.startswith(b"\xfd7zXZ\0"):
        data = lzma.decompress(data)
    else:
        from compression import zstd
        data = zstd.decompress(data)
    offset = 0
    while offset < len(data):
        header = data[offset:offset + 110]
        if header[:6] not in (b"070701", b"070702"):
            raise ValueError(f"Invalid RPM cpio payload: {path}")
        values = [int(header[i:i + 8], 16) for i in range(6, 110, 8)]
        size, name_size = values[6], values[11]
        name = data[offset + 110:offset + 110 + name_size - 1].decode()
        start = (offset + 110 + name_size + 3) & ~3
        offset = (start + size + 3) & ~3
        if name == "TRAILER!!!":
            return
        yield name, data[start:start + size]


def collect_licenses(archive, output, prefix, recipes):
    # Retain top-level notices and the Cygwin/newlib component notices. Nested
    # upstream source tarballs are inspected, never installed or executed.
    with tarfile.open(fileobj=archive, mode="r|*") as source:
        for entry in source:
            if not entry.isfile():
                continue
            parts = Path(entry.name).parts
            name = parts[-1]
            normalized = name.upper()
            if len(parts) <= 3 and (name.endswith((".cygport", ".patch", ".spec"))
                    or name in {"compile.sh", "INSTALL", "update-libtool-gcc.sh"}):
                (recipes / (prefix + "--" + "_".join(parts))).write_bytes(source.extractfile(entry).read())
            if len(parts) <= 3 and (normalized.startswith(("COPYING", "LICENSE"))
                    or normalized in {"CYGWIN_LICENSE", "NOTICE", "README.IJG", "AUTHORS"}):
                stream = source.extractfile(entry)
                destination = output / (prefix + "--" + "_".join(parts))
                destination.write_bytes(stream.read())
            elif len(parts) <= 2 and name.endswith((".tar.xz", ".tar.gz", ".tar.bz2", ".tgz")):
                collect_licenses(source.extractfile(entry), output, prefix, recipes)


def main():
    if sys.version_info < (3, 14):
        raise RuntimeError("Python 3.14 or later is required for tar.zst support")
    parser = argparse.ArgumentParser()
    for name in ("lock", "downloads", "sources", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    lock = json.loads(args.lock.read_text(encoding="utf-8-sig"))
    binary_dir = args.output / "bin"
    license_dir = args.output / "licenses"
    recipes = args.sources / "recipes"
    binary_dir.mkdir(parents=True, exist_ok=True)
    license_dir.mkdir(parents=True, exist_ok=True)
    recipes.mkdir(parents=True, exist_ok=True)
    retained = lock["retainedDll"]
    retained_path = binary_dir / retained["name"]
    if retained_path.exists() and hashlib.sha256(retained_path.read_bytes()).hexdigest() != retained["sha256"]:
        raise ValueError("Retained libewf DLL does not match its pinned corresponding source")
    collected_sources = set()
    for package in lock["packages"]:
        binary = verified_file(args.downloads, package["install"])
        with tarfile.open(binary) as archive:
            for item in package["files"]:
                data = archive.extractfile(item["archivePath"]).read()
                if hashlib.sha256(data).hexdigest() != item["sha256"]:
                    raise ValueError("Runtime content SHA256 mismatch: " + item["name"])
                destination = (binary_dir / item["name"]).resolve()
                if not destination.is_relative_to(binary_dir.resolve()):
                    raise ValueError("Invalid runtime destination")
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(data)
        source = verified_file(args.sources, package["source"])
        if package["source"]["sha256"] in collected_sources:
            continue
        collected_sources.add(package["source"]["sha256"])
        with source.open("rb") as stream:
            collect_licenses(stream, license_dir, package["package"], recipes)
        if package["package"] == "zlib0":
            # zlib embeds its original license in zlib.h rather than COPYING.
            with tarfile.open(source) as outer:
                member = next(x for x in outer.getmembers() if x.name.endswith(".tar.xz"))
                with tarfile.open(fileobj=outer.extractfile(member)) as inner:
                    header = next(x for x in inner.getmembers() if x.name.endswith("/zlib.h"))
                    (license_dir / "zlib0--zlib.h").write_bytes(inner.extractfile(header).read())
    for item in lock["additionalSources"]:
        path = verified_file(args.sources, item)
        if path.suffix == ".rpm":
            for name, data in rpm_members(path):
                if name.endswith((".spec", ".patch")):
                    (recipes / (item["package"] + "--" + Path(name).name)).write_bytes(data)
                if name.endswith((".tar.gz", ".tar.xz", ".tar.bz2", ".tgz")):
                    collect_licenses(io.BytesIO(data), license_dir, item["package"], recipes)
        else:
            with path.open("rb") as stream:
                collect_licenses(stream, license_dir, item["package"], recipes)
    manifest = json.dumps(lock, ensure_ascii=False, indent=2) + "\n"
    (args.output / "dependency-sources.json").write_text(manifest, encoding="utf-8")
    (args.sources / "dependency-sources.json").write_text(manifest, encoding="utf-8")
    print(f"Prepared {sum(len(x['files']) for x in lock['packages'])} pinned runtime files and original source licenses")


if __name__ == "__main__":
    main()
