#!/usr/bin/env python3
"""Generate the PYRE-themed icon assets for the autoloader.

This is a drop-in replacement for tools/gen_icons.py that needs neither
rsvg-convert nor the SDK docker image: the art is drawn directly with Pillow,
so it runs anywhere Pillow does.

Why it exists: the upstream art is a cold navy sphere (#0e182b -> #060a13),
which does not match the PYRE desktop app at all. This draws a pyre: an ember
flame on warm charcoal, using the palette lifted out of PYRE's WPF source.

Outputs (same paths as gen_icons.py, so the Makefile's `icons` target is
satisfied without ever calling rsvg-convert):
  assets/icon0.png                        512x512  PS5 homescreen icon
  assets/icon.ico                         multi-res Windows icon
  frontend/installer-page/favicon.svg     padded + dark background
  frontend/autoloader/favicon.svg         padded + dark background
  frontend/installer-page/logo.svg        raw art, transparent background
  frontend/autoloader/logo.svg            raw art, transparent background

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
ICON_ICO = os.path.join(ROOT, "assets", "icon.ico")
FAVICON_INSTALLER = os.path.join(ROOT, "frontend", "installer-page", "favicon.svg")
FAVICON_AUTOLOADER = os.path.join(ROOT, "frontend", "autoloader", "favicon.svg")
LOGO_INSTALLER = os.path.join(ROOT, "frontend", "installer-page", "logo.svg")
LOGO_AUTOLOADER = os.path.join(ROOT, "frontend", "autoloader", "logo.svg")

# PYRE palette, lifted from the desktop app's XAML.
CHARCOAL_DEEP = (5, 3, 2)
CHARCOAL = (12, 10, 9)
CHARCOAL_WARM = (26, 16, 9)
BROWN = (58, 29, 10)
EMBER = (255, 122, 24)
EMBER_LIGHT = (255, 174, 99)
CREAM = (246, 236, 227)

SIZE = 512
SS = 4  # supersample factor: draw big, downscale, get clean edges
ICO_SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]

# Geometry as fractions of the canvas.
# The silhouette is a droplet, not a cone: from a fine tip the sides widen
# with a CONCAVE neck (fast initial flare, then easing), reach full width
# ~82% down, and the base closes as a round cap. Straight sides read as a
# traffic cone; the concave neck plus the round base is what reads as fire.
FLAME_H = 0.540        # tip-to-shoulder height
FLAME_W = 0.152        # half-width at the shoulder
BASE_BULGE = 0.50      # base cap depth, as a multiple of FLAME_W
NECK = 1.25            # >1 keeps the tip narrow (concave upper side)
BULGE = 0.35           # adds the outward flare low down
SHOULDER = 1.0
RING_R = 0.435


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def radial_background(size, inner, outer):
    """Warm radial gradient: lighter at the centre, falling off to the edge."""
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


def bezier(p0, p1, p2, p3, n=64):
    pts = []
    for i in range(n + 1):
        t = i / n
        mt = 1.0 - t
        x = (mt ** 3) * p0[0] + 3 * mt * mt * t * p1[0] + 3 * mt * t * t * p2[0] + (t ** 3) * p3[0]
        y = (mt ** 3) * p0[1] + 3 * mt * mt * t * p1[1] + 3 * mt * t * t * p2[1] + (t ** 3) * p3[1]
        pts.append((x, y))
    return pts


def flame_path(cx, tip_y, height, width, n=56):
    """Closed droplet outline, clockwise: tip -> right -> round base -> left.

    `height` is tip to shoulder; the base cap then extends a little further
    down by width * BASE_BULGE.
    """
    shoulder_y = tip_y + height

    right = []
    for i in range(n + 1):
        t = i / n
        # t**NECK keeps the tip fine and gives the upper side a concave sweep;
        # the sine term then flares it outward low down so the body bulges.
        half = width * (t ** NECK) * (1.0 + BULGE * math.sin(math.pi * (t ** 1.2)))
        # a whisper of drift so the outline is organic rather than a lathe
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


def geo(size, cx=None):
    """Centre-x, tip-y, height and half-width for a flame of this canvas."""
    if cx is None:
        cx = size / 2.0
    h = size * FLAME_H
    w = size * FLAME_W
    total = h + w * BASE_BULGE
    tip_y = (size - total) / 2.0
    return cx, tip_y, h, w


def draw_flame(size, with_background=True, ring=True):
    """Render the pyre mark. Returns an RGBA image."""
    if with_background:
        bg = radial_background(size, CHARCOAL_WARM, CHARCOAL_DEEP).convert("RGBA")
    else:
        bg = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    cx, tip_y, h, w = geo(size)

    # Outer glow: the flame drawn fat and blurred, then composited underneath.
    glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.polygon(flame_path(cx, tip_y - h * 0.03, h * 1.06, w * 1.16), fill=EMBER + (170,))
    glow = glow.filter(ImageFilter.GaussianBlur(size * 0.030))
    bg = Image.alpha_composite(bg, glow)

    # Ring: echoes the upstream art's circle, in warm brown so it frames the
    # flame instead of boxing it in.
    if ring:
        rd = ImageDraw.Draw(bg)
        r = size * RING_R
        rd.ellipse([cx - r, cx - r, cx + r, cx + r],
                   outline=BROWN + (200,), width=max(2, int(size * 0.0075)))

    fd = ImageDraw.Draw(bg)
    fd.polygon(flame_path(cx, tip_y, h, w), fill=EMBER + (255,))
    fd.polygon(flame_path(cx, tip_y + h * 0.22, h * 0.62, w * 0.56),
               fill=EMBER_LIGHT + (255,))
    fd.polygon(flame_path(cx, tip_y + h * 0.40, h * 0.30, w * 0.26),
               fill=CREAM + (225,))

    return bg


def flame_svg(size, with_background=True, ring=True):
    """The same mark as an SVG, for favicons and in-page logos."""
    cx, tip_y, h, w = geo(size)

    def pts(cx_, tip_y_, height_, width_):
        path = flame_path(cx_, tip_y_, height_, width_, n=48)
        return " ".join("%.1f,%.1f" % (x, y) for (x, y) in path)

    parts = []
    if with_background:
        parts.append(
            '<defs><radialGradient id="pyreBg" cx="50%" cy="50%" r="80%">'
            '<stop offset="0%" stop-color="#1a1009"/>'
            '<stop offset="100%" stop-color="#050302"/>'
            "</radialGradient></defs>"
        )
        parts.append('<rect width="%d" height="%d" fill="url(#pyreBg)"/>' % (size, size))
    if ring:
        parts.append(
            '<circle cx="%.1f" cy="%.1f" r="%.1f" fill="none" stroke="#3a1d0a" stroke-width="%.1f"/>'
            % (cx, cx, size * RING_R, max(2.0, size * 0.0075))
        )
    parts.append('<polygon points="%s" fill="#ff7a18"/>' % pts(cx, tip_y, h, w))
    parts.append('<polygon points="%s" fill="#ffae63"/>'
                 % pts(cx, tip_y + h * 0.22, h * 0.62, w * 0.56))
    parts.append('<polygon points="%s" fill="#f6ece3" opacity="0.90"/>'
                 % pts(cx, tip_y + h * 0.40, h * 0.30, w * 0.26))

    return ('<?xml version="1.0" encoding="UTF-8"?>\n'
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d">\n'
            "  %s\n</svg>\n" % (size, size, "\n  ".join(parts)))


def main():
    img = draw_flame(SIZE * SS)
    img = img.resize((SIZE, SIZE), Image.LANCZOS)
    img.convert("RGB").save(ICON0, "PNG")

    img.save(ICON_ICO, format="ICO", sizes=ICO_SIZES)

    fav = flame_svg(1024, with_background=True, ring=True)
    for path in (FAVICON_INSTALLER, FAVICON_AUTOLOADER):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(fav)

    logo = flame_svg(1024, with_background=False, ring=False)
    for path in (LOGO_INSTALLER, LOGO_AUTOLOADER):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(logo)

    print("Generated PYRE icon assets:")
    for path in (ICON0, ICON_ICO, FAVICON_INSTALLER, FAVICON_AUTOLOADER,
                 LOGO_INSTALLER, LOGO_AUTOLOADER):
        print("  %s (%d bytes)" % (os.path.relpath(path, ROOT), os.path.getsize(path)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
