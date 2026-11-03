<#
.SYNOPSIS
  Soak run for the M6 exit criterion: a server under FakeNode swarm traffic with reconnect churn, sampled for leaks.

.DESCRIPTION
  Starts the published single-file server on fresh ports and a fresh data dir, waits for /healthz, bootstraps the admin
  password, then runs `X4MP.FakeNode swarm` with 3 clients, the authority, `--verify`, `--disconnect-every` (abrupt
  socket drops that resume with the resume token) and `--reload-every` (Disconnect(ClientReload) + NodeReady). While the
  swarm runs, the server process is sampled every -SampleSeconds for working set, handle count and thread count, and
  /healthz is checked each time.

  Verdict: the swarm must exit 0 (with --verify any replication mismatch makes it exit 1), the server must never leave
  /healthz at anything but 200, and after a warm-up window memory must not grow more than -MemoryGrowthPct percent and
  the handle count must not grow more than -HandleGrowth. The report is written to <out>/report.md.

  The defaults (240 minutes, 3 clients) are the M6 criterion itself. A short run (-Minutes 6 -SampleSeconds 10) is a
  rehearsal of the script; it is not evidence for the criterion.

.PARAMETER Build
  Publish the server (win-x64 profile) and build the FakeNode instead of using the binaries already in out/.
.PARAMETER KeepData
  Keep the fresh server data dir (saves, logs, database) after the run for inspection.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/soak.ps1 -Minutes 6 -SampleSeconds 10
#>
[CmdletBinding()]
param(
  [int]$Minutes = 240,
  [int]$Clients = 3,
  [int]$PortBase = 47920,
  [int]$DisconnectEvery = 120,
  [int]$ReloadEvery = 300,
  [string]$SlowReader = '',
  [int]$SampleSeconds = 30,
  [double]$MemoryGrowthPct = 5,
  [int]$HandleGrowth = 64,
  [int]$WarmupSeconds = 60,
  [string]$ServerExe = '',
  [string]$FakeNodeExe = '',
  [string]$Out = '',
  [switch]$Build,
  [switch]$KeepData
)

$ErrorActionPreference = 'Stop'
$isWin = $true
$Repo = Split-Path -Parent $PSScriptRoot
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
if (-not $Out) { $Out = Join-Path $Repo "out\soak-$stamp" }
$logDir = Join-Path $Out 'logs'
$work = Join-Path $Out 'work'
if (-not $ServerExe) { $ServerExe = Join-Path $Repo 'out\win-x64\x4mp-server.exe' }
if (-not $FakeNodeExe) { $FakeNodeExe = Join-Path $Repo 'out\fakenode\X4MP.FakeNode.exe' }
$adminPassword = 'Soak-ci-only-password-12345'
$started = New-Object System.Collections.Generic.List[object]
$samples = New-Object System.Collections.Generic.List[object]

function Say([string]$text) { Write-Host $text }
function Fail([string]$text) { throw "soak: $text" }

function Stop-Tree($proc) {
  if ($null -eq $proc) { return }
  try { if ($proc.HasExited) { return } } catch { return }
  try { & taskkill /PID $proc.Id /T /F *> $null } catch { }
}

# Starts a child with its stdout/stderr in the run's log folder. Environment variables are only visible to that child.
function Start-Proc([string]$name, [string]$exe, [string[]]$argList, [hashtable]$envVars = @{}) {
  $old = @{}
  foreach ($k in $envVars.Keys) {
    $old[$k] = [Environment]::GetEnvironmentVariable($k)
    [Environment]::SetEnvironmentVariable($k, [string]$envVars[$k])
  }
  try {
    $outLog = Join-Path $logDir "$name.log"
    $errLog = Join-Path $logDir "$name.err.log"
    $quoted = @($argList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
    if ($quoted.Count -eq 0) {
      $p = Start-Process -FilePath $exe -PassThru -NoNewWindow -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    } else {
      $p = Start-Process -FilePath $exe -ArgumentList $quoted -PassThru -NoNewWindow -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    }
  } finally {
    # Remove, do not set to $null: an empty variable is inherited and the server dies on X4MP:Net:UdpPort.
    foreach ($k in $old.Keys) {
      if ($null -eq $old[$k]) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue }
      else { [Environment]::SetEnvironmentVariable($k, $old[$k]) }
    }
  }
  $null = $p.Handle
  $started.Add($p)
  return $p
}

