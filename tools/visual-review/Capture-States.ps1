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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
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

function Write-CrashDetails {
    $log = Join-Path $dataDir 'Logs\aperture.log'
    if (Test-Path $log) { Get-Content $log -Tail 40 | ForEach-Object { Write-Host "  log: $_" } }
    Get-WinEvent -LogName Application -MaxEvents 10 -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in '.NET Runtime', 'Application Error' } |
        ForEach-Object { Write-Host "  event: $($_.Message)" }
}

function Step([string] $name, [scriptblock] $body) {
    Write-Host "=== $name"
    try { & $body }
    catch { $failures.Add("${name}: $($_.Exception.Message)"); Write-Warning "${name} failed: $($_.Exception.Message)"; Write-CrashDetails }
    Get-Process -Name Aperture -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Media([string] $name) { Join-Path $MediaDir $name }

# Average colour of a 24 x 24 block centred at a fraction of the window's width and height.
function Get-Colour($p, [double] $fx, [double] $fy) {
    $r = Get-WindowRect $p
    $cx = [int]($r.Left + ($r.Right - $r.Left) * $fx); $cy = [int]($r.Top + ($r.Bottom - $r.Top) * $fy)
    $bitmap = New-Object System.Drawing.Bitmap 24, 24
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($cx - 12, $cy - 12, 0, 0, $bitmap.Size)
    $graphics.Dispose()
    $sr = 0; $sg = 0; $sb = 0
    for ($x = 0; $x -lt 24; $x++) { for ($y = 0; $y -lt 24; $y++) { $c = $bitmap.GetPixel($x, $y); $sr += $c.R; $sg += $c.G; $sb += $c.B } }
    $bitmap.Dispose()
    return @{ R = [int]($sr / 576); G = [int]($sg / 576); B = [int]($sb / 576) }
}

# Orientation Check.mp4 is amber on top and blue below; prove both reach the screen, upright.
function Assert-VideoUpright($p, [string] $label) {
    $top = Get-Colour $p 0.5 0.30
    $bottom = Get-Colour $p 0.5 0.66
    Note "${label}: top rgb($($top.R),$($top.G),$($top.B)) bottom rgb($($bottom.R),$($bottom.G),$($bottom.B))"
    $topAmber = $top.R -gt 150 -and $top.R -gt $top.B + 60
    $bottomBlue = $bottom.B -gt 120 -and $bottom.B -gt $bottom.R + 40
    if ($topAmber -and $bottomBlue) { return }
    $flipped = ($bottom.R -gt 150 -and $bottom.R -gt $bottom.B + 60) -and ($top.B -gt 120 -and $top.B -gt $top.R + 40)
    if ($flipped) { throw "${label}: the picture is upside down" }
    throw "${label}: the video did not reach the screen"
}

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
    # The pointer may already be at the centre from the previous step; move it so the
    # window sees real pointer movement.
    Move-MouseToCenter $p 60
    Start-Sleep -Milliseconds 150
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

Step 'Nothing playable' {
    $p = Start-Aperture (Media 'Subtitles Only.mkv')
    Start-Sleep -Seconds 4
    Save-Shot $p '20-unsupported-format'
    Stop-Aperture $p
}

Step 'Video reaches the screen (GPU path)' {
    $p = Start-Aperture (Media 'Orientation Check.mp4')
    Start-Sleep -Seconds 3
    Move-MouseAway
    Start-Sleep -Seconds 5
    Save-Shot $p '30-render-gpu'
    Assert-VideoUpright $p 'GPU path'
    Set-WindowSize $p 1600 900
    Start-Sleep -Seconds 1
    Assert-VideoUpright $p 'GPU path after resize'
    Send-Keys $p 'f'
    Start-Sleep -Seconds 3
    Save-Shot $p '31-render-gpu-fullscreen' -FullScreen
    Assert-VideoUpright $p 'GPU path in full screen'
    Stop-Aperture $p
}

Step 'Video reaches the screen (software path)' {
    $env:APERTURE_VIDEO_RENDERER = 'software'
    try {
        $p = Start-Aperture (Media 'Orientation Check.mp4')
        Start-Sleep -Seconds 3
        Move-MouseAway
        Start-Sleep -Seconds 5
        Save-Shot $p '32-render-software'
        Assert-VideoUpright $p 'Software path'
        Stop-Aperture $p
    } finally { Remove-Item Env:APERTURE_VIDEO_RENDERER }
}

Step 'FFV1 plays' {
    $p = Start-Aperture (Media 'Archive Scan.mkv')
    Start-Sleep -Seconds 3
    $timeline = Find-Element $p 'Timeline'
    $before = (Get-Range $timeline).Value
    Start-Sleep -Seconds 2
    $after = (Get-Range $timeline).Value
    if ($after -le $before) { throw "FFV1 did not play ($before -> $after s)" }
    Note "Archive Scan.mkv (FFV1): position advanced $([math]::Round($before,1)) -> $([math]::Round($after,1)) s"
    Move-MouseAway
    Start-Sleep -Seconds 4
    Save-Shot $p '33-ffv1-playing'
    Stop-Aperture $p
}

Step 'Tracks, subtitles, info, picture shape, speed' {
    $p = Start-Aperture (Media 'Night Market.mkv')
    Start-Sleep -Seconds 3
    Send-Keys $p 'c'
    Start-Sleep -Milliseconds 600
    Save-Shot $p '40-subtitles-toast'
    Move-MouseAway
    Start-Sleep -Seconds 5
    Save-Shot $p '41-subtitles-shown'
    Move-MouseToCenter $p 40
    Start-Sleep -Milliseconds 400
    Invoke-Element (Find-Element $p 'TracksButton')
    Start-Sleep -Seconds 1
    Save-Shot $p '42-tracks-menu'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 500
    Send-Keys $p 'a'
    Start-Sleep -Milliseconds 600
    Save-Shot $p '43-audio-switched'
    Send-Keys $p 'i'
    Start-Sleep -Seconds 2
    Save-Shot $p '44-playback-info'
    # The info rows are plain text blocks (layout panels are not in the automation tree), so
    # read every text in the window from the panel's close button onwards.
    Find-Element $p 'InfoCloseButton' | Out-Null
    $texts = (Get-Root $p).FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }
    $start = [array]::IndexOf(@($texts), 'Close playback info')
    if ($start -lt 0) { throw 'The playback info panel did not open' }
    Note "Playback info: $(@($texts)[($start + 1)..(@($texts).Count - 1)] -join ' | ')"
    Send-Keys $p 'i'
    Send-Keys $p 'r'
    Start-Sleep -Milliseconds 800
    Save-Shot $p '45-picture-fill'
    Send-Keys $p 'rrrrr'
    Send-Keys $p ']'
    Start-Sleep -Milliseconds 500
    Save-Shot $p '46-speed-toast'
    Stop-Aperture $p
}

