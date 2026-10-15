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
$env:APERTURE_TRACE_CONTROLS = '1'   # TEMPORARY: logs why the controls hide
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

function Find-ByName($p, [string] $name, [int] $seconds = 5) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $element = (Get-Root $p).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

# Menus and flyouts can open in their own popup window; look through every window of the app.
function Find-InApp($p, [string] $id, [int] $seconds = 5) {
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
    $windows = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($window in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $windows)) {
            $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($element) { return $element }
        }
        Start-Sleep -Milliseconds 300
    }
    throw "UI element '$id' not found in any of Aperture's windows"
}

function Invoke-Element($element) {
    $pattern = $null
    if (-not $element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref] $pattern)) {
        # A toggle menu item does not always offer Invoke; toggling it is the same click.
        if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref] $pattern)) { $pattern.Toggle(); return }
        throw "'$($element.Current.AutomationId)' ($($element.Current.ControlType.ProgrammaticName), '$($element.Current.Name)') cannot be invoked"
    }
    $pattern.Invoke()
}
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
    Write-DumpAnalysis
}

# A crash inside WinUI (exception 0xc000027b) never reaches Aperture's own handler or log, and
# the event log names only the module. Windows Error Reporting keeps a dump of such a crash,
# and the debugger's analysis of it names the stowed exception (HRESULT, message and stack).
$dumpDir = Join-Path $OutDir 'dumps'
New-Item -ItemType Directory -Force -Path $dumpDir | Out-Null
try {
    $key = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\Aperture.exe'
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name DumpFolder -Value $dumpDir -Type ExpandString
    Set-ItemProperty -Path $key -Name DumpType -Value 2 -Type DWord
    Set-ItemProperty -Path $key -Name DumpCount -Value 3 -Type DWord
} catch { Write-Host "Crash dumps not enabled: $($_.Exception.Message)" }

function Write-DumpAnalysis {
    Start-Sleep -Seconds 5  # Windows Error Reporting writes the dump after the process is gone.
    $dump = Get-ChildItem $dumpDir -Filter *.dmp -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $dump) { Write-Host '  no crash dump'; return }
    $cdb = @("${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers\x64\cdb.exe", "$env:ProgramFiles\Windows Kits\10\Debuggers\x64\cdb.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $cdb) { Write-Host "  crash dump $($dump.Name) written, but no debugger to read it" }
    else {
        $analysis = & $cdb -z $dump.FullName -c '.symfix; .reload; !analyze -v; q' 2>&1 | Out-String
        $analysis | Set-Content (Join-Path $OutDir "crash-$($dump.BaseName).txt")
        $analysis -split "`n" | Select-String -Pattern 'STOWED|Stowed|HRESULT|ExceptionCode|EXCEPTION_|ERROR_CODE|Message|SYMBOL_NAME|FAILURE_BUCKET|STACK_TEXT|!' |
            Select-Object -First 80 | ForEach-Object { Write-Host "  dump: $($_.Line.TrimEnd())" }
    }
    Remove-Item $dump.FullName -Force -ErrorAction SilentlyContinue
}