function Wait-Until([string]$what, [scriptblock]$probe, [int]$timeoutSec = 30) {
  $end = (Get-Date).AddSeconds($timeoutSec)
  while ($true) {
    try { if (& $probe) { return } } catch { }
    if ((Get-Date) -gt $end) { Fail "timed out after ${timeoutSec}s waiting for $what" }
    Start-Sleep -Milliseconds 250
  }
}

function Get-Health([string]$url) {
  try { return [int](Invoke-WebRequest -UseBasicParsing "$url/healthz" -TimeoutSec 5).StatusCode } catch { return 0 }
}

if ($Minutes -lt 1) { Fail '-Minutes must be at least 1' }
$seconds = $Minutes * 60

# ------------------------------------------------------------------------------------------------ prerequisites

New-Item -ItemType Directory -Force $Out, $logDir, $work | Out-Null
Say "soak: $(Get-Date -Format s)  ${Minutes} min, $Clients clients, ports $PortBase+"
Say "soak: output $Out"
Say ('soak: git ' + (& git -C $Repo log -1 --format='%h %s' 2>$null))

if ($Build) {
  Say 'soak: publishing the server (win-x64 profile)'
  & dotnet publish (Join-Path $Repo 'server\src\X4MP.Server') -c Release -p:PublishProfile=win-x64 --nologo -v minimal
  if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish of the server failed' }
  Say 'soak: building the FakeNode'
  & dotnet build (Join-Path $Repo 'tools\X4MP.FakeNode') -c Release -o (Join-Path $Repo 'out\fakenode') --nologo -v minimal
  if ($LASTEXITCODE -ne 0) { Fail 'dotnet build of the FakeNode failed' }
}
foreach ($file in @($ServerExe, $FakeNodeExe)) {
  if (-not (Test-Path $file)) { Fail "missing $file (run with -Build, or pass -ServerExe/-FakeNodeExe)" }
}

# ------------------------------------------------------------------------------------------------ server

$tcp = $PortBase
$udp = $PortBase + 1
$http = $PortBase + 10
$data = Join-Path $work 'server-data'
New-Item -ItemType Directory -Force $data | Out-Null
$envVars = @{
  X4MP__Net__MaxConnectionsPerIp = '64'
  X4MP__Net__MaxPlayers          = '16'
  X4MP__Net__NodeTcpEndpoint     = "0.0.0.0:$tcp"
  X4MP__Net__UdpPort             = "$udp"
}
$serverArgs = @('--data-dir', $data, '--port', "$http")
Say ('soak: server ' + $ServerExe + ' ' + ($serverArgs -join ' '))
$server = Start-Proc 'server' $ServerExe $serverArgs $envVars
$url = "http://127.0.0.1:$http"
Wait-Until 'the server to answer /healthz' {
  if ($server.HasExited) { Fail "the server exited with $($server.ExitCode); see $logDir\server.err.log" }
  (Get-Health $url) -eq 200
} 60
Say "soak: server up on tcp $tcp, http $http, data $data"

# Bootstrap the admin password so the swarm can audit the session at the end of the run.
$pwFile = Join-Path $data 'initial-admin-password.txt'
Wait-Until 'initial-admin-password.txt' { (Test-Path $pwFile) -and (Get-Content $pwFile -Raw).Trim() } 30
$initial = (Get-Content $pwFile -Raw).Trim()
$headers = @{ 'X-X4MP' = '1' }
$session1 = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/auth/login" -Headers $headers -WebSession $session1 `
  -ContentType 'application/json' -Body (@{ username = 'admin'; password = $initial } | ConvertTo-Json)
$null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/auth/change-password" -Headers $headers -WebSession $session1 `
  -ContentType 'application/json' -Body (@{ current = $initial; new = $adminPassword } | ConvertTo-Json)
