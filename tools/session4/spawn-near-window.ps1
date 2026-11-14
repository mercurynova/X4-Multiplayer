<#
.SYNOPSIS
  Judges M4 criterion 6 (M4-23) on one captured client log: how many spawn-near-player pops happened inside a time window,
  and what the highway signal said while it ran.

.DESCRIPTION
  The counter is cumulative in the 1 Hz health line the ghost driver writes:

    [sync] ghosts health: rep_bad=.. spawn_failed=.. unmapped=.. unresolved=.. spawn_near=..

  A client join legitimately spawns the whole sector at once, and those spawns can land inside the 5 km bubble (the earlier
  session 7 join made 20-23 pops at 623-4081 m). So the criterion is judged on a window that starts after the join, normally
  the highway transit itself: the value before the window is the baseline, the last value at or before the end of the window
  is the result, and the delta must be 0.

  Every spawn-near-player line inside the window is printed with its timestamp, net id, sector and distance, together with
  the selfship highway lines (`superhighway entry/exit`, `highway context`) so the report says what the ship was doing.

  A window with no new ghosts proves nothing: the criterion is that the spawns which do happen during the transit keep
  outside the bubble. So the tool also counts the ghosts the client created inside the window (`ghost record: net=` lines)
  and, with -MinSpawns, fails when the transit was empty.

  Exit code 0 when the delta is 0 (and enough ghosts spawned), 1 otherwise (so a CI step can gate on it).

.PARAMETER Log
  The client's extension/logs/x4mp.log. `-` reads standard input.

.PARAMETER From
  Window start. `HH:mm:ss` or `yyyy-MM-dd HH:mm:ss[.fff]`. Omitted = the first line of the log (the join then counts, which
  is why the default usually fails on a real log).

.PARAMETER To
  Window end, same formats. Omitted = the last line of the log.

.PARAMETER MaxList
  How many pop lines to print (default 25; the count is always printed in full).

