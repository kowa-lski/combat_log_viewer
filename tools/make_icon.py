"""Draws the app icon (a magnifying glass over a small bar chart) and writes app.ico.

Each size is drawn separately at 8x and scaled down, so small sizes get proportionally thicker
lines and drop the chart, which would only blur at 16-32px.

Usage (from the project folder): python tools/make_icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

BG = (36, 38, 44, 255)       # #24262C, the sidebar colour
LENS = (53, 57, 42, 255)     # #35392A, the "best run" card colour
GOLD = (242, 201, 76, 255)   # #F2C94C, the accent
BARS = (232, 232, 234, 255)  # #E8E8EA, the text colour

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SS = 8  # supersampling factor


def draw(size: int) -> Image.Image:
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = s / 32  # design units: the icon is laid out on a 32x32 grid

    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=7 * u, fill=BG)

    small = size <= 24
    ring = (3.2 if small else 2.4) * u
    cx, cy, r = 13.5 * u, 13.5 * u, (8.5 if small else 9) * u

    # Handle first, so the ring overlaps its end.
    hw = (4.2 if small else 3.6) * u
    d.line([(cx + r * 0.62, cy + r * 0.62), (27 * u, 27 * u)], fill=GOLD, width=round(hw))
    d.ellipse([27 * u - hw / 2, 27 * u - hw / 2, 27 * u + hw / 2, 27 * u + hw / 2], fill=GOLD)

    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=LENS, outline=GOLD, width=round(ring))

    if size >= 40:
        # A tiny bar chart inside the lens: the logs being looked at.
        bw, base = 2.0 * u, cy + 4.2 * u
        for x, h in ((cx - 4 * u, 3.5 * u), (cx - 1 * u, 7 * u), (cx + 2 * u, 5 * u)):
            d.rounded_rectangle([x, base - h, x + bw, base], radius=0.6 * u, fill=BARS)

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    project = Path(__file__).resolve().parent.parent
    images = [draw(n) for n in SIZES]
    images[-1].save(project / "app.ico", format="ICO", sizes=[(n, n) for n in SIZES],
                    append_images=images[:-1])
    print(f"Wrote {project / 'app.ico'} ({', '.join(map(str, SIZES))} px)")


if __name__ == "__main__":
    main()
