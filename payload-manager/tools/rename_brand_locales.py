"""Rename the app's own name across every locale.

"Payload Manager" is kept as an untranslated proper noun in every locale that
mentions it (de: "Payload Manager wird nicht ausgeführt", fr: "Payload Manager
n'est pas lancé"), so a literal replace is correct here -- this is a brand
rename, not a translation.

Validates the JSON of every file it touches, and reports the nav-label values
that are TRANSLATED words rather than the brand, so those can be judged
separately instead of blindly overwritten.
"""
import io
import json
import os

LOC = r"D:\Projects\pyre-pldmgr-src\frontend\src\locales"
OLD = "Payload Manager"
NEW = "PYRE Payloads"

total = 0
touched = []
for name in sorted(os.listdir(LOC)):
    if not name.endswith(".json"):
        continue
    path = os.path.join(LOC, name)
    with io.open(path, "r", encoding="utf-8") as f:
        text = f.read()
    n = text.count(OLD)
    if n:
        text = text.replace(OLD, NEW)
        # validate before writing
        json.loads(text)
        with io.open(path, "w", encoding="utf-8", newline="") as f:
            f.write(text)
        touched.append((name, n))
        total += n

print("brand rename: %d replacements across %d locale files" % (total, len(touched)))
for name, n in touched:
    print("  %-14s %d" % (name, n))

print()
print("=== nav labels that are TRANSLATED words, not the brand ===")
print("(these need a human decision - they say 'get/fetch payloads' in their own language)")
for name in ("en-US.json", "de-DE.json", "fr-FR.json", "es-ES.json", "it-IT.json",
             "nl-NL.json", "pt-BR.json", "ja-JP.json", "zh-CN.json", "ru-RU.json"):
    path = os.path.join(LOC, name)
    if not os.path.exists(path):
        continue
    d = json.load(io.open(path, encoding="utf-8"))
    keys = []
    for k in ("app.nav.storage", "app.nav.manage_mobile", "storage_hub.title_1",
              "storage_hub.title_2", "app.offline.title"):
        if k in d:
            keys.append("%s=%r" % (k.split(".")[-1], d[k]))
    print("  %-12s %s" % (name, "  ".join(keys)))

print()
print("=== verify every locale still parses ===")
bad = []
for name in sorted(os.listdir(LOC)):
    if not name.endswith(".json"):
        continue
    try:
        json.load(io.open(os.path.join(LOC, name), encoding="utf-8"))
    except Exception as e:
        bad.append((name, str(e)))
print("  all %d parse OK" % len([n for n in os.listdir(LOC) if n.endswith('.json')])
      if not bad else "  BROKEN: %s" % bad)
