# Publishes the PYRE distributable: Pyre.exe (site packed INSIDE it) PLUS the ELF payloads
# that ship BESIDE it.
#
# Default: FRAMEWORK-DEPENDENT single file, win-x64. The pages/scripts/offsets travel inside
# the exe (packed at build time, unpacked + SHA256-verified at first run), but the .NET runtime
# does not: the user installs the .NET 8 Desktop Runtime (x64) once and Pyre.exe stays small.
# The ELF payloads are copied next to the exe, preserving their site-relative layout, so the
# runtime resolves them from the exe's own folder (no hard-coded paths).
#
#   -SelfContained             the fat build: runtime packed in, user installs nothing.
#   -IncludeOptionalPayloads   also ship the root optional-*.elf (~16 files). Without it a lean
#                              build ships the required payloads only - the Additional Payloads
#                              buttons for the optional tools will 404.
#
# The which-payloads decision comes from the manifest the packer wrote (obj\siteassets\
# site-manifest.json), so the shipped folder and the exe always agree. See build-embed.ps1.

[CmdletBinding()]
param(
    [string]$ProjectDir,
    [string]$OutputDir,
    [string]$Configuration = 'Release',
    [switch]$IncludeOptionalPayloads,
    [switch]$SelfContained
)

# Tool folder is this script's folder; the shipped exe goes to <site>\dist.
if (-not $ProjectDir) { $ProjectDir = $PSScriptRoot }
if (-not $OutputDir)  { $OutputDir  = Join-Path (Split-Path -Parent $ProjectDir) 'dist' }

$ErrorActionPreference = 'Stop'

$project = Join-Path $ProjectDir 'Pyre\Pyre.csproj'
$siteRoot = Split-Path -Parent $ProjectDir
$manifestPath = Join-Path $ProjectDir 'Pyre\obj\siteassets\site-manifest.json'
$selfContained = [bool]$SelfContained

Write-Host "Pyre - publish"
Write-Host "  project : $project"
Write-Host "  output  : $OutputDir"
Write-Host "  runtime : $(if ($selfContained) { 'self-contained (fat: user installs nothing)' } else { 'framework-dependent (.NET 8 Desktop Runtime, x64, required)' })"
Write-Host "  payloads: $(if ($IncludeOptionalPayloads) { 'all payloads (incl. optional-*.elf)' } else { 'required payloads (no optional-*.elf)' })"

$sdk = (dotnet --list-sdks | Select-Object -Last 1)
Write-Host "  sdk     : $sdk"

if (-not (Test-Path -LiteralPath $project)) {
    throw "Pyre.csproj not found at $project"
}

if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# The site packer runs inside the build (GeneratePyreSiteAssets); this only passes the switch.
$fat = if ($IncludeOptionalPayloads) { 'true' } else { 'false' }
$sc = if ($selfContained) { 'true' } else { 'false' }
$properties = @(
    '-p:PublishSingleFile=true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:PublishTrimmed=false'
    '-p:DebugType=none'
    '-p:DebugSymbols=false'
    "-p:PyreIncludeOptionalPayloads=$fat"
)

# -p:DebugType=none keeps a .pdb out of the shipped folder.
dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained $sc `
    @properties `
    --output $OutputDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# ------------------------------------------------------------------ the ELFs, beside the exe
#
# The manifest lists every payload the site asks for (path/size/sha256/optional). Copy each
# into the shipped folder preserving its relative path, so payloads\..., our-payloads\... and
# the root optional-*.elf all end up exactly where the runtime looks first. A single elfs\
# folder also works at runtime (matched on the basename), but the shipped shape is the mirrored
# one - obvious to read and one folder to hand over.

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "the site manifest was not produced by the build: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($null -eq $manifest.payloads -or $manifest.payloads.Count -eq 0) {
    throw "the site manifest lists no external payloads - refusing to ship an exe whose page buttons would 404"
}

# Clear the payload folders this script manages, so a lean publish after a fat one does not
# leave last build's optional ELFs behind. Pyre.exe is never touched.
foreach ($managed in @('payloads', 'our-payloads')) {
    $dir = Join-Path $OutputDir $managed
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}
Get-ChildItem -LiteralPath $OutputDir -Filter '*.elf' -File -ErrorAction SilentlyContinue | Remove-Item -Force

$copied = 0
$copiedBytes = [int64]0
$skippedOptional = 0

foreach ($payload in $manifest.payloads) {
    if ($payload.optional -and -not $IncludeOptionalPayloads) { $skippedOptional++; continue }

    $relative = $payload.path -replace '/', '\'
    $source = Join-Path $siteRoot $relative
    if (-not (Test-Path -LiteralPath $source)) {
        throw "the manifest lists a payload that is not in the site: $($payload.path)"
    }

    $destination = Join-Path $OutputDir $relative
    $destinationDir = Split-Path -Parent $destination
    if ($destinationDir -and -not (Test-Path -LiteralPath $destinationDir)) {
        New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
    }

    Copy-Item -LiteralPath $source -Destination $destination -Force

    $size = (Get-Item -LiteralPath $destination).Length
    if ($size -ne $payload.size) {
        throw "copied $($payload.path) is $size bytes, the manifest says $($payload.size)"
    }

    $copied++
    $copiedBytes += $size
}

Write-Host ""
Write-Host "payloads : $copied files / $copiedBytes bytes copied beside the exe"
if ($skippedOptional -gt 0) {
    Write-Host "           ($skippedOptional optional-*.elf not shipped - pass -IncludeOptionalPayloads to include them)"
}

# ------------------------------------------------------------------ report the shipped tree

$exe = Join-Path $OutputDir 'Pyre.exe'
$info = Get-Item -LiteralPath $exe
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash

$files = @(Get-ChildItem -LiteralPath $OutputDir -Recurse -File)
$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum

Write-Host ""
Write-Host "dist tree ($($files.Count) files, $totalBytes bytes total):"
$prefix = $OutputDir.TrimEnd('\') + '\'
foreach ($file in ($files | Sort-Object FullName)) {
    $short = $file.FullName.Substring($prefix.Length)
    Write-Host ("  {0,12}  {1}" -f $file.Length, $short)
}

Write-Host ""
Write-Host "published: $($info.FullName)"
Write-Host "size     : $($info.Length) bytes ($([math]::Round($info.Length / 1MB, 1)) MB)"
Write-Host "sha256   : $hash"
if (-not $selfContained) {
    Write-Host ""
    Write-Host "requires : .NET 8 Desktop Runtime, x64"
    Write-Host "           https://dotnet.microsoft.com/download/dotnet/8.0"
    Write-Host "           (or rebuild with -SelfContained to ship the runtime inside the exe)"
}
Write-Host ""
Write-Host "smoke test (no --root: this exercises the embedded copy and the payloads beside the exe):"
& $exe --selftest --port 18080
Write-Host "selftest exit code: $LASTEXITCODE"
