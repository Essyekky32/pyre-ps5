# Build-time site packer for the Pyre desktop tool (wave 4).
#
# Walks the frozen site folder D:\Projects\PYRE, packs everything the tool should carry
# INSIDE the exe into ONE deterministic zip, and writes a manifest of every embedded file
# with its SHA256 - plus the list of ELF payloads that must sit BESIDE the exe.
#
# Included in the exe : every file under the site root EXCEPT tool\, dist\, Pyre.exe itself,
#                       the our-payloads\ source zip - and EVERY .elf.
# Excluded from the exe: all *.elf payloads (they ship beside Pyre.exe, resolved at runtime
#                       from the exe's own folder - see tool\Pyre\Services\ElfLocator.cs), and
#                       our-payloads\ps5-webkit-autoloader-0.5.2-kp-fix.zip (the autoloader
#                       SOURCE zip, never needed at runtime, excluded in every build).
# Listed as external : every *.elf the site asks for, with path/size/sha256 and an optional
#                       flag for the root-level optional-*.elf (shipped only with
#                       -IncludeOptionalPayloads). The runtime serves any *.elf beside the exe
#                       and reports which of these are present / missing.
#
# Output (in $OutDir):
#   site.zip            deterministic zip; entry names are the site-relative paths, '/'-separated
#   site-manifest.json  { buildId, fileCount, totalBytes, zipSha256, includeOptionalPayloads,
#                         payloadCount, payloadBytes, files[], payloads[] }
#   site-manifest.txt   the embedded list, human readable, for eyeballing the build log
#
# The build id is derived from the EMBEDDED file list (path + SHA256 + the payload switch), so
# the runtime can unpack to %LOCALAPPDATA%\Pyre\site\<buildId>\ and reuse it until the embedded
# site changes. It deliberately does NOT depend on the external payloads: swapping a payload
# beside the exe must not force a pointless re-unpack. The exclusions are asserted here too, so
# a build can never quietly carry an ELF (or the source zip) inside the exe again.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SiteRoot,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$LogFile,
    # MSBuild only adds this switch when the fat build was asked for, so presence = true.
    # Wave 4: it no longer changes the exe at all (no ELF is embedded either way) - it is
    # recorded in the manifest and read by publish.ps1 to decide which ELFs go in the shipped
    # folder. Kept as a switch so the existing publish/msbuild plumbing is unchanged.
    [switch]$IncludeOptionalPayloads
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 does not have the zip types loaded by default.
Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

function Write-Build([string]$Message) {
    if ($LogFile) {
        try {
            $dir = Split-Path -Parent $LogFile
            if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            Add-Content -LiteralPath $LogFile -Value $Message
        } catch { }
    }
    Write-Host $Message
}

function Get-Sha256Hex([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '') }
        finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
}