function Step([string] $name, [scriptblock] $body) {
    Write-Host "=== $name"
    try { & $body }
    catch { $failures.Add("${name}: $($_.Exception.Message)"); Note "FAILED ${name}: $($_.Exception.Message)"; Write-Warning "${name} failed: $($_.Exception.Message)"; Write-CrashDetails }
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
    # C steps through the subtitle tracks and off; stop on an English track so 41 shows one.
    for ($i = 0; $i -lt 4; $i++) {
        Send-Keys $p 'c'
        Start-Sleep -Milliseconds 600
        $toast = (Find-Element $p 'ToastText').Current.Name
        if ($toast -match '(?i)eng') { break }
    }
    Note "Subtitle toast after C: $toast"
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

Step 'Seek preview, bookmarks, loop, sound' {
    $p = Start-Aperture (Media 'Night Drive.mp4')
    Start-Sleep -Seconds 3
    Move-MouseToCenter $p 60
    Start-Sleep -Milliseconds 150
    Move-MouseToCenter $p
    Start-Sleep -Milliseconds 600
    # Point at 30 % of the timeline; the picture is made by the hidden preview decoder.
    $box = (Find-Element $p 'Timeline').Current.BoundingRectangle
    $y = [int]($box.Top + $box.Height / 2)
    Move-Mouse ([int]($box.Left + $box.Width * 0.30) - 4) $y
    Start-Sleep -Milliseconds 200
    Move-Mouse ([int]($box.Left + $box.Width * 0.30)) $y
    # Rest on the timeline past the idle delay: the controls (and the preview) must stay.
    Start-Sleep -Seconds 4
    Save-Shot $p '100-seek-preview'
    try { Note "Seek preview time: $((Find-Element $p 'PreviewTime' 2).Current.Name)" } catch { Note 'Seek preview time text is not in the automation tree (the card is decorative); see 100-seek-preview.png' }

    Move-MouseToCenter $p
    Start-Sleep -Milliseconds 400
    Send-Keys $p 'b'
    Start-Sleep -Milliseconds 800
    $toast = (Find-Element $p 'ToastText').Current.Name
    Note "After B: $toast"
    if (-not $toast.StartsWith('Bookmarked')) { throw "B did not bookmark (the notice reads '$toast')" }
    Save-Shot $p '101-bookmark-toast'

    Send-Keys $p '+b'
    Start-Sleep -Seconds 1
    Find-Element $p 'BookmarksPanel' | Out-Null
    Find-Element $p 'AddBookmarkButton' | Out-Null
    Save-Shot $p '102-bookmarks-panel'
    Send-Keys $p '{ESC}'
    Start-Sleep -Milliseconds 600

    Send-Keys $p '+l'
    Start-Sleep -Seconds 3
    Send-Keys $p '+l'
    Start-Sleep -Milliseconds 800
    $chip = (Find-Element $p 'LoopChip').Current
    Note "Loop chip after Shift+L twice: $((Find-Element $p 'ToastText').Current.Name)"
    if ($chip.IsOffscreen) { throw 'the loop chip is not shown after setting a loop' }
    Move-MouseToCenter $p 30
    Start-Sleep -Milliseconds 400
    Save-Shot $p '103-loop-chip'

    Send-Keys $p 'd'
    Start-Sleep -Milliseconds 800
    $toast = (Find-Element $p 'ToastText').Current.Name
    Note "After D: $toast"
    if ($toast -ne 'Clearer dialogue on') { throw "D did not turn clearer dialogue on (the notice reads '$toast')" }
    Save-Shot $p '104-clear-dialogue-toast'

    Move-MouseToCenter $p 40
    Start-Sleep -Milliseconds 400
    Invoke-Element (Find-Element $p 'MoreButton')
    Start-Sleep -Seconds 1
    Save-Shot $p '105-more-menu-advanced'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 500

    Send-Keys $p '^,'
    Start-Sleep -Seconds 1
    $toggle = Find-Element $p 'ClearDialogueToggle'
    $scroll = $null
    if ($toggle.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref] $scroll)) { $scroll.ScrollIntoView() }
    Start-Sleep -Milliseconds 600
    Move-MouseAway
    Save-Shot $p '106-settings-sound'
    Stop-Aperture $p
}

