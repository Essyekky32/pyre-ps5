# Restructure PYRE's payload set to Cam's real console set.
# Step 1 of 3: BACK UP, then stage the new files. Nothing destructive yet.
$ErrorActionPreference = 'Stop'
$PYRE   = 'D:\Projects\PYRE'
$STAGE  = 'D:\Projects\PYRE-payloads-staging'
$BACKUP = 'D:\Projects\_backup\PYRE_payloadset_2026-10-17'
$NEWDIR = Join-Path $PYRE 'pyre-payloads'

Write-Output "=== 1. BACKUP ==="
New-Item -ItemType Directory -Path $BACKUP -Force | Out-Null
# the things this change touches
$touch = @('haze-catalog-data.js','payload-sources.json','additional.html','_FROZEN-MANIFEST.txt','README-PYRE.md')
foreach ($f in $touch) {
  $src = Join-Path $PYRE $f
  if (Test-Path $src) { Copy-Item $src -Destination (Join-Path $BACKUP $f) -Force }
}
# every .elf currently at the PYRE root, and the sequence dir
New-Item -ItemType Directory -Path (Join-Path $BACKUP 'root-elfs') -Force | Out-Null
Get-ChildItem $PYRE -Filter '*.elf' -File | ForEach-Object { Copy-Item $_.FullName -Destination (Join-Path $BACKUP 'root-elfs') -Force }
New-Item -ItemType Directory -Path (Join-Path $BACKUP 'payloads') -Force | Out-Null
Get-ChildItem (Join-Path $PYRE 'payloads') -File | ForEach-Object { Copy-Item $_.FullName -Destination (Join-Path $BACKUP 'payloads') -Force }
$nb = (Get-ChildItem $BACKUP -Recurse -File | Measure-Object Length -Sum)
Write-Output ("  backed up {0} files, {1} bytes -> {2}" -f $nb.Count, $nb.Sum, $BACKUP)

Write-Output ""
Write-Output "=== 2. STAGE the new payload set into pyre-payloads\ ==="
New-Item -ItemType Directory -Path $NEWDIR -Force | Out-Null

# his console payloads + OmniPad b98 from his desktop
$map = @(
  @{ from = (Join-Path $STAGE 'etaHEN-13.60.elf');                as = 'etaHEN-13.60.elf' },
  @{ from = (Join-Path $STAGE 'CheatRunner_v0.17.2.elf');         as = 'CheatRunner_v0.17.2.elf' },
  @{ from = (Join-Path $STAGE 'ShadowMountPlus_1.7beta3.elf');    as = 'ShadowMountPlus_1.7beta3.elf' },
  @{ from = (Join-Path $STAGE 'PKG-Manager_v1.4.1.elf');          as = 'PKG-Manager_v1.4.1.elf' },
  @{ from = (Join-Path $STAGE 'kstuff.elf');                      as = 'kstuff.elf' },
  @{ from = (Join-Path $STAGE 'ps5-web-file-manager_v1.9.elf');   as = 'ps5-web-file-manager_v1.9.elf' },
  @{ from = (Join-Path $STAGE 'ftpsrv_v0.21.1.elf');              as = 'ftpsrv_v0.21.1.elf' },
  @{ from = (Join-Path $STAGE 'nanodns.elf');                     as = 'nanodns.elf' },
  @{ from = (Join-Path $STAGE 'np-fake-signin-ps5.elf');          as = 'np-fake-signin-ps5.elf' },
  @{ from = 'C:\Users\Essy\Desktop\OmniPad-PS5-1.0.4-g7pro-b98.elf'; as = 'OmniPad-PS5-1.0.4-g7pro-b98.elf' }
)

$n = 0
foreach ($m in $map) {
  if (-not (Test-Path $m.from)) { Write-Output ("  MISSING source: {0}" -f $m.from); continue }
  Copy-Item $m.from -Destination (Join-Path $NEWDIR $m.as) -Force
  $f = Get-Item (Join-Path $NEWDIR $m.as)
  Write-Output ("  {0,-36} {1,10} B" -f $m.as, $f.Length)
  $n++
}
Write-Output ("  staged {0} files into pyre-payloads\" -f $n)

Write-Output ""
Write-Output "=== 3. sequence payloads Cam wants replaced ==="
foreach ($p in @('kstuff.elf','shadowmountplus.elf','etaHEN.elf')) {
  $cur = Join-Path $PYRE "payloads\$p"
  if (Test-Path $cur) {
    Write-Output ("  current payloads\{0,-20} {1,10} B  {2}" -f $p, (Get-Item $cur).Length, (Get-FileHash $cur -Algorithm SHA256).Hash.Substring(0,16))
  }
}
Write-Output "  (NOT yet applied - staged only. Nothing deleted.)"
