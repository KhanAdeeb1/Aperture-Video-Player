<#
.SYNOPSIS
    Drives a real Aperture build through its interface states and screenshots each one.

.DESCRIPTION
    Used by the visual-review CI job (Windows PowerShell 5.1). Everything happens through
    real input: command-line file opening, keyboard shortcuts sent with SendKeys, mouse
    movement, and UI Automation (the same interface Narrator uses). Nothing in the app is
    put into a special mode. Each step is independent; a failed step is logged and the
    script carries on, then exits non-zero at the end so CI shows the failure.

.PARAMETER Exe
    Path to Aperture.exe.
.PARAMETER MediaDir
    Folder made by make-test-media.sh.
.PARAMETER OutDir
    Where screenshots, the log and the throwaway data profile are written.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Exe,
    [Parameter(Mandatory = $true)] [string] $MediaDir,
    [Parameter(Mandatory = $true)] [string] $OutDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
}
"@
[Win]::SetProcessDPIAware() | Out-Null

$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$MediaDir = [System.IO.Path]::GetFullPath($MediaDir)
$dataDir = Join-Path $OutDir 'profile'
New-Item -ItemType Directory -Force -Path $OutDir, $dataDir | Out-Null
$env:APERTURE_DATA_DIR = $dataDir
$failures = New-Object System.Collections.Generic.List[string]
$notes = New-Object System.Collections.Generic.List[string]

function Note([string] $text) { Write-Host $text; $notes.Add($text) }