Step 'Error panel: try again and copy a report' {
    $p = Start-Aperture (Media 'Damaged Copy.mp4')
    Start-Sleep -Seconds 6
    Find-Element $p 'ErrorRetryButton' | Out-Null
    Move-MouseAway
    Save-Shot $p '107-error-retry-copy'
    Invoke-Element (Find-Element $p 'ErrorCopyButton')
    Start-Sleep -Milliseconds 800
    $report = Get-Clipboard -Raw
    if (-not $report -or -not $report.StartsWith('Aperture diagnostic report')) { throw "Copy details did not put a report on the clipboard: '$report'" }
    if ($report.Contains($MediaDir) -or $report.Contains('Damaged Copy')) { throw 'the copied report still names the media folder or file' }
    Note "Copied report: $((($report -split "`r?`n") | Select-Object -First 8) -join ' | ')"
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
    # Typing a path and Enter opens that folder in the picker, leaving focus in its Folder
    # box; Tab moves to the commit button ("Add to library") and Enter presses it.
    if (-not (Find-ByName $p 'Add and scan' 2)) {
        [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
        Start-Sleep -Milliseconds 400
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Start-Sleep -Seconds 2
    }
    if (-not (Find-ByName $p 'Add and scan' 5)) {
        Save-Shot $p '61-folder-picker-stuck' -FullScreen
        throw 'the folder picker did not hand the folder to Aperture'
    }
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

    # The title bar search filters the library grid as you type.
    Send-Keys $p '^f'
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait('night')
    Start-Sleep -Seconds 1
    Move-MouseAway
    Save-Shot $p '64-library-search'
    $found = Get-CardCount $p
    Note "Library search for 'night': $found cards"
    if ($found -lt 1 -or $found -ge $first) { throw "searching the library for 'night' showed $found of $first cards" }
    # Esc clears the search first and stays on the library.
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Seconds 1
    $cleared = Get-CardCount $p
    if ($cleared -ne $first) { throw "clearing the search left $cleared of $first cards" }

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
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, ("Check details" + [char]0x2026))))
    if (-not $check) { throw 'the card menu has no Check details item' }
    Invoke-Element $check
    Start-Sleep -Seconds 1
    Find-Element $p 'TitleBox' | Out-Null
    Save-Shot $p '67-edit-details'
    # Esc closes the dialog and leaves the library where it was.
    Send-Keys $p '{ESC}'
    Start-Sleep -Milliseconds 800
    $stillOpen = $null
    try { $stillOpen = Find-Element $p 'TitleBox' 1 } catch { }
    if ($stillOpen) { throw 'Esc did not close Edit details' }
    Find-Element $p 'LibraryItems' 2 | Out-Null

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

# Lines Aperture has written to its log so far (read while it runs).
function Get-LogText {
    $log = Join-Path $dataDir 'Logs\aperture.log'
    if (-not (Test-Path $log)) { return '' }
    $stream = New-Object System.IO.FileStream $log, 'Open', 'Read', 'ReadWrite, Delete'
    try { return (New-Object System.IO.StreamReader $stream).ReadToEnd() } finally { $stream.Dispose() }
}

function Get-Title([string] $episode) { "Night Shift " + [char]0x00B7 + " $episode" }

function Assert-Playing($p, [string] $title, [int] $seconds = 10) {
    if (-not (Find-ByName $p $title $seconds)) { throw "expected '$title' to be playing" }
}

# The card is a Border, which UI Automation does not list; its Play now button stands for it.
function Wait-NextUp($p, [int] $seconds = 15) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $card = $null
        try { $card = Find-Element $p 'NextUpPlayButton' 1 } catch { }
        if ($card -and -not $card.Current.IsOffscreen) { return (Find-Element $p 'NextUpCaption' 2).Current.Name }
        Start-Sleep -Milliseconds 300
    }
    throw 'the next-episode card did not appear'
}

function Test-NextUpShown($p) {
    $card = $null
    try { $card = Find-Element $p 'NextUpPlayButton' 1 } catch { }
    return [bool]($card -and -not $card.Current.IsOffscreen)
}

$nightShift = 'Library\Shows\Night Shift\Season 1'
$pilot = Get-Title ("S1 E1 " + [char]0x00B7 + " Pilot")
$longHour = Get-Title ("S1 E2 " + [char]0x00B7 + " The Long Hour")
$third = Get-Title 'S1 E3'

