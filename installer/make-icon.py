"""Draws the Tickle TV icon (an LCARS-style orange elbow with a lavender bar) and writes it as app.ico
next to the web project. Pure Python, no packages needed. Run: python installer/make-icon.py"""
import io, os, struct, zlib

ORANGE = (255, 153, 102, 255)
LAVENDER = (204, 153, 204, 255)
RED = (204, 102, 102, 255)
BLACK = (0, 0, 0, 255)
CLEAR = (0, 0, 0, 0)


def rounded(x, y, w, h, r, px, py):
    """Is (px, py) inside the rounded rectangle?"""
    if px < x or py < y or px >= x + w or py >= y + h:
        return False
    cx = min(max(px, x + r), x + w - 1 - r)
    cy = min(max(py, y + r), y + h - 1 - r)
    return (px - cx) ** 2 + (py - cy) ** 2 <= r * r


def pixel(n, px, py):
    s = n / 256.0
    # Backplate: a black rounded square so the icon reads on any background.
    if not rounded(0, 0, n, n, int(56 * s), px, py):
        return CLEAR
    # The elbow: orange across the top and down the left, with the inner corner rounded.
    top = py < int(88 * s)
    left = px < int(72 * s)
    inner = rounded(int(72 * s) - int(40 * s), int(88 * s) - int(40 * s), int(300 * s), int(300 * s), int(40 * s), px, py)
    if (top or left) and not (inner and not top and not left):
        if top and px >= int(96 * s) and px < int(110 * s):
            return BLACK  # a thin break in the top bar, the LCARS seam
        return ORANGE
    # Lavender bar and red bar inside the elbow.
    if rounded(int(100 * s), int(120 * s), int(136 * s), int(40 * s), int(10 * s), px, py):
        return LAVENDER
    if rounded(int(100 * s), int(176 * s), int(90 * s), int(40 * s), int(10 * s), px, py):
        return RED
    return BLACK


def png(n):
    raw = bytearray()
    for y in range(n):
        raw.append(0)
        for x in range(n):
            raw.extend(pixel(n, x, y))

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", n, n, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def ico(sizes):
    images = [png(n) for n in sizes]
    out = io.BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(sizes)))
    offset = 6 + 16 * len(sizes)
    for n, data in zip(sizes, images):
        out.write(struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
    for data in images:
        out.write(data)
    return out.getvalue()


here = os.path.dirname(os.path.abspath(__file__))
target = os.path.join(here, "..", "src", "TickleTV.Scheduler.Web", "app.ico")
with open(target, "wb") as f:
    f.write(ico([16, 24, 32, 48, 64, 128, 256]))
print("wrote", os.path.normpath(target))
