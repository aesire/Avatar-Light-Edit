# PNG 여러 장을 ICO 하나로 묶는다 (Vista 이후 ICO는 PNG를 그대로 담을 수 있다). 2026-11-02
import struct, sys

src, out, sizes = sys.argv[1], sys.argv[2], [int(x) for x in sys.argv[3:]]
blobs = [open(f"{src}/logo-{n}.png", "rb").read() for n in sizes]
head = struct.pack("<HHH", 0, 1, len(blobs))
offset = 6 + 16 * len(blobs)
dirs = b""
for n, b in zip(sizes, blobs):
    dirs += struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(b), offset)
    offset += len(b)
with open(out, "wb") as f:
    f.write(head + dirs + b"".join(blobs))