Step 'Damaged file' {
    $p = Start-Aperture (Media 'Damaged Copy.mp4')
    Start-Sleep -Seconds 6
    Save-Shot $p '21-error-damaged'
    Stop-Aperture $p
}

Step 'Sync and subtitle look panel, shortcuts panel' {
    $p = Start-Aperture (Media 'Night Market.mkv')
    Start-Sleep -Seconds 3
    Send-Keys $p 'xxx'
    Start-Sleep -Milliseconds 600
    Save-Shot $p '47-subtitle-delay-toast'
    Send-Keys $p 'g'
    Start-Sleep -Seconds 1
    $offset = (Find-Element $p 'SubtitleOffsetText').Current.Name
    Note "Subtitle offset after three presses of X: $offset"
    if ($offset -notmatch '0\.3') { throw "the subtitle offset reads '$offset', expected +0.3 s" }
    Save-Shot $p '48-adjust-panel'
    Send-Keys $p '{ESC}'
    Start-Sleep -Milliseconds 600
    Send-Keys $p '{F1}'
    Start-Sleep -Seconds 1
    Find-Element $p 'ShortcutsCloseButton' | Out-Null
    Save-Shot $p '49-shortcuts-panel'
    Send-Keys $p '{ESC}'
    Stop-Aperture $p
}

Step 'Shortcut editor' {
    $p = Start-Aperture
    Start-Sleep -Seconds 3
    Send-Keys $p '^,'
    Start-Sleep -Seconds 1
    Invoke-Element (Find-Element $p 'ShortcutsButton')
    Start-Sleep -Seconds 1
    Move-MouseAway
    Save-Shot $p '50-shortcut-editor'
    Send-Keys $p '{ESC}'
    Stop-Aperture $p
}

function Get-CardCount($p) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MediaCard')
    return @((Find-Element $p 'LibraryItems').FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)).Count
}

function Wait-ScanDone($p, [int] $seconds = 60) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $cancel = $null
        try { $cancel = Find-Element $p 'CancelScanButton' 1 } catch { }
        if (-not $cancel -or $cancel.Current.IsOffscreen) { return }
        Start-Sleep -Milliseconds 500
    }
    throw 'the scan did not finish'
}

