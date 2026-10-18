"""Swap the cold navy palette for the PYRE palette on the pointer and
installer pages, so nothing flashes blue before/while the autoloader loads.

Colour-only: no markup, no tokens, no logic. Functional status colours
(green success, red failure, amber warning) are deliberately left alone --
they are a signal, not branding.

Preserves line endings and encoding exactly: the file is read as text, only
these literal substrings are replaced, and it is written back with the same
UTF-8 encoding and no BOM.
"""
import io
import os
import re

ROOT = r"D:\Projects\PYRE\wkali-build\frontend"

# Cold navy -> warm charcoal / ember. Order does not matter: every key is a
# distinct 6-digit literal and none is a prefix of another.
MAP = {
    "#0f172a": "#0c0a09",   # page background
    "#0b1220": "#150f0c",   # background gradient end
    "#1b2436": "#1a1009",   # panel background
    "#22304a": "#2a1608",   # panel background (alt)
    "#2c3e5a": "#3a1d0a",   # borders
    "#334155": "#3a1d0a",   # borders (alt)
    "#2563eb": "#ff7a18",   # primary accent -> ember
    "#38bdf8": "#ffae63",   # light accent -> light ember
    "#7dd3fc": "#ffcda1",   # lighter accent -> warm peach
    "#e2e8f0": "#ecd9c8",   # body text -> cream
    "#cbd5e1": "#ecd9c8",   # body text (alt) -> cream
    "#94a3b8": "#c9a893",   # muted text -> warm muted
    "#4b5563": "#5c3620",   # dimmest text -> brown
    "#f87171": "#ffb4ad",   # failure text -> palette rose
}

FILES = [
    os.path.join(ROOT, "pointer", "index.html"),
    os.path.join(ROOT, "installer-page", "index.html"),
]

for path in FILES:
    with io.open(path, "r", encoding="utf-8", newline="") as f:
        text = f.read()
    original = text
    counts = {}
    for old, new in MAP.items():
        # case-insensitive so a stray uppercase hex is still caught
        pattern = re.compile(re.escape(old), re.IGNORECASE)
        text, n = pattern.subn(new, text)
        if n:
            counts[old] = n
    if text != original:
        with io.open(path, "w", encoding="utf-8", newline="") as f:
            f.write(text)
    print("%s: %d substitutions" % (os.path.basename(os.path.dirname(path)) + "/" +
                                    os.path.basename(path), sum(counts.values())))
    for k in sorted(counts):
        print("    %s -> %s  x%d" % (k, MAP[k], counts[k]))

print()
print("=== any cold hexes left anywhere in these two files? ===")
leftovers = ["#0f172a", "#0b1220", "#1b2436", "#22304a", "#2c3e5a", "#334155",
             "#2563eb", "#38bdf8", "#7dd3fc", "#e2e8f0", "#cbd5e1", "#94a3b8",
             "#4b5563", "#f87171", "#5b7fa6", "#1e3a5f"]
bad = False
for path in FILES:
    text = io.open(path, "r", encoding="utf-8").read().lower()
    for h in leftovers:
        if h in text:
            print("  STILL PRESENT in %s: %s" % (os.path.basename(os.path.dirname(path)), h))
            bad = True
print("  none" if not bad else "  ^ needs another pass")
