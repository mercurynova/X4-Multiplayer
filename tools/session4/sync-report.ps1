<#
.SYNOPSIS
  Session 4: summarises the [sync] and [perf] lines of an X4MP mod log against the M3 targets (docs\m3-plan.md Q3, criteria 2 and 11). Reads only.
.DESCRIPTION
  Input: the mod log (%LocalAppData%\X4MP\logs\x4mp.log by default, or the portable extension folder's; the old Documents\Egosoft\X4\x4mp\logs\x4mp.log only when this PC has none), or -Zip <logs zip made by collect-logs.ps1>, or -Log <file>.
  What it reads (formats: mod/native/core/ghost/sync_stats.cpp, features/ghosts/ghost_feature.cpp, features/stats/stats_feature.cpp, host/mod_host.cpp):
    [sync] player=<name> net=<id> frames=.. err_p50/p95/max=a/b/c m steady_p95=.. fast_p95=.. lat_p95=.. ms ...   one per ghost per 5 s window (client)
    [sync] ghosts tracked=.. spawned=.. respawned=.. ... frame_p95_us=..                                          ghost totals + ghost-driver frame cost
    [perf] stats: fps=.. frame_p95=..ms mod_p95=..ms mod_max=..ms connected=.. rtt=..ms rx=..B/s tx=..B/s       every 5 s
    [perf] frames=.. p50=..us p95=..us avg=..us max=..us over_budget=.. ... log_dropped=..                        the mod's own frame budget
  and judges (PASS / FAIL / n/a), after skipping the first -WarmupWindows (default 2) five-second windows of every ghost (the first windows predate the server's
  Near tier for this client):
    path error p95 (all speeds)             < 50 m      (Q3: below 500 m/s)
    path error p95, steady flight < 300 m/s < 10 m      (Q3)
    display latency p95                     <= 200 ms   (Q3, LAN)
    mod main-thread time per frame p95      < 0.2 ms    (criterion 11; the "mod_p95" of the stats line and "p95" of the frames line)
    rx and tx per second (mean of windows)  < 20 kB/s each (criterion 11)
    log lines per second (mean of 10 s windows, worst window) < 10 (criterion 11)
    ghost respawns ("pops")                 0           (criterion 2: no despawn + respawn while both are in one sector)
  Bots (FakeNode) do not send server-clock timestamps, so their [sync] latency can read high or low; the path error is what counts for them.
  Exit code: 0 = no FAIL (also with -Strict off), 1 = a FAIL with -Strict, 2 = nothing to read. Paste the output into your notes.
.PARAMETER Log     The mod log file. Rotated files (x4mp.log.1 ...) are not read: pass the one you want.
.PARAMETER Zip     A logs zip of collect-logs.ps1 (the x4mp\logs\x4mp.log entry is read, nothing is extracted to disk).
.PARAMETER Json    Also write the numbers as JSON to this file.
.PARAMETER Strict  Exit 1 when a check FAILs (CI uses this on the hostsim logs).
.PARAMETER WarmupWindows  [sync] windows skipped per ghost before judging (default 2).
.PARAMETER MinFrames  A [sync] window needs at least this many rendered frames to count (default 60, about 1 s).
.PARAMETER MaxModP95Ms, MaxKBps, MaxLogRate, MaxErrP95, MaxSteadyP95, MaxLatP95  The limits above (CI relaxes the timing ones on slow runners).
.PARAMETER UserId  The numeric folder under Documents\Egosoft\X4 (only used to find the default log; the log is in the X4MP config folder).
#>
[CmdletBinding()]
param(
    [string]$Log,
    [string]$Zip,
    [string]$Json,
    [switch]$Strict,
    [int]$WarmupWindows = 2,
    [int]$MinFrames = 60,
    [double]$MaxModP95Ms = 0.2,
    [double]$MaxKBps = 20,
    [double]$MaxLogRate = 10,
    [double]$MaxErrP95 = 50,
    [double]$MaxSteadyP95 = 10,
    [double]$MaxLatP95 = 200,
    [string]$UserId
)
$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture
function Die([string]$m) { [Console]::Error.WriteLine($m); exit 2 }

# ---- read the lines ---------------------------------------------------------------------------------------------------------------
$lines = New-Object System.Collections.Generic.List[string]
$source = ''
if ($Zip) {
    if (-not (Test-Path -LiteralPath $Zip)) { Die "zip not found: $Zip" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $za = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Zip).Path)
    try {
        $entry = $za.Entries | Where-Object { ($_.FullName -replace '\\', '/') -match '(^|/)x4mp\.log$' } | Select-Object -First 1
        if (-not $entry) { Die "no x4mp.log inside $Zip" }
        $sr = New-Object IO.StreamReader($entry.Open())
        try { while ($null -ne ($l = $sr.ReadLine())) { $lines.Add($l) } } finally { $sr.Dispose() }
        $source = "$Zip ($($entry.FullName))"
    }
    finally { $za.Dispose() }
}
else {
    if (-not $Log) {
        # M3-24: per-machine log first (never another PC's). The old Documents location is a fallback for logs of older mods only.
        . (Join-Path $PSScriptRoot 'common.ps1')
        $Log = Join-Path (Get-X4MPConfigDir) 'logs\x4mp.log'
        if (-not (Test-Path -LiteralPath $Log)) {
            $old = Join-Path (Get-X4MPLegacyConfigDir) 'logs\x4mp.log'
            if (Test-Path -LiteralPath $old) {
                [Console]::Error.WriteLine("No log at $Log; reading the OLD location $old (mod from before M3-24; with a OneDrive-redirected Documents it may be another PC's log).")
                $Log = $old
            }
        }
    }
    if (-not (Test-Path -LiteralPath $Log)) { Die "log not found: $Log (pass -Log or -Zip)" }
    $fs = [IO.File]::Open($Log, 'Open', 'Read', 'ReadWrite')
    $sr = New-Object IO.StreamReader($fs)
    try { while ($null -ne ($l = $sr.ReadLine())) { $lines.Add($l) } } finally { $sr.Dispose() }
    $source = $Log
}