Step 'Episodes' {
    # The library from the previous step holds Night Shift; give Aperture time to compare
    # its three episodes in the background (each run is logged as "Analysed <file>").
    $p = Start-Aperture
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        $done = ([regex]::Matches((Get-LogText), 'Analysed Night Shift S01E0[123]')).Count
        if ($done -ge 3) { break }
        Start-Sleep -Seconds 2
    }
    (Get-LogText) -split "`n" | Where-Object { $_ -like '*Analysed Night Shift*' } | ForEach-Object { Note "  $($_.Trim())" }
    if ($done -lt 3) { throw "only $done of the 3 Night Shift episodes were analysed" }
    Stop-Aperture $p

    # Each episode has a cold open, then the same 20 s opening (E1 at 0:10), so the intro is
    # found by comparison and offered as a button (two other episodes agree: not automatic).
    $p = Start-Aperture (Media "$nightShift\Night Shift S01E01 Pilot.mp4")
    Assert-Playing $p $pilot
    $skip = Find-Element $p 'SkipButton' 25
    Note "Skip button: '$($skip.Current.Name)'"
    if ($skip.Current.Name -ne 'Skip intro') { throw "the skip button says '$($skip.Current.Name)'" }
    Move-MouseAway
    Save-Shot $p '70-skip-intro'
    Invoke-Element $skip
    Start-Sleep -Seconds 1
    $timeline = Find-Element $p 'Timeline'
    $at = (Get-Range $timeline).Value
    Note "After Skip intro: $([math]::Round($at, 1)) s"
    if ($at -lt 29 -or $at -gt 40) { throw "Skip intro went to $at s instead of the end of the opening (0:30)" }

    # The credits (dark picture with the shared closing music) start at 2:00: the next
    # episode card appears and counts down.
    Set-Range $timeline 118
    $caption = Wait-NextUp $p
    Note "Next-up caption at the credits: '$caption'"
    if ($caption -notlike 'Next episode in*') { throw "the credits showed '$caption' instead of a countdown" }
    Move-MouseAway
    Save-Shot $p '71-next-episode-countdown'

    # Esc cancels it; the episode carries on.
    Send-Keys $p '{ESC}'
    Start-Sleep -Seconds 1
    if (Test-NextUpShown $p) { throw 'Esc did not put the next-episode card away' }
    Start-Sleep -Seconds 12
    Assert-Playing $p $pilot 2
    Save-Shot $p '72-countdown-cancelled'

    # At the very end the card comes back, without counting down, and Play now moves on.
    Set-Range $timeline 143
    $caption = Wait-NextUp $p
    Note "Next-up caption at the end after cancelling: '$caption'"
    if ($caption -like 'Next episode in*') { throw 'the countdown started again after it was cancelled' }
    Invoke-Element (Find-Element $p 'NextUpPlayButton')
    Assert-Playing $p $longHour 15
    Start-Sleep -Seconds 2
    Move-MouseAway
    Save-Shot $p '73-next-episode-playing'

    # Letting the countdown run out plays the next episode by itself.
    $timeline = Find-Element $p 'Timeline'
    Set-Range $timeline 118
    $caption = Wait-NextUp $p
    if ($caption -notlike 'Next episode in*') { throw "E2's credits showed '$caption' instead of a countdown" }
    Assert-Playing $p $third 25
    Note 'Autoplay opened S1 E3 when the countdown ended'

    # The correction panel, from More options. It is enabled once the file's fingerprint is
    # known, which takes a moment after autoplay opens the episode; the menu reads it on opening.
    $deadline = (Get-Date).AddSeconds(20)
    while ($true) {
        Start-Sleep -Seconds 2
        Move-MouseToCenter $p
        Invoke-Element (Find-Element $p 'MoreButton')
        Start-Sleep -Milliseconds 800
        $item = Find-InApp $p 'SegmentsMenuItem' 5
        if ($item.Current.IsEnabled) { break }
        if ((Get-Date) -gt $deadline) { throw 'Intro and credits stayed unavailable for S1 E3' }
        Send-Keys $p '{ESC}'
    }
    Invoke-Element $item
    Start-Sleep -Seconds 1
    Find-Element $p 'ConfirmSegmentButton' 3 | Out-Null
    Move-MouseAway
    Save-Shot $p '74-intro-credits-panel'
    Stop-Aperture $p

    # With autoplay turned off, the card offers the next episode but never counts down.
    $p = Start-Aperture
    Start-Sleep -Seconds 3
    Send-Keys $p '^,'
    Start-Sleep -Seconds 1
    $toggle = Find-Element $p 'AutoplayToggle'
    $toggle.SetFocus()
    $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Seconds 1
    Move-MouseAway
    Save-Shot $p '76-settings-episodes'
    Send-Keys $p '%{LEFT}'
    Start-Sleep -Seconds 2
    Move-MouseAway
    Save-Shot $p '77-home-up-next'
    Stop-Aperture $p

    $p = Start-Aperture (Media "$nightShift\Night Shift S01E01 Pilot.mp4")
    Assert-Playing $p $pilot
    Start-Sleep -Seconds 2
    $timeline = Find-Element $p 'Timeline'
    Set-Range $timeline 118
    $caption = Wait-NextUp $p
    Note "Next-up caption with autoplay off: '$caption'"
    if ($caption -like 'Next episode in*') { throw 'the countdown ran with autoplay turned off' }
    Start-Sleep -Seconds 15
    Assert-Playing $p $pilot 2
    Move-MouseAway
    Save-Shot $p '75-autoplay-off'
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