Step 'Library' {
    $p = Start-Aperture
    Start-Sleep -Seconds 3
    Send-Keys $p '^l'
    Start-Sleep -Seconds 1
    Move-MouseAway
    Save-Shot $p '60-library-no-folders'

    # Add the test library through the real folder picker: type its path, then choose it.
    Invoke-Element (Find-Element $p 'AddFirstFolderButton')
    Start-Sleep -Seconds 3
    [System.Windows.Forms.SendKeys]::SendWait((Media 'Library'))
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Seconds 2
    # Typing a path and Enter opens that folder in the picker; its commit button then picks it.
    # ([NullString]::Value: PowerShell would pass $null to a string parameter as "".)
    $pickerHandle = [Win]::FindWindow('#32770', [NullString]::Value)
    if ($pickerHandle -ne [IntPtr]::Zero) {
        $picker = [System.Windows.Automation.AutomationElement]::FromHandle($pickerHandle)
        # The commit button is the dialog's IDOK button; list rows can carry the same id.
        $select = $picker.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, '1')),
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))))
        if (-not $select) { throw 'the folder picker has no commit button' }
        Note "Folder picker commit button: $($select.Current.Name)"
        Invoke-Element $select
        Start-Sleep -Seconds 2
    }
    if ([Win]::FindWindow('#32770', [NullString]::Value) -ne [IntPtr]::Zero) { throw 'the folder picker is still open' }
    Save-Shot $p '61-add-folder-kind'
    Send-Keys $p '{ENTER}'
    Start-Sleep -Milliseconds 800
    Save-Shot $p '62-library-scanning'
    Wait-ScanDone $p
    Start-Sleep -Seconds 2
    Move-MouseAway
    Save-Shot $p '63-library'
    $first = Get-CardCount $p
    Note "Library after the first scan: $first cards"
    if ($first -lt 8) { throw "only $first library items appeared" }

    Invoke-Element (Find-Element $p 'ScanButton')
    Start-Sleep -Seconds 1
    Wait-ScanDone $p
    Start-Sleep -Seconds 2
    $second = Get-CardCount $p
    Note "Library after scanning again: $second cards"
    if ($second -ne $first) { throw "scanning again changed the item count ($first -> $second)" }

    $filter = Find-Element $p 'FilterBox'
    $filter.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('night')
    Start-Sleep -Seconds 1
    Save-Shot $p '64-library-filtered'
    $filter.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
    Start-Sleep -Seconds 1

    Set-WindowSize $p 1024 700
    Save-Shot $p '65-library-1024'
    Set-WindowSize $p 1280 800

    # The item with nothing to go on is marked; open its menu from the keyboard and check it.
    $cards = (Find-Element $p 'LibraryItems').FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MediaCard')))
    $untitled = $cards | Where-Object { $_.Current.Name -like '*Untitled Project*' } | Select-Object -First 1
    if (-not $untitled) { throw 'Untitled Project is not in the library' }
    $untitled.SetFocus()
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait('+{F10}')
    Start-Sleep -Seconds 1
    Save-Shot $p '66-card-menu'
    $check = (Get-Root $p).FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Check details…')))
    if (-not $check) { throw 'the card menu has no Check details item' }
    Invoke-Element $check
    Start-Sleep -Seconds 1
    Find-Element $p 'TitleBox' | Out-Null
    Save-Shot $p '67-edit-details'
    Send-Keys $p '{ESC}'
    Start-Sleep -Milliseconds 600

    Send-Keys $p '%{LEFT}'
    Start-Sleep -Seconds 2
    Move-MouseAway
    Save-Shot $p '68-home-with-library'
    Stop-Aperture $p

    # The library is read back from disk after a restart.
    $p = Start-Aperture
    Start-Sleep -Seconds 3
    Send-Keys $p '^l'
    Start-Sleep -Seconds 2
    $after = Get-CardCount $p
    Note "Library after a restart: $after cards"
    if ($after -ne $first) { throw "the library changed across a restart ($first -> $after)" }
    Stop-Aperture $p
}

Step 'Resume choice' {
    $p = Start-Aperture
    Start-Sleep -Seconds 3
    Send-Keys $p '^,'
    Start-Sleep -Seconds 1
    (Find-Element $p 'ResumeBox').SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
    Start-Sleep -Seconds 1
    Stop-Aperture $p

    $p = Start-Aperture (Media 'Harbour Lights.mp4')
    Start-Sleep -Seconds 3
    Find-Element $p 'ResumeButton' | Out-Null
    Move-MouseAway
    Save-Shot $p '35-resume-choice'
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