function D([string]$s) { return [double]::Parse($s, $inv) }
function Percentile([double[]]$v, [double]$p) {
    if ($v.Count -eq 0) { return [double]::NaN }
    $s = $v | Sort-Object
    $i = [math]::Min($s.Count - 1, [math]::Max(0, [int][math]::Ceiling($p * $s.Count) - 1))
    return [double]$s[$i]
}
function Fmt([double]$v, [string]$f = 'F2') { if ([double]::IsNaN($v)) { return '-' } return $v.ToString($f, $inv) }
$results = New-Object System.Collections.Generic.List[object]
function Judge([string]$what, [double]$value, [string]$op, [double]$limit, [string]$unit, [string]$note = '') {
    if ([double]::IsNaN($value)) { $results.Add([pscustomobject]@{ Check = $what; Value = '-'; Limit = "$op $limit $unit"; Result = 'n/a'; Note = $note }); return }
    $ok = if ($op -eq '<') { $value -lt $limit } else { $value -le $limit }
    $results.Add([pscustomobject]@{ Check = $what; Value = (Fmt $value); Limit = "$op $limit $unit"; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Note = $note })
}

# ---- parse -------------------------------------------------------------------------------------------------------------------------
$syncRe = [regex]'\[sync\] player=(?<who>.+?) net=(?<net>\d+) frames=(?<frames>\d+) err_p50/p95/max=(?<p50>[\d.]+)/(?<p95>[\d.]+)/(?<max>[\d.]+) m steady_p95=(?<steady>[\d.]+) fast_p95=(?<fast>[\d.]+) lat_p95=(?<lat>[\d.]+) ms speed_max=(?<speed>[\d.]+)'
$ghostRe = [regex]'\[sync\] ghosts tracked=(?<tracked>\d+) visible=(?<visible>\d+) spawned=(?<spawned>\d+) respawned=(?<respawned>\d+).*?frame_p95_us=(?<fp95>[\d.]+) frame_max_us=(?<fmax>[\d.]+)'
$statsRe = [regex]'stats: fps=(?<fps>[\d.]+) frame_p95=(?<fr>[\d.]+)ms mod_p95=(?<mod>[\d.]+)ms mod_max=(?<modmax>[\d.]+)ms connected=(?<conn>\d) rtt=(?<rtt>[-\d.]+)ms rx=(?<rx>\d+)B/s tx=(?<tx>\d+)B/s'
$hostRe = [regex]'frames=(?<frames>\d+) p50=(?<p50>\d+)us p95=(?<p95>\d+)us avg=(?<avg>\d+)us max=(?<max>\d+)us over_budget=(?<over>\d+) .*?log_dropped=(?<dropped>\d+)'
$tsRe = [regex]'^(?<t>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\.\d{3}Z '