.PARAMETER MinSpawns
  Fail unless at least this many ghosts were created inside the window (default 0). Use it for a real transit probe: it
  turns "no pops" into "no pops while traffic actually appeared".

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\session4\spawn-near-window.ps1 `
      -Log 'C:\...\extension\logs\x4mp.log' -From '19:41:02' -To '19:44:30'

.EXAMPLE
  # the whole log, to see the baseline the join leaves behind
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\session4\spawn-near-window.ps1 -Log .\x4mp.log
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$Log,
  [string]$From,
  [string]$To,
  [int]$MaxList = 25,
  [int]$MinSpawns = 0
)

$ErrorActionPreference = 'Stop'

$lines = if ($Log -eq '-') { @($input) } else {
  if (-not (Test-Path -LiteralPath $Log)) { throw "no such log: $Log" }
  Get-Content -LiteralPath $Log
}

$tsRe = [regex]'^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})Z'
$healthRe = [regex]'\[sync\] ghosts health: rep_bad=(?<bad>\d+) spawn_failed=(?<failed>\d+) unmapped=(?<unmapped>\d+) unresolved=(?<unresolved>\d+) spawn_near=(?<near>\d+)'
$nearRe = [regex]'spawn-near-player: net=(?<net>\d+) sector=(?<sector>\d+) distance=(?<dist>\d+) m macro=(?<macro>\S+)'
$spawnRe = [regex]'ghost record: net=(?<net>\d+) player=(?<player>\S*)'

function Parse-Stamp([string]$text, [datetime]$day) {
  if ([string]::IsNullOrWhiteSpace($text)) { return $null }
  $t = $text.Trim()
  $inv = [Globalization.CultureInfo]::InvariantCulture
  # time only: the log's own date is assumed (a sitting is one day; the user reads the clock off the health line)
  if ($t -match '^\d{1,2}:\d{2}(:\d{2}(\.\d{1,3})?)?$') {
    return $day.Date.Add([timespan]::Parse($t, $inv))
  }
  foreach ($f in 'yyyy-MM-dd HH:mm:ss.fff', 'yyyy-MM-dd HH:mm:ss') {
    $parsed = [datetime]::MinValue
    if ([datetime]::TryParseExact($t, $f, $inv, [Globalization.DateTimeStyles]::None, [ref]$parsed)) { return $parsed }
  }
  $parsed = [datetime]::MinValue
  if ([datetime]::TryParse($t, $inv, [Globalization.DateTimeStyles]::None, [ref]$parsed)) { return $parsed }
  throw "cannot parse '$text' as a timestamp (use 'HH:mm:ss' or 'yyyy-MM-dd HH:mm:ss[.fff]')"
}

$firstStamp = $null
$lastStamp = $null
foreach ($l in $lines) {
  $m = $tsRe.Match($l)
  if ($m.Success) {
    $stamp = [datetime]::ParseExact($m.Groups['ts'].Value, 'yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)
    if ($null -eq $firstStamp) { $firstStamp = $stamp }
    $lastStamp = $stamp
  }
}
if ($null -eq $firstStamp) { throw "no timestamped lines in the log (is this a mod log?)" }

$fromAt = Parse-Stamp $From $firstStamp
$toAt = Parse-Stamp $To $firstStamp
if ($null -eq $fromAt) { $fromAt = $firstStamp }
if ($null -eq $toAt) { $toAt = $lastStamp }

$baseNear = 0; $baseAt = $null
$endNear = 0; $endAt = $null
$popsBefore = 0                        # pop lines up to the window: the counter's own history, used when no health line precedes it
$firstHealthNear = $null; $firstHealthAt = $null
$pops = New-Object System.Collections.Generic.List[object]
$spawns = New-Object System.Collections.Generic.List[object]   # ghosts created inside the window: the transit must not be empty
$highway = New-Object System.Collections.Generic.List[object]
$points = New-Object System.Collections.Generic.List[object]   # every health line, for a readable timeline

foreach ($l in $lines) {
  $m = $tsRe.Match($l)
  if (-not $m.Success) { continue }
  $at = [datetime]::ParseExact($m.Groups['ts'].Value, 'yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)

  $h = $healthRe.Match($l)
  if ($h.Success) {
    $n = [int]$h.Groups['near'].Value
    $points.Add([pscustomobject]@{ At = $at; Near = $n })
    if ($null -eq $firstHealthAt) { $firstHealthAt = $at; $firstHealthNear = $n }
    if ($null -eq $fromAt -or $at -le $fromAt) { $baseNear = $n; $baseAt = $at }
    if ($null -eq $toAt -or $at -le $toAt) { $endNear = $n; $endAt = $at }
    continue
  }

  $near = $nearRe.Match($l)
  if ($near.Success) {
    if ($null -ne $fromAt -and $at -lt $fromAt) { $popsBefore++ }
    $atOk = ($null -eq $fromAt -or $at -ge $fromAt) -and ($null -eq $toAt -or $at -le $toAt)
    if ($atOk) {
      $pops.Add([pscustomobject]@{
        At = $at; Net = [int]$near.Groups['net'].Value; Sector = [int]$near.Groups['sector'].Value
        Distance = [int]$near.Groups['dist'].Value; Macro = $near.Groups['macro'].Value
      })
    }
    continue
  }

  if ($l -match 'selfship: (superhighway (entry|exit)|highway context)') {
    $atOk = ($null -eq $fromAt -or $at -ge $fromAt) -and ($null -eq $toAt -or $at -le $toAt)
    if ($atOk) {
      $idx = $l.IndexOf('[selfship]')
      if ($idx -lt 0) { $idx = 0 }
      $highway.Add([pscustomobject]@{ At = $at; Text = $l.Substring($idx).Trim() })
    }
    continue
  }

  $sp = $spawnRe.Match($l)
  if ($sp.Success) {
    $atOk = ($null -eq $fromAt -or $at -ge $fromAt) -and ($null -eq $toAt -or $at -le $toAt)
    if ($atOk) {
      $spawns.Add([pscustomobject]@{ At = $at; Net = [int]$sp.Groups['net'].Value; Player = $sp.Groups['player'].Value })
    }
  }
}

$baseSource = ''
$endSource = ''
if ($null -eq $baseAt) {
  # The join batch is written before the first 1 Hz health line ever appears, so the counter has no reading yet: the pop
  # lines themselves are the history (the counter is exactly the number of those lines).
  $baseNear = $popsBefore
  $baseAt = $fromAt
  $baseSource = ' - from the pop lines, no health line before the window'
}
if ($null -eq $endAt) {
  if ($null -eq $firstHealthAt) { throw 'no health line in the log: cannot read spawn_near' }
  $endNear = $firstHealthNear
  $endAt = $firstHealthAt
  $endSource = ' - first health line, the window ends before any'
}

$delta = $endNear - $baseNear
if ($delta -lt 0) { throw "the spawn_near counter went backwards ($baseNear -> $endNear); the log was rotated or edited" }

function Fmt($d) { if ($null -eq $d) { return '-' } return $d.ToString('yyyy-MM-dd HH:mm:ss.fff') }

Write-Host "Log:      $Log"
Write-Host "Window:   $(Fmt $fromAt) .. $(Fmt $toAt)"
Write-Host "Baseline: spawn_near=$baseNear at $(Fmt $baseAt)$baseSource"
Write-Host "End:      spawn_near=$endNear at $(Fmt $endAt)$endSource"
Write-Host "Delta:    $delta pop(s) inside the window"
Write-Host ''

if ($pops.Count -gt 0) {
  $distances = ($pops | ForEach-Object { $_.Distance }) | Sort-Object
  Write-Host "Pops inside the window: $($pops.Count) (distances $($distances[0])..$($distances[-1]) m)"
  $pops | Select-Object -First $MaxList | ForEach-Object {
    Write-Host ("  {0}  net={1} sector={2} distance={3} m  {4}" -f $_.At.ToString('HH:mm:ss.fff'), $_.Net, $_.Sector, $_.Distance, $_.Macro)
  }
  if ($pops.Count -gt $MaxList) { Write-Host "  ... $($pops.Count - $MaxList) more" }
} else {
  Write-Host 'Pops inside the window: 0'
}

if ($highway.Count -gt 0) {
  Write-Host ''
  Write-Host "Highway / selfship lines inside the window: $($highway.Count)"
  $highway | Select-Object -First $MaxList | ForEach-Object { Write-Host ("  {0}  {1}" -f $_.At.ToString('HH:mm:ss.fff'), $_.Text) }
  if ($highway.Count -gt $MaxList) { Write-Host "  ... $($highway.Count - $MaxList) more" }
}

Write-Host ''
Write-Host "Ghosts created inside the window: $($spawns.Count)"
if ($spawns.Count -gt 0) {
  $spawns | Select-Object -First $MaxList | ForEach-Object { Write-Host ("  {0}  net={1}  {2}" -f $_.At.ToString('HH:mm:ss.fff'), $_.Net, $_.Player) }
  if ($spawns.Count -gt $MaxList) { Write-Host "  ... $($spawns.Count - $MaxList) more" }
}
Write-Host ''

if ($delta -ne 0) {
  Write-Host "FAIL  spawn-near-player pops inside the window: $delta (criterion 6 wants 0; narrow the window past the join, or report the distances above)"
  exit 1
}
if ($spawns.Count -lt $MinSpawns) {
  Write-Host "FAIL  the window is empty: $($spawns.Count) new ghost(s), expected at least $MinSpawns (a transit with no traffic proves nothing)"
  exit 1
}
Write-Host "PASS  spawn-near-player pops inside the window: 0 (criterion 6, M4-23), $($spawns.Count) new ghost(s) seen"
exit 0
