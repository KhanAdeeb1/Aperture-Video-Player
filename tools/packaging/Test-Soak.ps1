<#
.SYNOPSIS
    Long-running checks on the real app: repeated open/close, then extended playback with
    memory, CPU, handle and thread samples.

.DESCRIPTION
    1. Opens a video and closes the window -Cycles times. Each run must open its window,
       keep running for a few seconds and exit cleanly when closed.
    2. Plays -Video for -Minutes, sampling the process every -SampleSeconds: private
       memory, working set, CPU (percent of the whole machine), handles and threads, and
       the timeline's position read through UI Automation.
    It fails when a cycle crashes, the app exits during playback, playback stops moving,
    or private memory keeps growing (the last stretch is more than 20 % and 64 MB above the
    settled level after warm-up). Results go to <OutDir>/soak.csv and a Markdown summary,
    which is also added to the GitHub job summary when there is one.
    Windows PowerShell 5.1 or PowerShell 7. ASCII only (5.1 reads files without a BOM as ANSI).
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Video,
    [Parameter(Mandatory)] [string] $OutDir,
    [int] $Minutes = 120,
    [int] $Cycles = 50,
    [int] $SampleSeconds = 15
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$OutDir = [IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (-not $env:APERTURE_DATA_DIR) { $env:APERTURE_DATA_DIR = Join-Path $OutDir 'profile' }
$summary = New-Object System.Collections.Generic.List[string]
$failures = New-Object System.Collections.Generic.List[string]
function Say([string] $text) { Write-Host $text; $summary.Add($text) }

function Start-App {
    $p = Start-Process -FilePath $Exe -ArgumentList "`"$Video`"" -PassThru
    $deadline = (Get-Date).AddSeconds(30)
    do { Start-Sleep -Milliseconds 250; $p.Refresh() } until ($p.HasExited -or $p.MainWindowHandle -ne [IntPtr]::Zero -or (Get-Date) -gt $deadline)
    return $p
}

function Get-Position($p) {
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'Timeline')
        $timeline = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if (-not $timeline) { return $null }
        return $timeline.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
    } catch { return $null }
}

function Median([double[]] $values) {
    if (-not $values -or $values.Count -eq 0) { return 0 }
    $sorted = $values | Sort-Object
    return $sorted[[int][math]::Floor(($sorted.Count - 1) / 2)]
}

# ---- 1. Open and close ---------------------------------------------------------------------
Say "## Open and close, $Cycles times"
$cycleTimes = New-Object System.Collections.Generic.List[double]
for ($i = 1; $i -le $Cycles; $i++) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-App
    if ($p.HasExited -or $p.MainWindowHandle -eq [IntPtr]::Zero) {
        $failures.Add("cycle $($i): the window did not open (exit code $(if ($p.HasExited) { $p.ExitCode } else { 'none' }))")
        if (-not $p.HasExited) { $p.Kill() }
        continue
    }
    $opened = $watch.Elapsed.TotalSeconds
    Start-Sleep -Seconds 3
    $p.Refresh()
    if ($p.HasExited) { $failures.Add("cycle $($i): exited while playing (code $($p.ExitCode))"); continue }
    $null = $p.CloseMainWindow()
    if (-not $p.WaitForExit(20000)) { $failures.Add("cycle $($i): did not close within 20 s"); $p.Kill(); continue }
    if ($p.ExitCode -ne 0) { $failures.Add("cycle $($i): exit code $($p.ExitCode) on close") }
    $cycleTimes.Add($opened)
}
Say ("Completed {0} of {1} cycles; window opened in {2:0.0} s (median), {3:0.0} s (slowest)." -f $cycleTimes.Count, $Cycles, (Median $cycleTimes.ToArray()), ($cycleTimes | Measure-Object -Maximum).Maximum)

# ---- 2. Extended playback ------------------------------------------------------------------
Say ""
Say "## Playback for $Minutes minutes"
# A fresh profile, so the video starts from the beginning instead of offering to resume.
$env:APERTURE_DATA_DIR = Join-Path $OutDir 'profile-playback'
$p = Start-App
if ($p.HasExited -or $p.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Aperture did not open for the playback run' }
$cores = [Environment]::ProcessorCount
$csv = Join-Path $OutDir 'soak.csv'
'minute,private_mb,working_set_mb,cpu_percent,handles,threads,position_s' | Set-Content -Encoding ascii $csv
$samples = New-Object System.Collections.Generic.List[object]
$start = Get-Date
$lastCpu = $p.TotalProcessorTime
$lastTime = $start
$end = $start.AddMinutes($Minutes)
while ((Get-Date) -lt $end) {
    Start-Sleep -Seconds $SampleSeconds
    $p.Refresh()
    if ($p.HasExited) { $failures.Add(("the app exited during playback at minute {0:0.0} (code {1})" -f ((Get-Date) - $start).TotalMinutes, $p.ExitCode)); break }
    $now = Get-Date
    $cpu = ($p.TotalProcessorTime - $lastCpu).TotalSeconds / ($now - $lastTime).TotalSeconds / $cores * 100
    $lastCpu = $p.TotalProcessorTime; $lastTime = $now
    $sample = [pscustomobject]@{
        Minute = ($now - $start).TotalMinutes
        Private = $p.PrivateMemorySize64 / 1MB
        Working = $p.WorkingSet64 / 1MB
        Cpu = $cpu
        Handles = $p.HandleCount
        Threads = $p.Threads.Count
        Position = Get-Position $p
    }
    $samples.Add($sample)
    ('{0:0.00},{1:0.0},{2:0.0},{3:0.0},{4},{5},{6}' -f $sample.Minute, $sample.Private, $sample.Working, $sample.Cpu, $sample.Handles, $sample.Threads, $sample.Position) | Add-Content -Encoding ascii $csv
}
if (-not $p.HasExited) {
    $null = $p.CloseMainWindow()
    if (-not $p.WaitForExit(20000)) { $failures.Add('did not close within 20 s after the playback run'); $p.Kill() }
}

if ($samples.Count -ge 4) {
    # Settled level: after a warm-up of a sixth of the run (at least 2 minutes); compared with the last sixth.
    $warm = [math]::Max(2, $Minutes / 6)
    $settled = @($samples | Where-Object { $_.Minute -ge $warm -and $_.Minute -lt $warm + [math]::Max(2, $Minutes / 6) })
    $last = @($samples | Where-Object { $_.Minute -ge $Minutes * 5 / 6 })
    if ($settled.Count -and $last.Count) {
        $a = Median ($settled | ForEach-Object { $_.Private }); $b = Median ($last | ForEach-Object { $_.Private })
        Say ("Private memory: {0:0} MB settled, {1:0} MB at the end, {2:0} MB highest." -f $a, $b, ($samples | Measure-Object Private -Maximum).Maximum)
        if ($b - $a -gt 64 -and $b -gt $a * 1.2) { $failures.Add(("private memory grew from {0:0} MB to {1:0} MB" -f $a, $b)) }
        $ha = Median ($settled | ForEach-Object { $_.Handles }); $hb = Median ($last | ForEach-Object { $_.Handles })
        Say ("Handles: {0} settled, {1} at the end. Threads: {2} at the end." -f $ha, $hb, $samples[-1].Threads)
        if ($hb -gt $ha * 1.5 -and $hb - $ha -gt 500) { $failures.Add("handles grew from $ha to $hb") }
    }
    $cpuValues = @($samples | ForEach-Object { $_.Cpu })
    Say ("CPU, share of all {0} cores: {1:0.0} % median, {2:0.0} % highest." -f $cores, (Median $cpuValues), ($cpuValues | Measure-Object -Maximum).Maximum)
    $positions = @($samples | Where-Object { $null -ne $_.Position })
    if ($positions.Count -ge 2) {
        $advanced = $positions[-1].Position - $positions[0].Position
        $wall = ($positions[-1].Minute - $positions[0].Minute) * 60
        Say ("Playback moved {0:0} s in {1:0} s of wall time." -f $advanced, $wall)
        if ($advanced -lt $wall * 0.9) { $failures.Add(("playback moved only {0:0} s in {1:0} s" -f $advanced, $wall)) }
    } else {
        Say 'The timeline position could not be read through UI Automation; playback progress was not checked.'
    }
}

Say ""
if ($failures.Count) { Say '**Failed:**'; $failures | ForEach-Object { Say "- $_" } } else { Say '**Passed.**' }
$summary | Set-Content -Encoding utf8 (Join-Path $OutDir 'soak.md')
if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -Encoding utf8 $env:GITHUB_STEP_SUMMARY }
if ($failures.Count) { exit 1 }
