"""Inventory: what payloads each app offers.

Reads D:\\Projects\\PYRE\\haze-catalog-data.js (what Pyre.exe lists) and prints
a clean table. Kept as a file because inline python via PowerShell -c mangles
quotes.
"""
import json
import re

CAT = r"D:\Projects\PYRE\haze-catalog-data.js"

src = open(CAT, encoding="utf-8").read()

# The file is `export const payloads = [ {..}, {..} ];` - pull the array, then
# parse each object leniently (there are // comments, so no json.loads on the
# whole thing).
start = src.index("[")
end = src.rindex("]") + 1
body = src[start:end]

objs = []
depth = 0
cur = ""
for ch in body:
    if ch == "{":
        depth += 1
    if depth > 0:
        cur += ch
    if ch == "}":
        depth -= 1
        if depth == 0 and cur:
            objs.append(cur)
            cur = ""

print("PYRE payload catalogue  (haze-catalog-data.js)")
print("%-12s %-38s %-12s %10s  %s" % ("id", "title", "version", "bytes", "file"))
print("-" * 110)

rows = []
for o in objs:
    def g(k):
        m = re.search(r'"%s"\s*:\s*"([^"]*)"' % k, o)
        return m.group(1) if m else ""
    def n(k):
        m = re.search(r'"%s"\s*:\s*(\d+)' % k, o)
        return int(m.group(1)) if m else 0
    rows.append((g("id"), g("title"), g("version"), n("bytes"), g("file")))

for r in rows:
    print("%-12s %-38s %-12s %10d  %s" % (r[0], r[1][:38], r[2], r[3], r[4]))

print()
print("total: %d payloads" % len(rows))
print()
print("our two:")
for r in rows:
    if "pyre-" in r[4]:
        print("  %-12s %-38s %s  %d B" % (r[0], r[1], r[4], r[3]))
