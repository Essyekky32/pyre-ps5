# Pull Cam's real payload set off the console into a staging folder.
# Read-only over FTP. Skips known-empty dirs and the byte-identical kstuff-lite.
$ErrorActionPreference = 'Continue'
$host_ = '192.168.1.194:2121'
$base  = '/data/pldmgr/payloads'
$stage = 'D:\Projects\PYRE-payloads-staging'
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# dir, filename  (kstuff-lite deliberately omitted: byte-identical to kstuff)
$items = @(
  @('kstuff',                  'kstuff.elf'),
  @('ShadowMountPlus',         'ShadowMountPlus_1.7beta3.elf'),
  @('ps5-web-file-manager',    'ps5-web-file-manager_v1.9.elf'),
  @('PKG-Manager',             'PKG-Manager_v1.4.1.elf'),
  @('ftpsrv',                  'ftpsrv_v0.21.1.elf'),
  @('np-fake-signin',          'np-fake-signin-ps5.elf'),
  @('nanodns',                 'nanodns.elf')
)

$ok = 0; $bad = 0
foreach ($it in $items) {
  $dir = $it[0]; $file = $it[1]
  $out = Join-Path $stage $file
  Remove-Item $out -Force -ErrorAction SilentlyContinue
  & curl.exe -s -m 180 -Q "CWD $base/$dir" -o $out "ftp://$host_/$file"
  $rc = $LASTEXITCODE
  if ((Test-Path $out) -and (Get-Item $out).Length -gt 0) {
    Write-Output ("OK   {0,-34} {1,10} B" -f $file, (Get-Item $out).Length)
    $ok++
  } else {
    Write-Output ("FAIL {0,-34} curl exit {1}" -f $file, $rc)
    $bad++
  }
}
Write-Output ""
Write-Output "pulled $ok ok, $bad failed"