Say 'soak: admin password set'

# ------------------------------------------------------------------------------------------------ swarm

$swarmArgs = @(
  'swarm',
  '--server', "127.0.0.1:$tcp",
  '--with-authority',
  '--clients', "$Clients",
  '--teams', "$Clients",
  '--relations', 'ffa',
  '--economy', 'casual',
  '--verify',
  '--disconnect-every', "$DisconnectEvery",
  '--reload-every', "$ReloadEvery",
  '--duration', "$seconds",
  '--admin-url', $url,
  '--admin-password', $adminPassword)
if ($SlowReader) { $swarmArgs += @('--slow-reader', $SlowReader) }
Say ('soak: ' + $FakeNodeExe + ' ' + ($swarmArgs -join ' '))
$swarm = Start-Proc 'swarm' $FakeNodeExe $swarmArgs
$t0 = Get-Date
$deadline = $t0.AddSeconds($seconds + 120)

# ------------------------------------------------------------------------------------------------ sampling

$next = $t0
while ((Get-Date) -lt $deadline) {
  $now = Get-Date
  $live = $false
  try { $live = -not $swarm.HasExited } catch { $live = $false }
  if (-not $live) { break }

  $proc = Get-Process -Id $server.Id -ErrorAction SilentlyContinue
  if ($null -eq $proc) { break }
  $health = Get-Health $url
  $sample = [pscustomobject]@{
    Seconds  = [int]($now - $t0).TotalSeconds
    WorkingSetMB = [math]::Round($proc.WorkingSet64 / 1MB, 1)
    PrivateMB    = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
    Handles      = $proc.HandleCount
    Threads      = $proc.Threads.Count
    Health       = $health
  }
  $samples.Add($sample)
  Say ("soak: t={0,5}s  ws={1,8} MB  private={2,8} MB  handles={3,5}  threads={4,4}  /healthz={5}" -f `
      $sample.Seconds, $sample.WorkingSetMB, $sample.PrivateMB, $sample.Handles, $sample.Threads, $sample.Health)
  $next = $next.AddSeconds($SampleSeconds)
  $sleep = [int][math]::Max(1, [math]::Ceiling(($next - (Get-Date)).TotalSeconds))
  if ($sleep -gt 0) { Start-Sleep -Seconds $sleep }
}

# The swarm is the thing that ends a full run; give it a moment to print its summary and the audit.
if (-not $swarm.HasExited) {
  Say 'soak: waiting up to 120 s for the swarm to finish'
  $null = $swarm.WaitForExit(120000)
}
$swarmExit = $null
try { $swarmExit = $swarm.ExitCode } catch { }
$serverAlive = -not $server.HasExited
$finalHealth = Get-Health $url

# ------------------------------------------------------------------------------------------------ verdict

function Median([double[]]$values) {
  if ($values.Count -eq 0) { return 0 }
  $sorted = @($values | Sort-Object)
  $mid = [int][math]::Floor($sorted.Count / 2)
  if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
  return ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

$warm = @($samples | Where-Object { $_.Seconds -ge $WarmupSeconds })
if ($warm.Count -lt 3) { $warm = @($samples) }
$base = @($warm | Select-Object -First 3)
$tail = @($warm | Select-Object -Last 3)
$baseWs = Median ([double[]]@($base | ForEach-Object { $_.WorkingSetMB }))
$tailWs = Median ([double[]]@($tail | ForEach-Object { $_.WorkingSetMB }))
$baseHandles = Median ([double[]]@($base | ForEach-Object { $_.Handles }))
$tailHandles = Median ([double[]]@($tail | ForEach-Object { $_.Handles }))
$growthPct = if ($baseWs -gt 0) { [math]::Round(($tailWs - $baseWs) / $baseWs * 100, 1) } else { 0 }
$handleDelta = [int]($tailHandles - $baseHandles)

$checks = New-Object System.Collections.Generic.List[object]
function Check([bool]$Ok, [string]$What, [string]$Detail) {
  $checks.Add([pscustomobject]@{ Ok = $Ok; What = $What; Detail = $Detail })
  $mark = if ($Ok) { 'ok  ' } else { 'FAIL' }
  Say ("soak: {0} {1} - {2}" -f $mark, $What, $Detail)
}

Check ($samples.Count -ge 3) 'samples taken' ("{0} samples, {1} s" -f $samples.Count, $(if ($samples.Count -gt 0) { $samples[$samples.Count - 1].Seconds } else { 0 }))
Check ($swarmExit -eq 0) 'swarm exited 0' ("exit={0} ({1} clients, --verify)" -f $swarmExit, $Clients)
Check $serverAlive 'server still running at the end' "pid $($server.Id)"
Check ($finalHealth -eq 200) 'server answers /healthz at the end' "HTTP $finalHealth"
Check (($samples | Where-Object { $_.Health -ne 200 }).Count -eq 0) 'every sample saw /healthz 200' ("{0} non-200" -f ($samples | Where-Object { $_.Health -ne 200 }).Count)
Check ($growthPct -lt $MemoryGrowthPct) 'working set growth' ("{0}% (baseline {1} MB -> {2} MB, budget {3}%)" -f $growthPct, $baseWs, $tailWs, $MemoryGrowthPct)
Check ($handleDelta -le $HandleGrowth) 'handle growth' ("{0} (baseline {1} -> {2}, budget +{3})" -f $handleDelta, $baseHandles, $tailHandles, $HandleGrowth)

$failed = @($checks | Where-Object { -not $_.Ok })
$verdict = if ($failed.Count -eq 0) { 'PASS' } else { 'FAIL' }

$report = Join-Path $Out 'report.md'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Soak report $stamp")
$lines.Add('')
$lines.Add("- Verdict: **$verdict** (" + ($checks.Count - $failed.Count) + ' of ' + $checks.Count + ' checks)')
$lines.Add("- Run: $Minutes min, $Clients clients, authority, team relations ffa, economy casual, --verify")
$lines.Add("- Churn: --disconnect-every $DisconnectEvery, --reload-every $ReloadEvery" + $(if ($SlowReader) { ", --slow-reader $SlowReader" } else { '' }))
$lines.Add("- Ports: tcp $tcp, udp $udp, http $http")
$lines.Add('- Server: ' + $ServerExe)
$lines.Add('- FakeNode: ' + $FakeNodeExe)
$lines.Add('- Git: ' + (& git -C $Repo log -1 --format='%h %s' 2>$null))
$lines.Add('')
$lines.Add('| t (s) | working set MB | private MB | handles | threads | /healthz |')
$lines.Add('|---:|---:|---:|---:|---:|---:|')
foreach ($s in $samples) {
  $lines.Add("| $($s.Seconds) | $($s.WorkingSetMB) | $($s.PrivateMB) | $($s.Handles) | $($s.Threads) | $($s.Health) |")
}
$lines.Add('')
$lines.Add('## Checks')
$lines.Add('')
foreach ($c in $checks) {
  $mark = if ($c.Ok) { 'x' } else { ' ' }
  $lines.Add("- [$mark] $($c.What): $($c.Detail)")
}
$lines.Add('')
$lines.Add('## Verdict')
$lines.Add('')
if ($verdict -eq 'PASS') {
  $lines.Add('Memory growth and handle count stayed inside the budget, the swarm verified clean, and the server answered')
  $lines.Add('/healthz throughout.')
} else {
  $lines.Add('Failed checks: ' + (($failed | ForEach-Object { $_.What }) -join ', ') + '. Logs are in `logs/`.')
}
[IO.File]::WriteAllText($report, ($lines -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))

Say ''
Say "soak: verdict $verdict - $report"
if ($failed.Count -gt 0) {
  foreach ($c in $failed) { Say ("soak:   failed: {0} - {1}" -f $c.What, $c.Detail) }
}

# ------------------------------------------------------------------------------------------------ teardown

Stop-Tree $swarm
Stop-Tree $server
foreach ($p in @($started.ToArray())) { Stop-Tree $p }
if (-not $KeepData) { Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue }

if ($failed.Count -gt 0) { exit 1 }
exit 0
