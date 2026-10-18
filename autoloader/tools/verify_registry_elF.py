"""Definitive check: are our edited files in the ELF?

Instead of re-deriving the compressed bytes, this reads them straight out of
include/file_registry.c -- the generated C file that was actually compiled --
maps each array to its registry path via the entries table, and searches the
linked ELF for those exact bytes.

That removes every possible discrepancy in my own reproduction: if the bytes
in the compiled source are in the ELF, the file is in the artifact.
"""
import os
import re
import zlib

ROOT = r"D:\Projects\pyre-autoloader-src"
REG_C = os.path.join(ROOT, "include", "file_registry.c")
ELF = os.path.join(ROOT, "installer.elf")

blob = open(ELF, "rb").read()
src = open(REG_C, "r", encoding="utf-8", errors="replace").read()
print("ELF %d bytes, registry source %d bytes" % (len(blob), len(src)))

arrays = {}
for m in re.finditer(r"file_(\d+)\s*\[\s*\]\s*=\s*\{(.*?)\}\s*;", src, re.S):
    idx = int(m.group(1))
    vals = re.findall(r"0x([0-9a-fA-F]{1,2})", m.group(2))
    arrays[idx] = bytes(int(v, 16) for v in vals)
print("arrays parsed:", len(arrays))

entries = []
for m in re.finditer(
        r'\{\s*"([^"]+)"\s*,\s*file_(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*"([^"]*)"\s*\}',
        src):
    entries.append((m.group(1), int(m.group(2)), int(m.group(3)), int(m.group(4)),
                    m.group(5) == "1", m.group(6)))
print("table entries:", len(entries))

WANT = ("app.js", "style.css", "index.html", "favicon.svg", "logo.svg",
        "cache.appcache", "elfldr-ps5.elf", "payload.elf", "kexp-ps5.bin")

print()
print("%-44s %8s %6s %s" % ("registry path", "bytes", "comp", "in ELF?"))
missing = []
for path, fidx, stored_size, orig_size, comp, ctype in entries:
    if path.rsplit("/", 1)[-1] not in WANT:
        continue
    data = arrays.get(fidx, b"")
    if len(data) != stored_size:
        print("%-44s %8d %6s ARRAY LEN MISMATCH (%d)" % (path, stored_size, comp, len(data)))
        continue
    hit = blob.find(data)
    if hit < 0:
        missing.append(path)
    print("%-44s %8d %6s %s" % (
        path, stored_size, comp, ("yes @0x%x" % hit) if hit >= 0 else "*** NO ***"))

print()
print("=== verdict ===")
print("NOT FOUND IN ELF: %s" % missing if missing else
      "every sampled registry entry is present in the ELF")

print()
print("=== decompressing the app.js entry that the ELF carries ===")
js = None
js_path = None
for path, fidx, stored_size, orig_size, comp, ctype in entries:
    if path.endswith("/app.js"):
        raw = arrays[fidx]
        if comp:
            d = zlib.decompressobj(-15)
            js = d.decompress(raw) + d.flush()
        else:
            js = raw
        js_path = path
        print("entry %s  stored=%d  inflated=%d" % (path, stored_size, len(js)))
        break

if js is None:
    print("*** no app.js entry found in the registry ***")
else:
    checks = [
        ("EXPLOIT_MODE token gone", b"[[EXPLOIT_MODE]]", False),
        ("EXPLOIT_MODE concrete", b"var EXPLOIT_MODE = 'auto'", True),
        ("armed via armExploit", b"armExploit(exploitUrl)", True),
        ("old immediate arm gone", b"exploitEl.src = exploitUrl;", False),
        ("delay default 5", b"ARM_DELAY_DEFAULT_S = 5", True),
        ("old 30s default gone", b"ARM_DELAY_DEFAULT_S = 30", False),
        ("localStorage key", b"pyre_arm_delay_ms", True),
        ("re-arm path untouched", b"exploitEl.src = POOPS_URL", True),
        ("Arm now control", b"delayNow", True),
        ("countdown label", b"Arming in ", True),
        ("initDelayControls called", b"initDelayControls();", True),
        ("branding: PYRE by Essy", b"PYRE by Essy", True),
        ("branding: old byline gone", b"WebKit Autoloader by PLK", False),
    ]
    bad = []
    for label, needle, expected in checks:
        got = needle in js
        ok = got == expected
        if not ok:
            bad.append(label)
        print("  %-26s %s (expected %s)" % (
            label, "yes" if got else "no", "present" if expected else "absent"))
    print()
    print("js checks: %s" % ("ALL PASS" if not bad else "FAILED: %s" % bad))
