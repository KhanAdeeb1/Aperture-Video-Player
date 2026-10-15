<#
.SYNOPSIS
    Installs, upgrades and uninstalls Aperture on this Windows machine, checking each step.

.DESCRIPTION
    Meant for a clean, throwaway machine (CI). It uses the real per-user locations, so on a
    machine where Aperture is already installed it refuses to run. Steps:
      1. Clean install of the older installer: files, Start menu entry, Installed apps
         entry, "Open with" registration, and a launch that creates the database.
      2. Upgrade to the new installer while the app and the media server are running:
         the version changes, files from the old version are cleared, the database and
         settings are kept, and there is still exactly one Installed apps entry.
      3. Launch the upgraded app and open a video given on the command line.
      4. Silent uninstall: program files, shortcut and registrations are gone; the
         library and settings in %LOCALAPPDATA%\Aperture stay (only the interactive
         uninstaller offers to remove them).
    Every check that fails throws, so the job fails.

.PARAMETER OldSetup
    The installer to start from (an earlier version).

.PARAMETER NewSetup
    The installer to upgrade to.

.PARAMETER Video
    A playable video for the launch test.
#>
param(
    [Parameter(Mandatory)] [string] $OldSetup,
    [Parameter(Mandatory)] [string] $NewSetup,
    [Parameter(Mandatory)] [string] $Video
)

$ErrorActionPreference = 'Stop'
$appId = '{940B2F38-5C0F-4831-81FA-4F24BDF190AC}_is1'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Aperture'
$dataDir = Join-Path $env:LOCALAPPDATA 'Aperture'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Aperture.lnk'
$exe = Join-Path $installDir 'Aperture.exe'
$server = Join-Path $installDir 'Server\Aperture.Server.exe'

function Step([string] $text) { Write-Host "`n=== $text" }
function Check([bool] $condition, [string] $what) {
    if (-not $condition) { throw "FAILED: $what" }
    Write-Host "ok   $what"
}

function Install([string] $setup) {
    $log = Join-Path $env:RUNNER_TEMP ("install-" + [IO.Path]::GetFileNameWithoutExtension($setup) + ".log")
    $p = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS', "/LOG=`"$log`"" -PassThru -Wait
    if ($p.ExitCode -ne 0) { Get-Content $log -ErrorAction SilentlyContinue; throw "$setup exited with $($p.ExitCode)" }
}

function Launch([string[]] $arguments = @()) {
    $p = if ($arguments.Count) { Start-Process -FilePath $exe -ArgumentList $arguments -PassThru } else { Start-Process -FilePath $exe -PassThru }
    $deadline = (Get-Date).AddSeconds(30)
    do { Start-Sleep -Milliseconds 500; $p.Refresh() } until ($p.HasExited -or $p.MainWindowHandle -ne 0 -or (Get-Date) -gt $deadline)
    if ($p.HasExited) {
        Get-Content (Join-Path $dataDir 'Logs\aperture.log') -Tail 40 -ErrorAction SilentlyContinue
        Get-WinEvent -LogName Application -MaxEvents 10 -ErrorAction SilentlyContinue |
            Where-Object { $_.ProviderName -in '.NET Runtime', 'Application Error' } | ForEach-Object { $_.Message }
        Get-ChildItem $installDir -Filter *.pri | ForEach-Object { "installed resources: $($_.Name)" }
        throw "Aperture exited during startup with code $($p.ExitCode)"
    }
    Check ($p.MainWindowHandle -ne 0) "Aperture opened its window ('$($p.MainWindowTitle)')"
    return $p
}

function Close($p) {
    # Ask the window to close, as clicking X does, so settings and positions are saved.
    $null = $p.CloseMainWindow()
    if (-not $p.WaitForExit(20000)) { Stop-Process -Id $p.Id -Force; throw 'Aperture did not close within 20 s' }
    Check $true "Aperture closed (exit code $($p.ExitCode))"
}

function OpenWithRegistered {
    $value = Get-ItemProperty -Path 'HKCU:\Software\Classes\.mkv\OpenWithProgids' -Name 'Aperture.Video' -ErrorAction SilentlyContinue
    return $null -ne $value
}

if ((Test-Path $installDir) -or (Test-Path $uninstallKey) -or (Test-Path $dataDir)) {
    throw "Aperture is already installed or has data on this machine; this test needs a clean machine."
}

