"""Generates vivid.ico: a prism ring with a white centre (same design as the in-app logo)."""
import colorsys
import math
import sys

from PIL import Image, ImageDraw


def render(size: int) -> Image.Image:
    ss = 4  # supersample for smooth edges
    n = size * ss
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    px = img.load()
    c = (n - 1) / 2
    r_out = n * 0.48
    r_in = r_out * (1 - 0.48)
    bg = (10, 11, 15, 255)
    for y in range(n):
        for x in range(n):
            dx, dy = x - c, y - c
            d = math.hypot(dx, dy)
            if d > r_out:
                continue
            if d < r_in:
                px[x, y] = bg
                continue
            ang = (math.degrees(math.atan2(dy, dx)) + 90 + 200) % 360
            r, g, b = colorsys.hsv_to_rgb(ang / 360, 0.75, 1.0)
            px[x, y] = (int(r * 255), int(g * 255), int(b * 255), 255)
    draw = ImageDraw.Draw(img)
    dot = n * 0.16 / 2
    draw.ellipse([c - dot, c - dot, c + dot, c + dot], fill=(255, 255, 255, 255))
    return img.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "vivid.ico"
    big = render(256)
    big.save(out, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print("wrote", out)
