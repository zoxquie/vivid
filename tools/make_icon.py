"""Builds the Fjord Vision app icon and in-app badge from the fjord logo.

    python3 tools/make_icon.py [assets/brand/fjord-logo.jpg]

Writes:
  assets/fjord-vision.ico   Windows icon, 16-256 px. Small sizes use a tight crop of the
                            glowing F so it stays readable in the taskbar and tray;
                            64 px and up use the fuller logo with its smoke.
  assets/brand/window.ico   16-48 px only, uncompressed frames. Loaded at runtime for the
                            window and tray icon (safe for every Icon reader, including Mono's).
  assets/brand/mark.png     192 px rounded badge drawn at the top left of the app.
"""
import struct
import sys

from PIL import Image, ImageDraw

# Crops in fractions of the square logo (the bright F sits slightly below centre).
TIGHT = (0.262, 0.311, 0.717, 0.766)
WIDE = (0.096, 0.136, 0.909, 0.949)


def crop(logo: Image.Image, box) -> Image.Image:
    w, h = logo.size
    return logo.crop((round(box[0] * w), round(box[1] * h), round(box[2] * w), round(box[3] * h)))


def rounded(img: Image.Image, size: int, radius_frac: float) -> Image.Image:
    ss = 4
    big = img.resize((size * ss, size * ss), Image.LANCZOS).convert("RGBA")
    mask = Image.new("L", big.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, big.size[0] - 1, big.size[1] - 1], radius=round(size * ss * radius_frac), fill=255)
    big.putalpha(mask)
    return big.resize((size, size), Image.LANCZOS)


def write_ico(path: str, images, png_from: int = 9999) -> None:
    """Classic ICO: 32-bit BGRA DIB frames plus the 1-bit AND mask every icon reader expects.
    Frames png_from px and larger are stored as PNG (the usual layout for 256 px art)."""
    import io
    blobs = []
    for im in images:
        w, h = im.size
        if w >= png_from:
            buf = io.BytesIO()
            im.save(buf, format="PNG", optimize=True)
            blobs.append((w, h, buf.getvalue()))
            continue
        px = im.convert("RGBA").load()
        xor = bytearray()
        for y in range(h - 1, -1, -1):  # bottom-up rows
            for x in range(w):
                r, g, b, a = px[x, y]
                xor += bytes((b, g, r, a))
        stride = ((w + 31) // 32) * 4
        mask = bytearray()
        for y in range(h - 1, -1, -1):
            row = bytearray(stride)
            for x in range(w):
                if px[x, y][3] == 0:
                    row[x // 8] |= 0x80 >> (x % 8)
            mask += row
        header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(mask), 0, 0, 0, 0)
        blobs.append((w, h, header + xor + mask))
    out = bytearray(struct.pack("<HHH", 0, 1, len(blobs)))
    offset = 6 + 16 * len(blobs)
    for w, h, data in blobs:
        out += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, _, data in blobs:
        out += data
    with open(path, "wb") as f:
        f.write(out)


def main() -> None:
    src = sys.argv[1] if len(sys.argv) > 1 else "assets/brand/fjord-logo.jpg"
    logo = Image.open(src).convert("RGB")
    tight, wide = crop(logo, TIGHT), crop(logo, WIDE)

    frames = {s: rounded(tight if s <= 48 else wide, s, 0.2) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)}
    write_ico("assets/fjord-vision.ico", list(frames.values()), png_from=64)
    write_ico("assets/brand/window.ico", [frames[s] for s in frames if s <= 48])
    rounded(tight, 192, 0.24).save("assets/brand/mark.png", optimize=True)
    print("wrote assets/fjord-vision.ico, assets/brand/window.ico and assets/brand/mark.png")


if __name__ == "__main__":
    main()