function Start-Aperture([string] $file) {
    if ($file) { $p = Start-Process -FilePath $Exe -ArgumentList "`"$file`"" -PassThru }
    else { $p = Start-Process -FilePath $Exe -PassThru }
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        $p.Refresh()
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    if ($p.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Aperture did not open a window' }
    [Win]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    return $p
}

function Stop-Aperture($p) {
    if ($p.HasExited) { return }
    $p.CloseMainWindow() | Out-Null
    if (-not $p.WaitForExit(20000)) { Note 'Aperture did not exit within 20 s of closing; killed'; $p.Kill() }
}

function Get-Root($p) { [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle) }

function Find-Element($p, [string] $id, [int] $seconds = 10) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $element = (Get-Root $p).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 300
    }
    throw "UI element '$id' not found"
}

function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Get-Range($element) { $element.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current }
function Set-Range($element, [double] $value) { $element.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue($value) }

function Get-WindowRect($p) {
    $rect = New-Object Win+RECT
    [Win]::DwmGetWindowAttribute($p.MainWindowHandle, 9, [ref] $rect, 16) | Out-Null  # DWMWA_EXTENDED_FRAME_BOUNDS
    return $rect
}

function Save-Shot($p, [string] $name, [switch] $FullScreen) {
    if ($FullScreen) {
        $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $x = $b.X; $y = $b.Y; $w = $b.Width; $h = $b.Height
    } else {
        $r = Get-WindowRect $p
        $x = $r.Left; $y = $r.Top; $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    }
    $bitmap = New-Object System.Drawing.Bitmap $w, $h
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($x, $y, 0, 0, $bitmap.Size)
    $graphics.Dispose()
    $bitmap.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Note "captured $name ($w x $h)"
}

function Move-Mouse([int] $x, [int] $y) { [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point $x, $y }
function Move-MouseToCenter($p, [int] $dx = 0) {
    $r = Get-WindowRect $p
    Move-Mouse ([int](($r.Left + $r.Right) / 2) + $dx) ([int](($r.Top + $r.Bottom) / 2))
}
function Move-MouseAway { Move-Mouse 2 2 }
function Send-Keys($p, [string] $keys) {
    [Win]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 250
    [System.Windows.Forms.SendKeys]::SendWait($keys)
}
function Set-WindowSize($p, [int] $w, [int] $h) {
    [Win]::ShowWindow($p.MainWindowHandle, 9) | Out-Null  # SW_RESTORE
    [Win]::MoveWindow($p.MainWindowHandle, 40, 20, $w, $h, $true) | Out-Null
    Start-Sleep -Milliseconds 1200
}

function Step([string] $name, [scriptblock] $body) {
    Write-Host "=== $name"
    try { & $body }
    catch { $failures.Add("${name}: $($_.Exception.Message)"); Write-Warning "${name} failed: $($_.Exception.Message)" }
    Get-Process -Name Aperture -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Media([string] $name) { Join-Path $MediaDir $name }

# ---------------------------------------------------------------------------------------
Step 'First launch' {
    $p = Start-Aperture
    Start-Sleep -Seconds 4
    Move-MouseAway
    Save-Shot $p '01-first-launch'
    Set-WindowSize $p 900 600
    Save-Shot $p '01b-first-launch-small-window'
    Stop-Aperture $p
}

Step 'Open file dialog' {
    $p = Start-Aperture
    Start-Sleep -Seconds 3
    Send-Keys $p '^o'
    Start-Sleep -Seconds 4
    Save-Shot $p '02-open-file-dialog' -FullScreen
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Seconds 1
    Stop-Aperture $p
}

# Build up real history: play each clip, seek with the timeline, close the window.
# Order matters: the last one played appears first on Home.
$plays = @(
    @{ File = 'Field Notes, Kyoto.mp4'; Fraction = 0.995 },
    @{ File = 'Northern Ridge.mp4';     Fraction = 0.995 },
    @{ File = 'Coastline at Dusk.mp4';  Fraction = 0.21 },
    @{ File = 'Studio Session 04.mp4';  Fraction = 0 },
    @{ File = 'First Light.mp4';        Fraction = 0.55 },
    @{ File = 'Night Drive.mp4';        Fraction = 0.68 },
    @{ File = 'Harbour Lights.mp4';     Fraction = 0.42 }
)
foreach ($play in $plays) {
    Step "Play $($play.File)" {
        $p = Start-Aperture (Media $play.File)
        Start-Sleep -Seconds 4
        $timeline = Find-Element $p 'Timeline'
        $before = (Get-Range $timeline).Value
        Start-Sleep -Seconds 2
        $range = Get-Range $timeline
        if ($range.Value -le $before) { throw "playback did not advance (position $before -> $($range.Value) s)" }
        Note "$($play.File): playing, position advanced $([math]::Round($before,1)) -> $([math]::Round($range.Value,1)) s of $([math]::Round($range.Maximum)) s"
        if ($play.Fraction -gt 0) {
            Set-Range $timeline ($range.Maximum * $play.Fraction)
            Start-Sleep -Seconds 3
        }
        Stop-Aperture $p
    }
}

Step 'Playback states' {
    # Harbour Lights was left at 42 %, so this open resumes and shows the resume notice.
    $p = Start-Aperture (Media 'Harbour Lights.mp4')
    Save-Shot $p '05-opening'
    Start-Sleep -Seconds 3
    Move-MouseToCenter $p
    Start-Sleep -Milliseconds 600
    Save-Shot $p '03-playback-controls-visible'

    Start-Sleep -Seconds 5   # default idle delay is 3 s; the pointer stays still over the video
    Save-Shot $p '04-playback-controls-hidden'

    Move-MouseToCenter $p 40
    Start-Sleep -Milliseconds 400
    Invoke-Element (Find-Element $p 'MoreButton')
    Start-Sleep -Seconds 1
    Save-Shot $p '06-playback-more-menu'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 500

    Send-Keys $p ' '
    Start-Sleep -Seconds 1
    Save-Shot $p '09-playback-paused'
    Send-Keys $p ' '

    Send-Keys $p 'p'
    Start-Sleep -Seconds 2
    Move-MouseToCenter $p
    Start-Sleep -Milliseconds 600
    Save-Shot $p '07-mini-player'
    Move-MouseAway
    Start-Sleep -Seconds 5
    Save-Shot $p '07b-mini-player-idle'
    Send-Keys $p '{ESC}'
    Start-Sleep -Seconds 2

    Send-Keys $p 'f'
    Start-Sleep -Seconds 2
    Move-MouseToCenter $p
    Start-Sleep -Milliseconds 600
    Save-Shot $p '08-fullscreen-controls-visible' -FullScreen
    Start-Sleep -Seconds 5
    Save-Shot $p '08b-fullscreen-controls-hidden' -FullScreen
    Send-Keys $p '{ESC}'
    Start-Sleep -Seconds 2
    Save-Shot $p '08c-after-fullscreen'
    Stop-Aperture $p
}

Step 'Home' {
    $p = Start-Aperture
    Start-Sleep -Seconds 5
    Move-MouseAway
    Save-Shot $p '10-home'

    $card = Find-Element $p 'MediaCard'
    $box = $card.Current.BoundingRectangle
    Move-Mouse ([int]($box.X + $box.Width / 2)) ([int]($box.Y + $box.Height / 3))
    Start-Sleep -Milliseconds 700
    Save-Shot $p '13-home-hover'
    Move-MouseAway

    Set-WindowSize $p 1920 1040
    Save-Shot $p '11-home-1920'
    Set-WindowSize $p 1024 700
    Save-Shot $p '12-home-1024'
    Set-WindowSize $p 1280 800

    Send-Keys $p '{TAB}{TAB}{TAB}{TAB}'
    Start-Sleep -Milliseconds 600
    Save-Shot $p '14-home-keyboard-focus'

    Send-Keys $p '^f'
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait('light')
    Start-Sleep -Seconds 1
    Save-Shot $p '15-search-results'
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    [System.Windows.Forms.SendKeys]::SendWait('zebra')
    Start-Sleep -Seconds 1
    Save-Shot $p '16-search-no-results'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 500

    Send-Keys $p '^,'
    Start-Sleep -Seconds 1
    Move-MouseAway
    Save-Shot $p '17-settings'
    Stop-Aperture $p
}

Step 'File not found' {
    $p = Start-Aperture (Media 'Missing Episode.mp4')
    Start-Sleep -Seconds 3
    Save-Shot $p '18-error-not-found'
    Stop-Aperture $p
}

Step 'Not a video' {
    $p = Start-Aperture (Media 'Reading List.txt')
    Start-Sleep -Seconds 3
    Save-Shot $p '19-unsupported-file'
    Stop-Aperture $p
}

Step 'Unsupported codec' {
    $p = Start-Aperture (Media 'Archive Scan.mkv')
    Start-Sleep -Seconds 6
    Save-Shot $p '20-unsupported-format'
    Stop-Aperture $p
}

Step 'Damaged file' {
    $p = Start-Aperture (Media 'Damaged Copy.mp4')
    Start-Sleep -Seconds 6
    Save-Shot $p '21-error-damaged'
    Stop-Aperture $p
}

# ---------------------------------------------------------------------------------------
$logFile = Join-Path $dataDir 'Logs\aperture.log'
if (Test-Path $logFile) { Copy-Item $logFile (Join-Path $OutDir 'aperture.log') }
$notes | Set-Content (Join-Path $OutDir 'notes.txt')
if ($failures.Count -gt 0) {
    $failures | Set-Content (Join-Path $OutDir 'failures.txt')
    Write-Host "Failed steps:"; $failures | ForEach-Object { Write-Host " - $_" }
    exit 1
}
Write-Host 'All visual review steps completed.'
