<#
.SYNOPSIS
    Builds Aperture's release files for one platform: the installer and a portable zip.

.DESCRIPTION
    Publishes Aperture.App (self-contained, with the media server in Server\ and the pinned
    libmpv), adds the licence and notices, then makes:
      Aperture-<version>-<platform>-setup.exe     per-user installer (Inno Setup 6)
      Aperture-<version>-<platform>-portable.zip  the same files, to unzip and run
      SHA256SUMS-<platform>.txt                   hashes of both
    The version comes from Directory.Build.props. Runs on Windows with the .NET 10 SDK.
    Inno Setup 6 is found in its usual folders; pass -InnoSetup to point at ISCC.exe.

.EXAMPLE
    pwsh tools/Build-Release.ps1 -Platform x64
#>
param(
    [ValidateSet('x64', 'ARM64')] [string] $Platform = 'x64',
    [string] $OutDir,
    [string] $InnoSetup
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'artifacts/release' }
$OutDir = [IO.Path]::GetFullPath($OutDir)
$rid = if ($Platform -eq 'x64') { 'win-x64' } else { 'win-arm64' }

$version = ([xml](Get-Content -Raw (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props' }
Write-Host "Aperture $version for $Platform"

$publish = Join-Path $OutDir "publish-$rid"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

dotnet publish (Join-Path $root 'src/Aperture.App/Aperture.App.csproj') -c Release -p:Platform=$Platform -r $rid --self-contained -nologo -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

if (-not (Get-ChildItem $publish -Filter *.pri)) { throw 'The published app has no .pri file (compiled XAML resources); it would fail at launch' }
Get-ChildItem $publish -Filter *.pri | ForEach-Object { Write-Host "Resources: $($_.Name) ($($_.Length) bytes)" }
foreach ($required in 'Aperture.exe', 'libmpv-2.dll', 'av_libglesv2.dll', 'Assets/Aperture.ico', 'Server/Aperture.Server.exe', 'Server/wwwroot/app.js') {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "$required is missing from the published app" }
}

# Debug symbols stay out of the release; the licence and notices go in.
Get-ChildItem $publish -Recurse -Filter *.pdb | Remove-Item -Force
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $publish 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $publish
New-Item -ItemType Directory -Force -Path (Join-Path $publish 'licenses') | Out-Null
Copy-Item (Join-Path $root 'third_party/licenses/*') (Join-Path $publish 'licenses')

$zip = Join-Path $OutDir "Aperture-$version-$Platform-portable.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Portable zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"

if (-not $InnoSetup) {
    $InnoSetup = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $InnoSetup) { throw 'Inno Setup 6 (ISCC.exe) was not found. Install it from https://jrsoftware.org/isdl.php or pass -InnoSetup.' }

& $InnoSetup /Qp "/DAppVersion=$version" "/DPlatform=$Platform" "/DSourceDir=$publish" "/DOutputDir=$OutDir" (Join-Path $root 'installer/Aperture.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }
$setup = Join-Path $OutDir "Aperture-$version-$Platform-setup.exe"
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB)) MB)"

# Hash with .NET: Get-FileHash is missing when Windows PowerShell is started from pwsh.
$sums = foreach ($file in $setup, $zip) {
    $stream = [IO.File]::OpenRead($file)
    try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
    "$hash  $(Split-Path -Leaf $file)"
}
$sums | Set-Content -Encoding ascii (Join-Path $OutDir "SHA256SUMS-$Platform.txt")
$sums | Write-Host