# YouTube needs the network; GitHub's Windows runners have it. No API key is configured,
# so this covers the keyless path: consent, pasting a link, the embedded player (oEmbed
# for details), saving, the unavailable-video panel and the settings group.
function Wait-Log([string] $text, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-LogText).Contains($text)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# Types a link into the title bar's search box and submits it. The box is focused through UI
# Automation and its text checked before Enter, so a keystroke lost to another window shows up
# as a clear failure here rather than as a player that never answers.
function Open-YouTubeLink($p, [string] $url) {
    # The AutoSuggestBox itself takes no focus; its text field does.
    $box = Find-Element $p 'SearchBox'
    $field = $box.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
    if (-not $field) { throw 'the search box has no text field' }
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        [Win]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
        $field.SetFocus()
        Start-Sleep -Milliseconds 400
        [System.Windows.Forms.SendKeys]::SendWait('^a{BACKSPACE}' + $url)
        Start-Sleep -Milliseconds 400
        $value = $null
        if ($field.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref] $value) -and $value.Current.Value -eq $url) {
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            return
        }
        Note "Search box held '$($value.Current.Value)' instead of the link (attempt $attempt)"
    }
    throw 'the link could not be typed into the search box'
}

Step 'YouTube' {
    $p = Start-Aperture
    Start-Sleep -Seconds 3
    Invoke-Element (Find-Element $p 'YouTubeButton')
    Find-Element $p 'YouTubeAcceptButton' | Out-Null
    Move-MouseAway
    Save-Shot $p '80-youtube-consent'

    Invoke-Element (Find-Element $p 'YouTubeAcceptButton')
    Find-Element $p 'YouTubeNotice' | Out-Null
    Save-Shot $p '81-youtube-no-key'

    Open-YouTubeLink $p 'https://www.youtube.com/watch?v=jNQXAC9IVRw'
    # YouTube decides whether it serves a video to a cloud machine. Either outcome proves the
    # embed works end to end (page, IFrame API, events back to Aperture): it plays, or YouTube's
    # player reports an error, which Aperture must explain in place of the player.
    $deadline = (Get-Date).AddSeconds(60)
    $outcome = $null
    while (-not $outcome -and (Get-Date) -lt $deadline) {
        $text = Get-LogText
        if ($text.Contains('YouTube player: playing.')) { $outcome = 'playing' }
        elseif ($text -match 'YouTube player error (\d+)') { $outcome = "error $($Matches[1])" }
        else { Start-Sleep -Milliseconds 500 }
    }
    if (-not $outcome) {
        Save-Shot $p '82-youtube-player-failed'
        throw 'the YouTube player neither played nor reported an error'
    }
    Start-Sleep -Seconds 3
    Move-MouseAway
    if ($outcome -eq 'playing') {
        Note 'YouTube: the video played in the embedded player'
        Save-Shot $p '82-youtube-player'
        # Nothing Aperture draws may cover the embedded player while it plays.
        foreach ($id in 'YouTubeProblemTitle', 'YouTubeNextCaption') {
            $over = $null
            try { $over = Find-Element $p $id 1 } catch { }
            if ($over -and -not $over.Current.IsOffscreen) { throw "'$id' is showing over the player" }
        }
    } else {
        $refused = Find-Element $p 'YouTubeProblemTitle' 5
        Note "YouTube: playback NOT confirmed; YouTube's player refused this machine ($outcome), shown as '$($refused.Current.Name)'"
        Save-Shot $p '82-youtube-player-refused'
    }
    Invoke-Element (Find-Element $p 'YouTubeSaveButton')
    Start-Sleep -Seconds 1
    Stop-Aperture $p

    # Saved survives a restart, and the consent is not asked again.
    $p = Start-Aperture
    Start-Sleep -Seconds 3
    Invoke-Element (Find-Element $p 'YouTubeButton')
    if (-not (Find-ByName $p 'Saved' 10)) { throw 'the Saved shelf did not appear after a restart' }
    Start-Sleep -Seconds 2
    Move-MouseAway
    Save-Shot $p '83-youtube-saved'

    # A video id that does not exist is explained, not played or left blank.
    Open-YouTubeLink $p 'https://www.youtube.com/watch?v=Ap3rtur3x0Q'
    $title = Find-Element $p 'YouTubeProblemTitle' 30
    Note "Unavailable video: $($title.Current.Name)"
    Move-MouseAway
    Save-Shot $p '84-youtube-unavailable'
    Stop-Aperture $p

    $p = Start-Aperture
    Start-Sleep -Seconds 3
    Send-Keys $p '^,'
    Start-Sleep -Seconds 1
    $toggle = Find-Element $p 'YouTubeAutoplayToggle'
    $scroll = $null
    if ($toggle.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref] $scroll)) { $scroll.ScrollIntoView() }
    $toggle.SetFocus()
    Start-Sleep -Seconds 1
    if ($toggle.Current.IsOffscreen) { throw 'Settings did not scroll to the YouTube group' }
    Move-MouseAway
    Save-Shot $p '85-settings-youtube'
    Stop-Aperture $p
}

