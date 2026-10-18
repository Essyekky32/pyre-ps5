#!/usr/bin/env bash
# Build PYRE Payloads: substitute the page title/build date exactly as the
# Makefile's frontend-build target does, then compile the ELF.
#
# Why not `make frontend-build`: that runs `npm install && npm run build` in
# WSL, and the node_modules were installed on Windows (win32 esbuild binaries).
# Reinstalling under Linux would swap those out. The frontend was already built
# on Windows with the PYRE theme; this only does the two sed substitutions the
# Makefile performs after the vite build.
set -euo pipefail

cd /mnt/d/Projects/pyre-pldmgr-src

VERSION=$(grep '#define MENU_VERSION' include/pldmgr.h | awk '{print $3}' | tr -d '"')
DATE=$(date -u +"%Y-%m-%d %H:%M:%S UTC")
TITLE="PYRE Payloads by Essy (built at $DATE)"

echo "version : $VERSION"
echo "title   : $TITLE"

# index.html carries [[TITLE_PLACEHOLDER]] in <title>
if grep -q '\[\[TITLE_PLACEHOLDER\]\]' frontend/dist/index.html; then
  tmp=$(mktemp)
  sed "s|\[\[TITLE_PLACEHOLDER\]\]|$TITLE|g" frontend/dist/index.html > "$tmp"
  mv "$tmp" frontend/dist/index.html
  echo "title substituted"
else
  echo "WARNING: [[TITLE_PLACEHOLDER]] not found (already substituted?)"
fi

# cache.appcache carries [[BUILD_DATE]]
if grep -q '\[\[BUILD_DATE\]\]' frontend/dist/cache.appcache; then
  tmp=$(mktemp)
  sed "s|\[\[BUILD_DATE\]\]|$DATE|g" frontend/dist/cache.appcache > "$tmp"
  mv "$tmp" frontend/dist/cache.appcache
  echo "build date substituted"
fi

# Prove the substitution landed before compiling.
echo "--- resulting <title> ---"
grep -o '<title>[^<]*</title>' frontend/dist/index.html | head -1

echo "--- make ---"
make
echo "--- result ---"
ls -l pldmgr.elf
