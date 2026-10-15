<#
.SYNOPSIS
    Fetches the pinned libmpv build for Windows into third_party/bin/<rid>/libmpv-2.dll.

.DESCRIPTION
    libmpv is not committed to the repository. This script downloads the build pinned in
    third_party/libmpv.json, checks its SHA-256, and extracts libmpv-2.dll. It does nothing
    when the right file is already in place. The app's build runs it automatically on
    Windows; run it by hand to prepare the engine tests.

    With -Discover it instead looks up the newest release of the build source, downloads
    the assets for both architectures and prints the values to pin. Nothing is pinned
    automatically: a person reviews and commits the change to third_party/libmpv.json.

.PARAMETER RuntimeIdentifier
    win-x64 or win-arm64.
#>
param(
    [ValidateSet('win-x64', 'win-arm64')] [string] $RuntimeIdentifier = 'win-x64',
    [switch] $Discover
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$pinFile = Join-Path $root 'third_party/libmpv.json'
$pin = Get-Content -Raw $pinFile | ConvertFrom-Json

function Expand-SevenZip([string] $archive, [string] $destination) {
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $sevenZip = @(
        (Get-Command 7z -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        "$env:ProgramFiles\7-Zip\7z.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($sevenZip) {
        & $sevenZip x $archive "-o$destination" -y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7-Zip could not extract $archive" }
        return
    }
    # Windows 11's tar (libarchive) reads 7z archives.
    & tar.exe -xf $archive -C $destination
    if ($LASTEXITCODE -ne 0) { throw "Neither 7-Zip nor tar could extract $archive. Install 7-Zip and try again." }
}

# Get-FileHash is a script function in Windows PowerShell 5.1's Utility module, and it fails to
# load when this script is started from PowerShell 7 (as MSBuild does on CI), so hash with .NET.
function Get-Sha256([string] $path) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-Archive([string] $url, [string] $file) {
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) "aperture-libmpv"
    New-Item -ItemType Directory -Force -Path $temp | Out-Null
    $archive = Join-Path $temp $file
    if (-not (Test-Path $archive)) {
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile "$archive.part" -UseBasicParsing
        Move-Item -Force "$archive.part" $archive
    }
    return $archive
}

if ($Discover) {
    $headers = @{ 'User-Agent' = 'aperture-build' }
    if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$($pin.repository)/releases/latest" -Headers $headers
    Write-Host "Newest release of $($pin.repository): $($release.tag_name) ($($release.published_at))"
    foreach ($rid in 'win-x64', 'win-arm64') {
        $pattern = $pin.assetPatterns.$rid
        $asset = $release.assets | Where-Object { $_.name -match $pattern } | Select-Object -First 1
        if (-not $asset) { Write-Host "  $rid : no asset matches $pattern"; continue }
        $archive = Get-Archive $asset.browser_download_url $asset.name
        $hash = Get-Sha256 $archive
        Write-Host "  $rid : $($asset.name) sha256=$hash size=$($asset.size)"
        $probe = Join-Path ([System.IO.Path]::GetTempPath()) "aperture-libmpv-probe-$rid"
        Expand-SevenZip $archive $probe
        Get-ChildItem -Recurse $probe | Where-Object { -not $_.PSIsContainer } | ForEach-Object { Write-Host "      $($_.FullName.Substring($probe.Length)) ($($_.Length) bytes)" }
    }
    return
}

$entry = $pin.assets.$RuntimeIdentifier
if (-not $entry -or -not $entry.file -or -not $entry.sha256) {
    throw "third_party/libmpv.json has no pinned libmpv for $RuntimeIdentifier. Run tools/Get-Libmpv.ps1 -Discover and pin the result."
}

$destination = Join-Path $root "third_party/bin/$RuntimeIdentifier"
$dll = Join-Path $destination 'libmpv-2.dll'
$stamp = Join-Path $destination 'libmpv.sha256'
if ((Test-Path $dll) -and (Test-Path $stamp) -and ((Get-Content -Raw $stamp).Trim() -eq $entry.sha256)) {
    return
}

$url = "https://github.com/$($pin.repository)/releases/download/$($pin.release)/$($entry.file)"
$archive = Get-Archive $url $entry.file
$hash = Get-Sha256 $archive
if ($hash -ne $entry.sha256) {
    Remove-Item -Force $archive
    throw "libmpv download for $RuntimeIdentifier has SHA-256 $hash, expected $($entry.sha256). Nothing was installed."
}

$unpacked = Join-Path ([System.IO.Path]::GetTempPath()) "aperture-libmpv-$RuntimeIdentifier"
if (Test-Path $unpacked) { Remove-Item -Recurse -Force $unpacked }
Expand-SevenZip $archive $unpacked
$found = Get-ChildItem -Recurse -Filter 'libmpv-2.dll' $unpacked | Select-Object -First 1
if (-not $found) { throw "The libmpv archive for $RuntimeIdentifier does not contain libmpv-2.dll." }

New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -Force $found.FullName $dll
Set-Content -Path $stamp -Value $entry.sha256 -NoNewline
Write-Host "libmpv for $RuntimeIdentifier installed from $($pin.release) ($($entry.file))"
