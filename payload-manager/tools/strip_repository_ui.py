"""Remove the install-from-list UI from StorageHub.jsx.

Two deletions, both verified against anchor lines before anything is written:
  A. the whole <section id="cloud-repository"> block (the repository catalogue)
  B. the toolbar controls that only served it: search, sort, refresh
     The Upload control is deliberately KEPT - it is the replacement path.

Assertions first, then delete bottom-up so earlier ranges keep their numbering.
A backup is written next to the original before any change.
"""
import io
import os
import shutil
import sys

PATH = r"D:\Projects\pyre-pldmgr-src\frontend\src\components\views\StorageHub.jsx"
BACKUP = PATH + ".bak-before-repo-removal"

with io.open(PATH, "r", encoding="utf-8", newline="") as f:
    text = f.read()

lines = text.split("\n")          # keep line endings via join
orig_count = len(lines)
print("lines before: %d" % orig_count)


def line(n):
    """1-indexed accessor."""
    return lines[n - 1]


# ---- anchors (1-indexed, inclusive) -------------------------------------
checks = [
    (505, "Cloud Repository Section", True),
    (506, "cloud-repository", True),
    (701, "</section>", True),
    (703, "USB Storage Section", True),
    (399, "space-x-2", True),
    (400, "isSearchOpen", True),
    (493, "{!isPS5 && (", True),   # the Upload control we keep (opens here)
    (499, "onUpload", True),       # ...and the input inside it
]

ok = True
for n, needle, _ in checks:
    got = line(n)
    if needle not in got:
        print("  FAIL anchor line %d: expected %r, got %r" % (n, needle, got.strip()[:80]))
        ok = False

if not ok:
    sys.exit("anchors do not match - refusing to edit (file already changed, or line numbers moved)")

print("all anchors matched")

# ---- delete bottom-up ---------------------------------------------------
# A: lines 505..702  (the repository section + its trailing blank line)
del lines[504:702]
# B: lines 400..492  (search conditional + sort + refresh; leaves the Upload control)
del lines[399:492]

if not os.path.exists(BACKUP):
    shutil.copyfile(PATH, BACKUP)
    print("backup written: %s" % os.path.basename(BACKUP))

with io.open(PATH, "w", encoding="utf-8", newline="") as f:
    f.write("\n".join(lines))

print("lines after:  %d (removed %d)" % (len(lines), orig_count - len(lines)))
print()
print("context around the seam (should be testId toolbar -> Upload control):")
for i in range(395, 404):
    print("  %4d: %s" % (i, lines[i - 1].rstrip()[:100]))