$windows = @{}          # player -> list of windows
$ghostTotals = $null
$ghostFrameP95 = New-Object System.Collections.Generic.List[double]
$modP95 = New-Object System.Collections.Generic.List[double]
$modMax = 0.0
$hostP95Us = New-Object System.Collections.Generic.List[double]
$rx = New-Object System.Collections.Generic.List[double]
$tx = New-Object System.Collections.Generic.List[double]
$connectedWindows = 0
$perSecond = @{}        # "yyyy-MM-dd HH:mm:ss" -> line count
$logDropped = 0
foreach ($l in $lines) {
    $m = $tsRe.Match($l)
    if ($m.Success) { $k = $m.Groups['t'].Value; if ($perSecond.ContainsKey($k)) { $perSecond[$k]++ } else { $perSecond[$k] = 1 } }
    if ($l.Contains('[sync]')) {
        $m = $syncRe.Match($l)
        if ($m.Success) {
            $who = $m.Groups['who'].Value
            if (-not $windows.ContainsKey($who)) { $windows[$who] = New-Object System.Collections.Generic.List[object] }
            $windows[$who].Add([pscustomobject]@{ Frames = [int]$m.Groups['frames'].Value; P50 = (D $m.Groups['p50'].Value); P95 = (D $m.Groups['p95'].Value); Max = (D $m.Groups['max'].Value);
                    Steady = (D $m.Groups['steady'].Value); Lat = (D $m.Groups['lat'].Value); Speed = (D $m.Groups['speed'].Value) })
            continue
        }
        $m = $ghostRe.Match($l)
        if ($m.Success) { $ghostTotals = $m; $ghostFrameP95.Add((D $m.Groups['fp95'].Value)) }
        continue
    }
    if ($l.Contains('stats: fps=')) {
        $m = $statsRe.Match($l)
        if ($m.Success -and $m.Groups['conn'].Value -eq '1') {
            $connectedWindows++
            $modP95.Add((D $m.Groups['mod'].Value)); $modMax = [math]::Max($modMax, (D $m.Groups['modmax'].Value))
            $rx.Add((D $m.Groups['rx'].Value)); $tx.Add((D $m.Groups['tx'].Value))
        }
        continue
    }
    if ($l.Contains(' p95=') -and $l.Contains('over_budget=')) {
        $m = $hostRe.Match($l)
        if ($m.Success) { $hostP95Us.Add((D $m.Groups['p95'].Value)); $logDropped = [int]$m.Groups['dropped'].Value }
    }
}

if ($windows.Count -eq 0 -and $modP95.Count -eq 0 -and $perSecond.Count -eq 0) { Die "no [sync] / [perf] lines in $source" }

