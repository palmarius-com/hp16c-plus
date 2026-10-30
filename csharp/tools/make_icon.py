#!/usr/bin/env python3
"""Draws the application icon: the blue Palmarius square of the faceplate
badge, filled with a 45 degree gradient from the gold of the HP-12C plate to
the silver of the others; outside the square it is transparent. Each size is
drawn on its own, so the line stays sharp at 16 pixels.

    python csharp/tools/make_icon.py [preview.png]
"""
import math
import pathlib
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter

HERE = pathlib.Path(__file__).resolve().parent
OUT = HERE.parent / "src" / "Hp16c.App" / "Assets" / "app.ico"
BLUE = (0x00, 0x80, 0xFF, 255)      # PalmBlue in CalculatorView
GOLD = (230, 176, 52)
LIGHT = (252, 246, 228)             # a highlight: gold mixed with silver looks brown
SILVER = (192, 201, 212)
STOPS = ((0.34, GOLD), (0.5, LIGHT), (0.66, SILVER))
ANGLE = math.radians(45)
SIZES = [256, 128, 64, 48, 32, 24, 16]
OVER = 8                            # drawn large, then scaled down


def colour(t):
    """Solid gold, a short bright band across the diagonal, solid silver."""
    if t <= STOPS[0][0]:
        return STOPS[0][1]
    for (t0, c0), (t1, c1) in zip(STOPS, STOPS[1:]):
        if t <= t1:
            u = (t - t0) / (t1 - t0)
            return tuple(round(a + (b - a) * u) for a, b in zip(c0, c1))
    return STOPS[-1][1]


def gradient(size):
    """Gold at the upper left, silver at the lower right."""
    img = Image.new("RGB", (size, size))
    cos, sin = math.cos(ANGLE), math.sin(ANGLE)
    length = (size - 1) * (cos + sin) or 1
    pixels = []
    for y in range(size):
        for x in range(size):
            pixels.append(colour((x * cos + y * sin) / length))
    img.putdata(pixels)
    return img


def draw(size):
    n = size * OVER
    box = (0, 0, n - 1, n - 1)
    radius = round(n * 8 / 29)
    line = max(2 * OVER, round(n * 0.12))
    mask = Image.new("L", (n, n), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box, radius=radius, fill=255)
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    img.paste(gradient(size).resize((n, n), Image.BICUBIC), (0, 0), mask)
    ImageDraw.Draw(img).rounded_rectangle(box, radius=radius, outline=BLUE, width=line)
    img.alpha_composite(glare(n, mask))
    return img.resize((size, size), Image.LANCZOS)


def glare(n, mask):
    """A glint in the gold, a third in from the left, a quarter down."""
    x, y = n / 3, n / 4
    arm, waist = n * 0.2, n * 0.016
    layer = Image.new("RGBA", (n, n), (255, 255, 255, 0))
    glow = Image.new("L", (n, n), 0)
    r = n * 0.05
    ImageDraw.Draw(glow).ellipse((x - r, y - r, x + r, y + r), fill=210)
    glow = glow.filter(ImageFilter.GaussianBlur(n * 0.035))
    star = Image.new("L", (n, n), 0)
    ImageDraw.Draw(star).polygon(
        [(x - arm, y), (x - waist, y - waist), (x, y - arm), (x + waist, y - waist),
         (x + arm, y), (x + waist, y + waist), (x, y + arm), (x - waist, y + waist)], fill=255)
    layer.putalpha(ImageChops.multiply(ImageChops.lighter(glow, star), mask))
    return layer


def main():
    images = [draw(s) for s in SIZES]
    OUT.parent.mkdir(exist_ok=True)
    images[0].save(OUT, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[1:])
    print(OUT, OUT.stat().st_size, "bytes")
    if len(sys.argv) > 1:
        sheet = Image.new("RGBA", (900, 620), (32, 32, 32, 255))
        d = ImageDraw.Draw(sheet)
        d.rectangle((0, 310, 900, 620), fill=(238, 238, 238, 255))
        for top in (20, 330):
            x = 20
            for img, size in zip(images, SIZES):
                sheet.alpha_composite(img, (x, top))
                x += size + 20
        sheet.save(sys.argv[1])


if __name__ == "__main__":
    main()