if ($LogFile) {
    # Fresh log for each run - a build log that only ever grows is not a log.
    $logDir = Split-Path -Parent $LogFile
    if ($logDir -and -not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
    Set-Content -LiteralPath $LogFile -Value "--- pyre-embed $([System.DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss')) ---"
}

if (-not (Test-Path -LiteralPath $SiteRoot)) { throw "site root not found: $SiteRoot" }
$SiteRoot = (Resolve-Path -LiteralPath $SiteRoot).Path.TrimEnd('\', '/')

if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

$excludedDirs = @('tool', 'dist')
$excludedRootNames = @('Pyre.exe')

# Payloads that must be listed as external in EVERY build: a page pushes the PYRE autoloader
# installer to the console's loader, so a build that lost it from the payload list would 404.
$requiredPayloads = @('our-payloads/pyre-autoloader-installer_v0.5.2-pyre.elf')

Write-Build "pyre-embed: site   $SiteRoot"
Write-Build "pyre-embed: out    $OutDir"
Write-Build "pyre-embed: optionals-listed $([bool]$IncludeOptionalPayloads)"

# ------------------------------------------------------------------ collect + hash

$entries = New-Object System.Collections.Generic.List[object]
$payloads = New-Object System.Collections.Generic.List[object]
$excludedCount = 0
$excludedBytes = 0

foreach ($file in Get-ChildItem -LiteralPath $SiteRoot -Recurse -File -Force) {
    $relative = $file.FullName.Substring($SiteRoot.Length).TrimStart('\', '/')
    $relative = $relative.Replace('\', '/')
    $segments = $relative.Split('/')
    $top = $segments[0]
    $isElf = $relative.ToLowerInvariant().EndsWith('.elf')

    $exclude = $false
    $external = $false
    if ($segments.Length -gt 1 -and ($excludedDirs -contains $top)) { $exclude = $true }
    elseif ($segments.Length -eq 1 -and ($excludedRootNames -contains $segments[0])) { $exclude = $true }
    elseif ($top -eq 'our-payloads' -and $relative -like '*.zip') { $exclude = $true }
    elseif ($isElf) { $exclude = $true; $external = $true }

    if ($exclude) {
        $excludedCount++
        $excludedBytes += $file.Length

        if ($external) {
            # A root-level optional-*.elf ships only with -IncludeOptionalPayloads; everything
            # else the site asks for is a required payload.
            $optional = ($segments.Length -eq 1 -and $segments[0].ToLowerInvariant().StartsWith('optional-'))
            $payloads.Add([pscustomobject]@{
                Rel      = $relative
                Size     = [int64]$file.Length
                Sha256   = Get-Sha256Hex $file.FullName
                Optional = [bool]$optional
            })
            Write-Build "pyre-embed:  payload   $relative"
        }
        elseif ($top -eq 'our-payloads' -or ($segments.Length -eq 1 -and $segments[0] -eq 'Pyre.exe')) {
            Write-Build "pyre-embed:  excluded  $relative"
        }
        continue
    }

    $entries.Add([pscustomobject]@{
        Rel    = $relative
        Full   = $file.FullName
        Size   = [int64]$file.Length
        Sha256 = Get-Sha256Hex $file.FullName
    })
}

$sorted = $entries | Sort-Object -Property Rel
$payloadSorted = $payloads | Sort-Object -Property Rel

# ------------------------------------------------------------------ build id

$idBuilder = New-Object System.Text.StringBuilder
[void]$idBuilder.Append("optional=$([bool]$IncludeOptionalPayloads)`n")
foreach ($e in $sorted) { [void]$idBuilder.Append($e.Rel).Append("`n").Append($e.Sha256).Append("`n") }
$idSha = [System.Security.Cryptography.SHA256]::Create()
try { $buildId = ([System.BitConverter]::ToString($idSha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($idBuilder.ToString()))) -replace '-', '').Substring(0, 16) }
finally { $idSha.Dispose() }

$totalBytes = [int64]0
foreach ($e in $sorted) { $totalBytes += $e.Size }

$payloadBytes = [int64]0
foreach ($p in $payloadSorted) { $payloadBytes += $p.Size }

Write-Build "pyre-embed: files  $($sorted.Count)"
Write-Build "pyre-embed: bytes  $totalBytes"
Write-Build "pyre-embed: payloads $($payloadSorted.Count) files / $payloadBytes bytes (beside the exe)"
Write-Build "pyre-embed: build  $buildId"

# ------------------------------------------------------------------ deterministic zip

$zipPath = Join-Path $OutDir 'site.zip'
$manifestPath = Join-Path $OutDir 'site-manifest.json'
$listPath = Join-Path $OutDir 'site-manifest.txt'

# Unchanged site (same derived build id) -> leave the files untouched so incremental builds
# do not see a changed embedded resource for no reason.
$reuse = $false
if ((Test-Path -LiteralPath $zipPath) -and (Test-Path -LiteralPath $manifestPath)) {
    try {
        $existing = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($existing.buildId -eq $buildId) { $reuse = $true }
    } catch { $reuse = $false }
}

if ($reuse) {
    Write-Build "pyre-embed: unchanged - reusing existing site.zip / site-manifest.json"
} else {
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

    $fixedTimestamp = [System.DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
    $fileStream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $zip = [System.IO.Compression.ZipArchive]::new($fileStream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($e in $sorted) {
                $entry = $zip.CreateEntry($e.Rel, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedTimestamp
                $target = $entry.Open()
                $source = [System.IO.File]::Open($e.Full, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
                try { $source.CopyTo($target) }
                finally { $source.Dispose(); $target.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $fileStream.Dispose() }
}

$zipSha = Get-Sha256Hex $zipPath
$zipSize = (Get-Item -LiteralPath $zipPath).Length

# ------------------------------------------------------------------ manifest

$manifest = [ordered]@{
    schema                  = 2
    buildId                 = $buildId
    generatedUtc            = [System.DateTime]::UtcNow.ToString('o')
    includeOptionalPayloads = [bool]$IncludeOptionalPayloads
    fileCount               = $sorted.Count
    totalBytes              = $totalBytes
    zipSha256               = $zipSha
    zipSize                 = $zipSize
    payloadCount            = $payloadSorted.Count
    payloadBytes            = $payloadBytes
    files                   = @($sorted | ForEach-Object { [ordered]@{ path = $_.Rel; size = $_.Size; sha256 = $_.Sha256 } })
    payloads                = @($payloadSorted | ForEach-Object { [ordered]@{ path = $_.Rel; size = $_.Size; sha256 = $_.Sha256; optional = $_.Optional } })
}

$json = $manifest | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($manifestPath, $json, [System.Text.UTF8Encoding]::new($false))

$listBuilder = New-Object System.Text.StringBuilder
foreach ($e in $sorted) { [void]$listBuilder.Append($e.Sha256).Append('  ').Append($e.Rel).Append("`r`n") }
[System.IO.File]::WriteAllText($listPath, $listBuilder.ToString(), [System.Text.UTF8Encoding]::new($false))

# ------------------------------------------------------------------ assertions

# Wave 4: NOTHING binary ships inside the exe. An .elf here would be served from the unpacked
# root as well as from beside the exe, and the whole point is that the payloads live outside.
$forbidden = @($sorted | Where-Object {
    $_.Rel.ToLowerInvariant().EndsWith('.elf') -or $_.Rel -like 'our-payloads/*.zip'
})
if ($forbidden.Count -gt 0) {
    throw "this build must not contain: $($forbidden.Rel -join ', ')"
}

if ($payloadSorted.Count -eq 0) {
    throw "this build lists no external payloads - the site asks for its ELFs beside the exe, so the manifest must list them"
}

# The runtime payloads must survive every exclusion rule above. A build that dropped one from
# the payload list would still verify, but the page that pushes it would 404 - fail instead.
foreach ($needed in $requiredPayloads) {
    if (-not ($payloadSorted.Rel -contains $needed)) {
        throw "the external payload list is missing a payload the site needs: $needed"
    }
}

Write-Build "pyre-embed: zip    $zipPath"
Write-Build "pyre-embed: zipsz  $zipSize"
Write-Build "pyre-embed: zipsha $zipSha"
Write-Build "pyre-embed: excluded $excludedCount files / $excludedBytes bytes"
Write-Build "pyre-embed: done"
exit 0
