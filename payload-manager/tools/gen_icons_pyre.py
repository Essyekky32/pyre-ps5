#!/usr/bin/env python3
"""Generate the PYRE Payloads icons.

Deliberately the sibling of the PYRE Autoloader icon: the SAME flame silhouette,
inverted. Autoloader = ember flame on charcoal. Payloads = charcoal flame on
ember. Side by side on the XMB they read as one family and are never confused.

Drawn with Pillow so it needs no SVG rasteriser (this box has none).

Outputs:
  assets/icon0.png                 1024x1024 RGB   PS5 homescreen icon
  frontend/public/icon.png           512x512 RGBA
  frontend/public/icon-192.png       192x192 RGBA
  frontend/public/apple-touch-icon.png 180x180 RGBA
  frontend/public/favicon-32x32.png   32x32  RGBA
  frontend/public/favicon-16x16.png   16x16  RGBA
  frontend/public/favicon.svg         vector, transparent background

Run:  python tools/gen_icons_pyre.py
"""
import math
import os
import sys

try:
    from PIL import Image, ImageDraw, ImageFilter
except ImportError:
    sys.exit("Pillow is required: pip install pillow")

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))

ICON0 = os.path.join(ROOT, "assets", "icon0.png")
PUBLIC = os.path.join(ROOT, "frontend", "public")
WEB_ICONS = [
    ("icon.png", 512),
    ("icon-192.png", 192),
    ("apple-touch-icon.png", 180),
    ("favicon-32x32.png", 32),
    ("favicon-16x16.png", 16),
]
FAVICON_SVG = os.path.join(PUBLIC, "favicon.svg")

# PYRE palette
CHARCOAL = (12, 10, 9)
CHARCOAL_DEEP = (5, 3, 2)
BROWN = (42, 22, 8)
EMBER = (255, 122, 24)
EMBER_HOT = (255, 138, 46)
EMBER_DEEP = (209, 83, 0)
EMBER_LIGHT = (255, 174, 99)
CREAM = (246, 236, 227)

SS = 4  # supersample

# Same profile as the autoloader icon so the silhouettes match exactly.
FLAME_H = 0.540
FLAME_W = 0.152
BASE_BULGE = 0.50
NECK = 1.25
BULGE = 0.35


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def radial(size, inner, outer):
    img = Image.new("RGB", (size, size), outer)
    px = img.load()
    c = (size - 1) / 2.0
    maxd = math.hypot(c, c)
    for y in range(size):
        for x in range(size):
            d = math.hypot(x - c, y - c) / maxd
            t = min(1.0, d / 0.80)
            px[x, y] = lerp(inner, outer, t * t)
    return img


def flame_path(cx, tip_y, height, width, n=56):
    shoulder_y = tip_y + height
    right = []
    for i in range(n + 1):
        t = i / n
        half = width * (t ** NECK) * (1.0 + BULGE * math.sin(math.pi * (t ** 1.2)))
        drift = width * 0.04 * math.sin(t * math.pi * 0.9)
        right.append((cx + half + drift, tip_y + t * height))
    base = []
    steps = max(10, n // 2)
    for i in range(1, steps):
        a = math.pi * (i / float(steps))
        base.append((cx + width * math.cos(a),
                     shoulder_y + width * BASE_BULGE * math.sin(a)))
    left = [(2 * cx - x, y) for (x, y) in reversed(right)]
    return right + base + left


def geo(size):
    cx = size / 2.0
    h = size * FLAME_H
    w = size * FLAME_W
    total = h + w * BASE_BULGE
    return cx, (size - total) / 2.0, h, w


def draw(size, with_background=True):
    """Ember field, charcoal flame. RGBA."""
    if with_background:
        bg = radial(size, EMBER_HOT, EMBER_DEEP).convert("RGBA")
    else:
        bg = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    cx, tip_y, h, w = geo(size)

    # soft dark bloom behind the flame so it sits in the field rather than on top
    glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.polygon(flame_path(cx, tip_y - h * 0.02, h * 1.05, w * 1.20),
               fill=CHARCOAL + (120,))
    glow = glow.filter(ImageFilter.GaussianBlur(size * 0.035))
    bg = Image.alpha_composite(bg, glow)

    fd = ImageDraw.Draw(bg)
    fd.polygon(flame_path(cx, tip_y, h, w), fill=CHARCOAL + (255,))
    fd.polygon(flame_path(cx, tip_y + h * 0.24, h * 0.58, w * 0.54),
               fill=BROWN + (255,))
    fd.polygon(flame_path(cx, tip_y + h * 0.44, h * 0.26, w * 0.24),
               fill=EMBER_LIGHT + (255,))
    return bg


def flame_svg(size=64):
    cx, tip_y, h, w = geo(size)

    def pts(tip, hh, ww):
        p = flame_path(cx, tip, hh, ww, n=48)
        return " ".join("%.1f,%.1f" % (x, y) for (x, y) in p)

    parts = [
        '<rect width="%d" height="%d" rx="%.1f" fill="#d15300"/>' % (size, size, size * 0.18),
        '<polygon points="%s" fill="#0c0a09"/>' % pts(tip_y, h, w),
        '<polygon points="%s" fill="#2a1608"/>' % pts(tip_y + h * 0.24, h * 0.58, w * 0.54),
        '<polygon points="%s" fill="#ffae63"/>' % pts(tip_y + h * 0.44, h * 0.26, w * 0.24),
    ]
    return ('<?xml version="1.0" encoding="UTF-8"?>\n'
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d">\n'
            "  %s\n</svg>\n" % (size, size, "\n  ".join(parts)))


def main():
    # PS5 homescreen icon: 1024x1024, RGB like the original.
    big = draw(1024 * 1)  # drawn at target size; see supersample note below
    big = big.resize((1024, 1024), Image.LANCZOS)
    big.convert("RGB").save(ICON0, "PNG")

    # Web icons. Render once at high res then downscale per size for clean edges.
    master = draw(512 * SS).resize((512, 512), Image.LANCZOS)
    for name, size in WEB_ICONS:
        out = master.resize((size, size), Image.LANCZOS)
        out.save(os.path.join(PUBLIC, name), "PNG")

    with open(FAVICON_SVG, "w", encoding="utf-8") as f:
        f.write(flame_svg(64))

    print("Generated PYRE Payloads icons:")
    for path in [ICON0] + [os.path.join(PUBLIC, n) for n, _ in WEB_ICONS] + [FAVICON_SVG]:
        print("  %s (%d bytes)" % (os.path.relpath(path, ROOT), os.path.getsize(path)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
