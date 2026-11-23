"""
Draws the Store package's tile and icon images from Overskin's logo (src/Overskin/Assets/logo.svg).

    python src/Store/make-images.py

Writes PNGs into src/Store/Images. Needs Pillow. The logo is two simple shapes, so they are drawn
here directly (4x oversampled, then scaled down) instead of needing an SVG renderer.
"""
import math
import os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "Images")
OVERSAMPLE = 4


def sheet(x0, y0, x1, y1, big, small, steps=24):
    """A leaf-shaped sheet: big rounded top-left and bottom-right corners, small ones elsewhere."""
    pts = []

    def arc(cx, cy, r, a0, a1):
        for i in range(steps + 1):
            a = math.radians(a0 + (a1 - a0) * i / steps)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))

    arc(x0 + big, y0 + big, big, 180, 270)      # top-left
    arc(x1 - small, y0 + small, small, 270, 360)  # top-right
    arc(x1 - big, y1 - big, big, 0, 90)          # bottom-right
    arc(x0 + small, y1 - small, small, 90, 180)  # bottom-left
    return pts


def logo(size):
    """The logo on a transparent square, 256 units across like the SVG."""
    s = size * OVERSAMPLE
    k = s / 256.0
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    scale = lambda pts: [(x * k, y * k) for x, y in pts]

    # back sheet: an outline 16 wide, drawn as a filled ring (outer edge, then the inside cleared)
    d.polygon(scale(sheet(20, 20, 172, 172, 56, 16)), fill="#A9B4C6")
    d.polygon(scale(sheet(36, 36, 156, 156, 40, 0.01)), fill=(0, 0, 0, 0))
    # front sheet: blue, with a dark 6-unit edge outside it (stroke 12 painted under the fill)
    d.polygon(scale(sheet(86, 86, 234, 234, 54, 14)), fill="#121316")
    d.polygon(scale(sheet(92, 92, 228, 228, 48, 8)), fill="#4F8CFF")
    return im.resize((size, size), Image.LANCZOS)


def tile(width, height, fraction):
    """The logo centred on a transparent tile, filling `fraction` of the shorter side."""
    im = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    side = max(1, round(min(width, height) * fraction))
    mark = logo(side)
    im.alpha_composite(mark, ((width - side) // 2, (height - side) // 2))
    return im


def save(im, name):
    im.save(os.path.join(OUT, name))


def main():
    os.makedirs(OUT, exist_ok=True)
    scales = {100: 1.0, 125: 1.25, 150: 1.5, 200: 2.0, 400: 4.0}
    for pct, f in scales.items():
        save(tile(round(44 * f), round(44 * f), 0.84), f"Square44x44Logo.scale-{pct}.png")
        save(tile(round(150 * f), round(150 * f), 0.56), f"Square150x150Logo.scale-{pct}.png")
        save(tile(round(310 * f), round(150 * f), 0.56), f"Wide310x150Logo.scale-{pct}.png")
        save(tile(round(50 * f), round(50 * f), 0.9), f"StoreLogo.scale-{pct}.png")
        save(tile(round(620 * f), round(300 * f), 0.5), f"SplashScreen.scale-{pct}.png")
    # taskbar, Start list and file icons: exact sizes, plated and unplated
    for ts in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256):
        save(tile(ts, ts, 1.0), f"Square44x44Logo.targetsize-{ts}.png")
        save(tile(ts, ts, 1.0), f"Square44x44Logo.targetsize-{ts}_altform-unplated.png")
        save(tile(ts, ts, 1.0), f"Square44x44Logo.targetsize-{ts}_altform-lightunplated.png")
    print("images written to", OUT)


if __name__ == "__main__":
    main()
