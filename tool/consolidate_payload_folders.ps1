# Consolidate to TWO payload folders and keep the root clean.
#
#   payloads/      = the RUN PYRE chain        (name fixed by frozen site code)
#   our-payloads/  = everything we ship ourselves
#
# pyre-payloads/ was an invented name; it is merged away. pldmgr moves out of the
# root into our-payloads/. Then the generated files are rebuilt from disk.
$ErrorActionPreference = 'Stop'
$PYRE = 'D:\Projects\PYRE'
$OLD  = Join-Path $PYRE 'pyre-payloads'
$DEST = Join-Path $PYRE 'our-payloads'
function Hash([string]$p) { (Get-FileHash $p -Algorithm SHA256).Hash.ToLower() }

Write-Output "=== move pyre-payloads\* -> our-payloads\ ==="
if (Test-Path $OLD) {
  Get-ChildItem $OLD -File | ForEach-Object {
    $to = Join-Path $DEST $_.Name
    if (Test-Path $to) { throw "refusing: $($_.Name) already exists in our-payloads" }
    Move-Item $_.FullName -Destination $to
    Write-Output ("  {0,-38} {1,10} B" -f $_.Name, $_.Length)
  }
  Remove-Item $OLD -Force
  Write-Output "  removed empty pyre-payloads\"
} else {
  Write-Output "  (already gone)"
}

Write-Output ""
Write-Output "=== move pldmgr out of the root ==="
Get-ChildItem $PYRE -Filter 'pyre-payloads_pldmgr_*.elf' -File | ForEach-Object {
  $to = Join-Path $DEST $_.Name
  Move-Item $_.FullName -Destination $to -Force
  Write-Output ("  {0} -> our-payloads\" -f $_.Name)
}
Get-ChildItem $PYRE -Filter 'pyre-payloads_pldmgr_*.elf.json' -File -ErrorAction SilentlyContinue | ForEach-Object {
  Move-Item $_.FullName -Destination (Join-Path $DEST $_.Name) -Force
  Write-Output ("  {0} -> our-payloads\" -f $_.Name)
}

Write-Output ""
Write-Output "=== our-payloads\ now holds ==="
Get-ChildItem $DEST -File | Sort-Object Length -Descending | ForEach-Object { "{0,12:N0}  {1}" -f $_.Length, $_.Name }

Write-Output ""
Write-Output "=== root .elf count (expect 0) ==="
(Get-ChildItem $PYRE -Filter '*.elf' -File | Measure-Object).Count