# ---- per ghost ----------------------------------------------------------------------------------------------------------------------
Write-Host "Source: $source ($($lines.Count) lines)"
Write-Host ''
function Median([double[]]$v) { return (Percentile $v 0.5) }
$ghostRows = New-Object System.Collections.Generic.List[object]
$errMed = New-Object System.Collections.Generic.List[double];    $errWorst = New-Object System.Collections.Generic.List[double]
$steadyMed = New-Object System.Collections.Generic.List[double]; $steadyWorst = New-Object System.Collections.Generic.List[double]
$latMed = New-Object System.Collections.Generic.List[double];    $latWorst = New-Object System.Collections.Generic.List[double]
foreach ($who in ($windows.Keys | Sort-Object)) {
    $all = $windows[$who].ToArray()
    $judged = @($all | Select-Object -Skip $WarmupWindows | Where-Object { $_.Frames -ge $MinFrames })
    $moving = @($judged | Where-Object { $_.Speed -ge 1 })      # a parked ship has no meaningful display latency (its newest sample just gets old)
    $row = [pscustomobject]@{ Player = $who; Windows = $all.Count; Judged = $judged.Count; ErrMed = [double]::NaN; ErrWorst = [double]::NaN; SteadyMed = [double]::NaN; SteadyWorst = [double]::NaN
        ErrMax = [double]::NaN; LatMed = [double]::NaN; LatWorst = [double]::NaN; SpeedMax = [double]::NaN }
    if ($judged.Count -gt 0) {
        $p = [double[]]@($judged | ForEach-Object { $_.P95 }); $s = [double[]]@($judged | ForEach-Object { $_.Steady })
        $row.ErrMed = Median $p; $row.ErrWorst = ($p | Measure-Object -Maximum).Maximum
        $row.SteadyMed = Median $s; $row.SteadyWorst = ($s | Measure-Object -Maximum).Maximum
        $row.ErrMax = ($judged | Measure-Object -Property Max -Maximum).Maximum
        $row.SpeedMax = ($judged | Measure-Object -Property Speed -Maximum).Maximum
        $errMed.Add($row.ErrMed); $errWorst.Add($row.ErrWorst); $steadyMed.Add($row.SteadyMed); $steadyWorst.Add($row.SteadyWorst)
        if ($moving.Count -gt 0) {
            $lt = [double[]]@($moving | ForEach-Object { $_.Lat })
            $row.LatMed = Median $lt; $row.LatWorst = ($lt | Measure-Object -Maximum).Maximum
            $latMed.Add($row.LatMed); $latWorst.Add($row.LatWorst)
        }
    }
    $ghostRows.Add($row)
}
if ($ghostRows.Count -gt 0) {
    Write-Host 'Ghosts ([sync] windows after warm-up, median (worst) per ghost; this log is the viewer; latency of parked ghosts is not judged):'
    $ghostRows | Format-Table @{ n = 'Player'; e = { $_.Player } }, @{ n = 'windows'; e = { $_.Windows } }, @{ n = 'judged'; e = { $_.Judged } },
        @{ n = 'err p95 m'; e = { (Fmt $_.ErrMed) + ' (' + (Fmt $_.ErrWorst) + ')' } }, @{ n = 'steady p95 m'; e = { (Fmt $_.SteadyMed) + ' (' + (Fmt $_.SteadyWorst) + ')' } },
        @{ n = 'err max m'; e = { Fmt $_.ErrMax } }, @{ n = 'lat p95 ms'; e = { (Fmt $_.LatMed '0') + ' (' + (Fmt $_.LatWorst '0') + ')' } }, @{ n = 'speed max'; e = { Fmt $_.SpeedMax '0' } } -AutoSize | Out-String | Write-Host
}
else { Write-Host 'No ghost [sync] windows (this log is from a node without ghosts: the authority, or a client before any ghost existed).' }
function MaxOf($list) { if ($list.Count) { return ($list | Measure-Object -Maximum).Maximum } return [double]::NaN }