# The media server: turned on from Settings, reached over the network like a phone would,
# and still running after Aperture closes, until it is stopped from Settings.
function Get-ServerCode([string] $path) {
    $code = & curl.exe -sk -o NUL -w '%{http_code}' --max-time 5 "https://127.0.0.1:8443/$path"
    return "$code"
}

function Wait-ServerCaption($p, [string] $prefix, [int] $seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $caption = (Find-Element $p 'ServerStatusCaption').Current.Name
        if ($caption.StartsWith($prefix)) { return $caption }
        Start-Sleep -Milliseconds 500
    }
    throw "the media server status never read '$prefix' (it reads '$caption')"
}

function Show-ServerSettings($p) {
    Send-Keys $p '^,'
    Start-Sleep -Seconds 1
    $button = Find-Element $p 'ServerStartButton'
    $scroll = $null
    if ($button.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref] $scroll)) { $scroll.ScrollIntoView() }
    $button.SetFocus()
    Start-Sleep -Seconds 1
    if ($button.Current.IsOffscreen) { throw 'Settings did not scroll to the media server group' }
    return $button
}

Step 'Media server' {
    $p = Start-Aperture
    Start-Sleep -Seconds 3
    $button = Show-ServerSettings $p
    $off = Wait-ServerCaption $p 'Off'
    Note "Media server before starting: $off"
    Move-MouseAway
    Save-Shot $p '90-settings-media-server'

    # Share the test library and set a passphrase, as a person would.
    $share = Find-ByName $p 'Share Library' 10
    if (-not $share) { throw 'the Library folder has no share switch' }
    Invoke-Element $share
    foreach ($id in 'PassphraseBox', 'PassphraseAgainBox') {
        (Find-Element $p $id).SetFocus()
        Start-Sleep -Milliseconds 300
        [System.Windows.Forms.SendKeys]::SendWait('lantern river 42')
    }
    Invoke-Element (Find-Element $p 'SavePassphraseButton')
    Start-Sleep -Seconds 1
    Note "Passphrase: $((Find-Element $p 'PassphraseCaption').Current.Name)"

    Invoke-Element $button
    $on = Wait-ServerCaption $p 'On'
    Note "Media server after Start: $on"
    $setupCode = & curl.exe -s -o NUL -w '%{http_code}' --max-time 5 'http://127.0.0.1:8080/'
    if ("$setupCode" -ne '200') { throw "the setup page answered $setupCode" }
    if ((Get-ServerCode 'api/library') -ne '401') { throw 'the library answered without signing in' }

    # Sign in over HTTPS like a phone. (TLS trust itself is covered by the server tests.)
    $body = Join-Path $env:TEMP 'aperture-signin.json'
    [System.IO.File]::WriteAllText($body, '{"passphrase":"lantern river 42","deviceName":"CI phone"}')
    $jar = Join-Path $env:TEMP 'aperture-cookies.txt'
    $signIn = & curl.exe -sk -o NUL -w '%{http_code}' --max-time 10 -c $jar -H 'X-Aperture: 1' -H 'Content-Type: application/json' --data-binary "@$body" 'https://127.0.0.1:8443/api/session'
    if ("$signIn" -ne '200') { throw "signing in answered $signIn" }
    $library = & curl.exe -sk --max-time 10 -b $jar 'https://127.0.0.1:8443/api/library'
    if ("$library" -notmatch 'Night Shift') { throw "the shared library didn't list the test shows: $library" }
    Note 'Media server: signed in over HTTPS and listed the shared library'
    if (-not (Find-ByName $p 'Sign out CI phone' 15)) { throw 'the signed-in device did not appear in Settings' }
    Start-Sleep -Seconds 1
    Move-MouseAway
    Save-Shot $p '91-settings-media-server-running'
    $connect = Find-Element $p 'ServerSetupText'
    Note "Connect a device shows: $($connect.Current.Name)"

    # Closing Aperture leaves the server running.
    Stop-Aperture $p
    Start-Sleep -Seconds 2
    if ((Get-ServerCode 'api/session') -ne '200') { throw 'the media server stopped when Aperture closed' }
    Note 'Media server: still answering after Aperture closed'

    # Stopping it from Settings, and starting it again.
    $p = Start-Aperture
    Start-Sleep -Seconds 3
    $button = Show-ServerSettings $p
    Wait-ServerCaption $p 'On' | Out-Null
    Invoke-Element $button
    Wait-ServerCaption $p 'Off' | Out-Null
    if ((Get-ServerCode 'api/session') -ne '000') { throw 'the media server still answers after Stop' }
    Invoke-Element $button
    Wait-ServerCaption $p 'On' | Out-Null
    if ((Get-ServerCode 'api/session') -ne '200') { throw 'the media server did not answer after starting again' }
    Invoke-Element $button
    Wait-ServerCaption $p 'Off' | Out-Null
    Note 'Media server: stopped, started again and stopped from Settings'
    Stop-Aperture $p
}

