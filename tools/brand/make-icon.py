#!/usr/bin/env python3
"""Draws Aperture's mark (the same one as src/Aperture.Server/wwwroot/icon.svg: a Lamp ring
with a point of light, on a charcoal tile) into the Windows icon used by Aperture.exe,
its windows and the installer. Run from the repository root:

    python3 tools/brand/make-icon.py

Needs Pillow. The result is committed, so builds don't need Python."""
from PIL import Image, ImageDraw

SURFACE = (0x14, 0x15, 0x17, 255)
LAMP = (0xF2, 0xA9, 0x3B, 255)
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
OUT = "src/Aperture.App/Assets/Aperture.ico"


def draw(size: int) -> Image.Image:
    scale = 8  # draw large and shrink, so edges are smooth at every size
    s = size * scale
    u = s / 64  # the SVG's 64-unit grid
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    d.rounded_rectangle((0, 0, s - 1, s - 1), radius=14 * u, fill=SURFACE)
    # Small sizes get a slightly heavier ring so it doesn't fade to a smudge.
    stroke = (5 if size >= 32 else 6.5) * u
    r = 17 * u
    c = 32 * u
    d.ellipse((c - r - stroke / 2, c - r - stroke / 2, c + r + stroke / 2, c + r + stroke / 2), fill=LAMP)
    inner = r - stroke / 2
    d.ellipse((c - inner, c - inner, c + inner, c + inner), fill=SURFACE)
    dot = (4 if size >= 32 else 5) * u
    d.ellipse((c - dot, c - dot, c + dot, c + dot), fill=LAMP)
    return image.resize((size, size), Image.Resampling.LANCZOS)


def main() -> None:
    images = [draw(size) for size in SIZES]
    images[-1].save(OUT, format="ICO", sizes=[(i.width, i.height) for i in images], append_images=images[:-1])
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