# PASS = the median window of the worst ghost is inside the limit and so is every window; WARN = typical windows are fine but some window is not
# (reloads, gate jumps and loss bursts cause those); FAIL = the typical window is outside the limit.
function Judge2([string]$what, [double]$typical, [double]$worst, [string]$op, [double]$limit, [string]$unit, [string]$note = '') {
    if ([double]::IsNaN($typical)) { $results.Add([pscustomobject]@{ Check = $what; Value = '-'; Limit = "$op $limit $unit"; Result = 'n/a'; Note = $note }); return }
    $inside = { param($v) if ($op -eq '<') { $v -lt $limit } else { $v -le $limit } }
    $res = if (-not (& $inside $typical)) { 'FAIL' } elseif (-not (& $inside $worst)) { 'WARN' } else { 'PASS' }
    $results.Add([pscustomobject]@{ Check = $what; Value = ((Fmt $typical) + ' (worst ' + (Fmt $worst) + ')'); Limit = "$op $limit $unit"; Result = $res; Note = $note })
}
Judge2 'path error p95, all speeds (worst ghost, median window)' (MaxOf $errMed) (MaxOf $errWorst) '<' $MaxErrP95 'm' 'Q3: below 500 m/s'
Judge2 'path error p95, steady flight < 300 m/s (worst ghost, median window)' (MaxOf $steadyMed) (MaxOf $steadyWorst) '<' $MaxSteadyP95 'm' 'Q3'
Judge2 'display latency p95 (worst moving ghost, median window)' (MaxOf $latMed) (MaxOf $latWorst) '<=' $MaxLatP95 'ms' 'Q3: LAN'
if ($ghostTotals) {
    $respawned = [int]$ghostTotals.Groups['respawned'].Value
    $results.Add([pscustomobject]@{ Check = 'ghost respawns ("pops"), mod lifetime total'; Value = "$respawned"; Limit = '= 0'; Result = $(if ($respawned -eq 0) { 'PASS' } else { 'FAIL' }); Note = "of $($ghostTotals.Groups['spawned'].Value) spawns; criterion 2" })
}

