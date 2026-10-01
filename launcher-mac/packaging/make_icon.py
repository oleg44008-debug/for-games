#!/usr/bin/env python3
"""Encode the original logo as a macOS icon; preserve its aspect and transparency."""
from __future__ import annotations
import argparse
import hashlib
import io
from pathlib import Path
import struct
from PIL import Image

EXPECTED = "69cdb26a75f82302b8f476788a705bbdd6c1ed8d74934e3f05cb4df6a41468d4"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if hashlib.sha256(args.source.read_bytes()).hexdigest() != EXPECTED:
        raise ValueError("Use the unchanged original DUSTORE PNG, not a replacement logo.")
    chunks = []
    with Image.open(args.source) as image:
        original = image.convert("RGBA")
        for kind, size in ((b"icp4", 16), (b"icp5", 32), (b"icp6", 64), (b"ic07", 128),
                           (b"ic08", 256), (b"ic09", 512), (b"ic10", 1024)):
            scale = min(size / original.width, size / original.height)
            resized = original.resize((max(1, round(original.width * scale)), max(1, round(original.height * scale))), Image.Resampling.LANCZOS)
            square = Image.new("RGBA", (size, size), (0, 0, 0, 0))
            square.alpha_composite(resized, ((size - resized.width) // 2, (size - resized.height) // 2))
            encoded = io.BytesIO(); square.save(encoded, format="PNG")
            data = encoded.getvalue()
            chunks.append(kind + struct.pack(">I", len(data) + 8) + data)
    contents = b"".join(chunks)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(b"icns" + struct.pack(">I", len(contents) + 8) + contents)
    print(args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
