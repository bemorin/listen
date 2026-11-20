"""Packs icon-16..256.png (from make-icon.ps1) into listen.ico, one PNG entry per size."""
import struct
import sys
from pathlib import Path

folder = Path(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).parent)
sizes = [16, 24, 32, 48, 64, 128, 256]
images = [(s, (folder / f"icon-{s}.png").read_bytes()) for s in sizes]
header = struct.pack("<HHH", 0, 1, len(images))
offset = 6 + 16 * len(images)
entries, blobs = b"", b""
for size, data in images:
    dim = 0 if size >= 256 else size
    entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
    blobs += data
(folder / "listen.ico").write_bytes(header + entries + blobs)
print("listen.ico written")