# Play on a TV: a stand-in DLNA TV (fake-tv.py) answers on the network, Aperture finds it,
# casts to it, and the TV really fetches the media from the media server.
function Wait-CastHeading($p, [string] $prefix, [int] $seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $heading = (Find-Element $p 'CastHeading').Current.Name
        if ($heading.StartsWith($prefix)) { return $heading }
        Start-Sleep -Milliseconds 500
    }
    throw "the casting view never read '$prefix' (it reads '$heading')"
}

function Open-CastPicker($p) {
    Move-MouseToCenter $p 40
    Start-Sleep -Milliseconds 400
    Invoke-Element (Find-Element $p 'MoreButton')
    Start-Sleep -Milliseconds 800
    Invoke-Element (Find-InApp $p 'CastMenuItem' 5)
    Start-Sleep -Seconds 1
}

Step 'Casting' {
    $tvLog = Join-Path $OutDir 'fake-tv.log'
    Remove-Item $tvLog -ErrorAction SilentlyContinue
    $tv = Start-Process -FilePath 'python' -ArgumentList "`"$PSScriptRoot\fake-tv.py`" --port 49500 --log `"$tvLog`"" -PassThru -WindowStyle Hidden
    try {
        Start-Sleep -Seconds 2
        if ($tv.HasExited) { throw 'the stand-in TV did not start' }
        $p = Start-Aperture (Media 'Harbour Lights.mp4')
        Start-Sleep -Seconds 3
        Send-Keys $p ' '   # paused, so the place cast from stays put
        Start-Sleep -Milliseconds 500

        # The media server is off (the last step stopped it): the picker says so and offers to start it.
        Open-CastPicker $p
        Note "Cast picker with the server off: $((Find-Element $p 'CastPickerStatus').Current.Name)"
        Move-MouseAway
        Save-Shot $p '95-cast-picker-server-off'
        Invoke-Element (Find-Element $p 'CastStartServerButton')
        $renderer = $null
        $deadline = (Get-Date).AddSeconds(45)
        while (-not $renderer -and (Get-Date) -lt $deadline) {
            # A search takes a little over 3 s; wait longer than that before searching again,
            # since Search again clears the list.
            try { $renderer = Find-Element $p 'CastRenderer' 10 } catch { }
            if (-not $renderer) {
                try { $search = Find-Element $p 'CastSearchButton' 1; if ($search.Current.IsEnabled -and -not $search.Current.IsOffscreen) { Invoke-Element $search } } catch { }
            }
        }
        if (-not $renderer) { throw "no TV was found (picker reads '$((Find-Element $p 'CastPickerStatus').Current.Name)')" }
        Note "Cast picker lists: $($renderer.Current.Name)"
        Move-MouseAway
        Save-Shot $p '96-cast-picker'

        Invoke-Element $renderer
        $heading = Wait-CastHeading $p 'Playing on' 45
        Note "Casting view: $heading; delivery: $((Find-Element $p 'CastDelivery').Current.Name)"
        Start-Sleep -Seconds 3
        Note "Casting position: $((Find-Element $p 'CastPositionText').Current.Name)"
        Move-MouseAway
        Save-Shot $p '97-casting'

        Send-Keys $p ' '
        Note "After Space: $(Wait-CastHeading $p 'Paused on' 15)"
        Invoke-Element (Find-Element $p 'StopCastingButton')
        $toast = ''
        $deadline = (Get-Date).AddSeconds(15)
        while (-not $toast.StartsWith('Stopped casting') -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 300
            $toast = (Find-Element $p 'ToastText').Current.Name
        }
        if (-not $toast.StartsWith('Stopped casting')) { throw "stopping didn't say where it stopped (the notice reads '$toast')" }
        Note "After Stop casting: $toast"
        Move-MouseAway
        Save-Shot $p '98-cast-stopped'

        $log = Get-Content $tvLog -Raw
        if ($log -notmatch 'fetched 20[06] ') { throw "the TV never fetched the media: $log" }
        if ($log -notmatch 'Stop -> STOPPED') { throw "the TV was not told to stop: $log" }
        Note 'Casting: the stand-in TV fetched the media, paused and stopped'
        Stop-Aperture $p
    }
    finally {
        if (-not $tv.HasExited) { Stop-Process -Id $tv.Id -Force -ErrorAction SilentlyContinue }
        $server = Join-Path (Split-Path $Exe) 'Server\Aperture.Server.exe'
        & $server stop | Out-Null
    }
}

# ---------------------------------------------------------------------------------------
$logFile = Join-Path $dataDir 'Logs\aperture.log'
if (Test-Path $logFile) { Copy-Item $logFile (Join-Path $OutDir 'aperture.log') }
$serverLog = Join-Path $dataDir 'Logs\server.log'
if (Test-Path $serverLog) { Copy-Item $serverLog (Join-Path $OutDir 'server.log') }
# Any exception that reached Aperture's last-chance handler fails the review, even if the
# step that caused it carried on.
if ((Test-Path $logFile) -and (Select-String -Path $logFile -Pattern 'Unhandled exception' -SimpleMatch -Quiet)) {
    $failures.Add('Aperture logged an unhandled exception (see aperture.log)')
}
$notes | Set-Content (Join-Path $OutDir 'notes.txt')
if ($failures.Count -gt 0) {
    $failures | Set-Content (Join-Path $OutDir 'failures.txt')
    Write-Host "Failed steps:"; $failures | ForEach-Object { Write-Host " - $_" }
    exit 1
}
Write-Host 'All visual review steps completed.'
