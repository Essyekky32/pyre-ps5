#!/usr/bin/env bash
# Verify the PYRE Payloads rebuild: repository UI removed, naming fixed.
cd /mnt/d/Projects/pyre-pldmgr-src || exit 1

echo "=== ELF ==="
ls -l pldmgr.elf
sha256sum pldmgr.elf

echo
echo "=== expect 0 (removed / stale) ==="
for s in "PLDMGR" "Payload Manager is not running" "Refresh Repository"; do
  printf '  %-32s %s\n' "$s" "$(strings -a pldmgr.elf | grep -c -- "$s")"
done

echo
echo "=== expect >=1 (new) ==="
for s in "PYRE Payloads is not running" "Upload ELF Payload" "USB Storage"; do
  printf '  %-32s %s\n' "$s" "$(strings -a pldmgr.elf | grep -c -- "$s")"
done

echo
echo "=== title / XMB name ==="
strings -a pldmgr.elf | grep -o "PYRE Payloads by Essy[^<]*" | head -1
strings -a pldmgr.elf | grep -o 'titleName[^,]*' | head -1

echo
echo "=== theme still present ==="
printf '  #ff7a18 occurrences: %s\n' "$(strings -a pldmgr.elf | grep -c '#ff7a18')"
printf '  #0095ff occurrences (expect 0): %s\n' "$(strings -a pldmgr.elf | grep -c '#0095ff')"