Step 'Clean install (older version)'
Install $OldSetup
$old = Get-ItemProperty $uninstallKey
Check (Test-Path $exe) 'Aperture.exe is in %LOCALAPPDATA%\Programs\Aperture'
Check (Test-Path (Join-Path $installDir 'libmpv-2.dll')) 'libmpv-2.dll was installed'
Check (Test-Path (Join-Path $installDir 'LICENSE.txt')) 'the licence was installed'
Check (Test-Path (Join-Path $installDir 'THIRD-PARTY-NOTICES.md')) 'the third-party notices were installed'
Check (Test-Path $shortcut) 'the Start menu has Aperture'
Check ($old.DisplayName -eq 'Aperture') "Installed apps lists 'Aperture' ($($old.DisplayVersion))"
Check (OpenWithRegistered) '"Open with" lists Aperture for .mkv'
$mkvDefault = (Get-ItemProperty 'HKCU:\Software\Classes\.mkv' -ErrorAction SilentlyContinue).'(default)'
Check ($mkvDefault -ne 'Aperture.Video') 'Aperture did not make itself the default player'

$p = Launch
Close $p
Check (Test-Path (Join-Path $dataDir 'aperture.db')) 'the first launch created the database'
$dbBefore = (Get-Item (Join-Path $dataDir 'aperture.db')).Length

Step 'Upgrade while the app and the media server are running'
$stale = Join-Path $installDir 'left-over-from-old-version.dll'
Set-Content -Path $stale -Value 'old'
$app = Launch
$serverEnv = Start-Process -FilePath $server -ArgumentList 'run', '--background' -PassThru -WindowStyle Hidden
$deadline = (Get-Date).AddSeconds(30)
do { Start-Sleep -Seconds 1; & $server status | Out-Null } until ($LASTEXITCODE -eq 0 -or (Get-Date) -gt $deadline)
Check ($LASTEXITCODE -eq 0) 'the media server is running before the upgrade'

Install $NewSetup
$new = Get-ItemProperty $uninstallKey
Check ($new.DisplayVersion -ne $old.DisplayVersion) "the version changed from $($old.DisplayVersion) to $($new.DisplayVersion)"
Check (-not (Test-Path $stale)) 'files from the old version were cleared'
Check (Test-Path $exe) 'Aperture.exe is in place after the upgrade'
Check ($app.HasExited) 'the running app was closed for the upgrade'
$serverEnv.Refresh()
Check ($serverEnv.HasExited) 'the media server was stopped for the upgrade'
Check (Test-Path (Join-Path $dataDir 'aperture.db')) 'the database was kept'
Check ((Get-Item (Join-Path $dataDir 'aperture.db')).Length -ge $dbBefore) 'the database was not reset'
$entries = @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | Where-Object { (Get-ItemProperty $_.PSPath).DisplayName -eq 'Aperture' })
Check ($entries.Count -eq 1) 'Installed apps has exactly one Aperture entry'

Step 'Launch the upgraded app with a video'
$p = Launch @("`"$Video`"")
Start-Sleep -Seconds 8
$p.Refresh()
Check (-not $p.HasExited) 'Aperture is still running after opening the video'
Write-Host "     window title: '$($p.MainWindowTitle)'"
Close $p
$log = Get-Content (Join-Path $dataDir 'Logs\aperture.log') -Raw
Check ($log -notmatch [regex]::Escape([IO.Path]::GetFileName($Video))) 'the log does not contain the video''s file name'

Step 'Silent uninstall'
$uninstaller = $new.UninstallString.Trim('"')
$p = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait
Check ($p.ExitCode -eq 0) "the uninstaller finished (exit $($p.ExitCode))"
# unins000.exe hands over to a copy of itself in %TEMP% (named _iu*.tmp) and exits at once, so
# wait for that copy, which does the removing, to finish.
$deadline = (Get-Date).AddMinutes(5)
Start-Sleep -Seconds 2
while ((Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like '_iu*' }) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
while ((Test-Path $installDir) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
if (Test-Path $installDir) {
    Write-Host '     left in the program folder:'
    Get-ChildItem $installDir -Recurse -Force | Select-Object -First 20 | ForEach-Object { Write-Host "       $($_.FullName.Substring($installDir.Length))" }
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Write-Host "     still running from it: $($_.ProcessName) ($($_.Id))" }
}
Check (-not (Test-Path $installDir)) 'the program folder is gone'
Check (-not (Test-Path $shortcut)) 'the Start menu entry is gone'
Check (-not (Test-Path $uninstallKey)) 'the Installed apps entry is gone'
Check (-not (OpenWithRegistered)) 'the "Open with" registration is gone'
Check (-not (Test-Path 'HKCU:\Software\Classes\Aperture.Video')) 'the Aperture.Video file type is gone'
Check (Test-Path (Join-Path $dataDir 'aperture.db')) 'the library and settings were kept (silent uninstall)'

Write-Host "`nInstall, upgrade and uninstall: all checks passed."