# ---- performance ---------------------------------------------------------------------------------------------------------------------
$modP95Ms = [double]::NaN
if ($modP95.Count -gt 0) { $modP95Ms = Median ([double[]]$modP95.ToArray()) }
$modP95Worst = if ($modP95.Count -gt 0) { ($modP95 | Measure-Object -Maximum).Maximum } else { [double]::NaN }
Judge2 'mod main-thread time per frame, p95 (median 5 s window, stats line)' $modP95Ms $modP95Worst '<' $MaxModP95Ms 'ms' "$connectedWindows connected windows; criterion 11"
$hostP95Ms = [double]::NaN; $hostP95Worst = [double]::NaN
if ($hostP95Us.Count -gt 0) { $hostP95Ms = (Median ([double[]]$hostP95Us.ToArray())) / 1000.0; $hostP95Worst = (($hostP95Us | Measure-Object -Maximum).Maximum) / 1000.0 }
Judge2 'mod frame budget p95 (median window, [perf] frames line)' $hostP95Ms $hostP95Worst '<' $MaxModP95Ms 'ms' 'same measurement by the host'
$rxMean = if ($rx.Count) { (Median ([double[]]$rx.ToArray())) / 1000.0 } else { [double]::NaN }
$txMean = if ($tx.Count) { (Median ([double[]]$tx.ToArray())) / 1000.0 } else { [double]::NaN }
$rxMax = if ($rx.Count) { ($rx | Measure-Object -Maximum).Maximum / 1000.0 } else { [double]::NaN }
$txMax = if ($tx.Count) { ($tx | Measure-Object -Maximum).Maximum / 1000.0 } else { [double]::NaN }
Judge2 'download rate rx, TCP + UDP (median 5 s window)' $rxMean $rxMax '<' $MaxKBps 'kB/s' 'per client, criterion 11; the worst window is a join, a checkpoint or a reload'
Judge2 'upload rate tx, TCP + UDP (median 5 s window)' $txMean $txMax '<' $MaxKBps 'kB/s' 'per client, criterion 11; the authority uploads its checkpoints in bursts'
# log lines per second: sustained = the worst 60 s window; the worst 10 s window is shown too (a load, a reload or a join is a burst)
$worstRate60 = [double]::NaN; $worstRate10 = [double]::NaN; $meanRate = [double]::NaN
if ($perSecond.Count -gt 0) {
    $t0 = ($perSecond.Keys | ForEach-Object { [datetime]::ParseExact($_, 'yyyy-MM-dd HH:mm:ss', $inv) } | Sort-Object | Select-Object -First 1)
    $b10 = @{}; $b60 = @{}
    foreach ($k in $perSecond.Keys) {
        $sec = ([datetime]::ParseExact($k, 'yyyy-MM-dd HH:mm:ss', $inv) - $t0).TotalSeconds
        $i10 = [int][math]::Floor($sec / 10); $i60 = [int][math]::Floor($sec / 60)
        if ($b10.ContainsKey($i10)) { $b10[$i10] += $perSecond[$k] } else { $b10[$i10] = $perSecond[$k] }
        if ($b60.ContainsKey($i60)) { $b60[$i60] += $perSecond[$k] } else { $b60[$i60] = $perSecond[$k] }
    }
    $worstRate10 = (($b10.Values | Measure-Object -Maximum).Maximum) / 10.0
    $worstRate60 = (($b60.Values | Measure-Object -Maximum).Maximum) / 60.0
    $meanRate = (($b10.Values | Measure-Object -Average).Average) / 10.0
}
Judge2 'log lines per second (mean of the worst 60 s window)' $worstRate60 $worstRate10 '<' $MaxLogRate 'lines/s' $(if ([double]::IsNaN($meanRate)) { 'criterion 11' } else { "worst = busiest 10 s; mean $(Fmt $meanRate) lines/s over the whole log; $logDropped lines dropped by the rate limiter" })

Write-Host 'Checks:'
foreach ($r in $results) {
    $color = switch ($r.Result) { 'PASS' { 'Green' } 'WARN' { 'Yellow' } 'FAIL' { 'Red' } default { 'Gray' } }
    Write-Host ('  {0,-5} {1}: {2} (limit {3})' -f $r.Result, $r.Check, $r.Value, $r.Limit) -ForegroundColor $color
    if ($r.Note) { Write-Host ('        {0}' -f $r.Note) -ForegroundColor DarkGray }
}
$fail = @($results | Where-Object { $_.Result -eq 'FAIL' }).Count
$pass = @($results | Where-Object { $_.Result -eq 'PASS' }).Count
$warn = @($results | Where-Object { $_.Result -eq 'WARN' }).Count
Write-Host ("Summary: {0} PASS, {1} WARN, {2} FAIL, {3} n/a" -f $pass, $warn, $fail, @($results | Where-Object { $_.Result -eq 'n/a' }).Count)
if ($ghostTotals) {
    Write-Host ("Ghost driver: tracked {0}, visible {1}, frame p95 {2} us (worst window)" -f $ghostTotals.Groups['tracked'].Value, $ghostTotals.Groups['visible'].Value, (Fmt (($ghostFrameP95 | Measure-Object -Maximum).Maximum) '0'))
}
if ($Json) {
    [pscustomobject]@{ source = $source; ghosts = $ghostRows; checks = $results; modP95Ms = $modP95Ms; modMaxMs = $modMax; rxKBps = $rxMean; txKBps = $txMean; logLinesPerSecWorst60 = $worstRate60 } |
        ConvertTo-Json -Depth 5 | Set-Content -Path $Json -Encoding UTF8
}
if ($Strict -and $fail -gt 0) { exit 1 }
exit 0
