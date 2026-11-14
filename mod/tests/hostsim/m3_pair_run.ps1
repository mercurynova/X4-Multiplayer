<#
.SYNOPSIS
  M3-14 end-to-end (CI step HostSimM3): TWO real x4mp.dll instances in two hostsim processes - the AUTHORITY (HostAlice) and a CLIENT (Pia) - against
  the published server, plus FakeNode bots as the other players. The whole M3 loop: join, takeover, avatars, ghosts, chat, a gate jump, a checkpoint
  with avatars, a "save load" with renumbered ids, reloads, leave; then the perf / bandwidth / log-rate numbers from the mod logs.
.DESCRIPTION
  1. server on its own ports (47930 TCP, 47931 UDP, 47932 HTTP), admin bootstrap, a dummy start save uploaded and a session created from it;
  2. hostsim A = the authority (m3_authority.hostsim): joins as the authority, first checkpoint, session Running, the host sits and flies a circle;
  3. when A wrote auth.running (the runner waits for the file with a bounded wait on A's process): the FakeNode bots (-Bots, default 6, --avatars
     --chat-echo) and hostsim B = the client (m3_client.hostsim) start. B sees -Bots + 1 remote players, A has -Bots + 1 avatars;
  4. both scenarios synchronise through marker files in a shared sync folder; the runner waits for both processes;
  5. tools\session4\sync-report.ps1 -Strict on B's mod log (ghost path error, latency, mod frame p95, rx/tx, log rate: the Q3 and criterion-11 numbers with
     CI limits) and on A's log (frame p95, rates).
  Needs: mod\build.ps1 (x4mp.dll, x4mp-hostsim.exe) and tools\e2e.ps1 -Steps Publish (server + FakeNode). Nothing sleeps: waits are process waits with timeouts.
  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\m3_pair_run.ps1 [-Bots 6] [-Reloads 3]
#>
[CmdletBinding()]
param(
    [string]$Config = 'relwithdebinfo',
    [int]$TcpPort = 47930, [int]$UdpPort = 47931, [int]$HttpPort = 47932,
    [int]$Bots = 6,
    [int]$Reloads = 3,
    [switch]$UseRunningServer,      # the server and the session already exist (the session-4 kit started them): do not start one
    [string]$AdminPasswordFile,     # with -UseRunningServer: the file holding the GUI admin password (out\session4\admin-password.txt)
    [string]$NodeAdminPasswordValue = 'x4mp-host-test',   # with -UseRunningServer: the in-game admin password the server was started with
    [double]$MaxModP95Ms = 0.5,     # CI runners are noisy: the product target is 0.2 ms (docs/m3-plan.md criterion 11), the CI guard catches regressions
    [double]$MaxLogRate = 12,
    [int]$MaxSeconds = 420
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$build = Join-Path $repo "mod\build\msvc-x64-$Config"
$hostSim = Join-Path $build 'x4mp-hostsim.exe'
$dll = Join-Path $build 'x4mp.dll'
$serverExe = Join-Path $repo 'out\win-x64\x4mp-server.exe'
$fakeExe = Join-Path $repo 'out\fakenode\X4MP.FakeNode.exe'
$syncReport = Join-Path $repo 'tools\session4\sync-report.ps1'
$nearReport = Join-Path $repo 'tools\session4\spawn-near-window.ps1'
foreach ($f in $hostSim, $dll, $serverExe, $fakeExe, $syncReport, $nearReport) { if (-not (Test-Path $f)) { throw "Missing $f (build the mod, then tools\e2e.ps1 -Steps Publish)" } }
$authorityScript = Join-Path $PSScriptRoot 'm3_authority.hostsim'
$clientScript = Join-Path $PSScriptRoot 'm3_client.hostsim'
$sim = Join-Path $PSScriptRoot 'authority_sim.ps1'
$avatars = $Bots + 1          # what the authority holds: the client's avatar + one per bot
$ghosts = $Bots + 1           # what the client shows: the host's ship + one per bot
$spawns = $ghosts + 1         # the client's spawn count: the ghosts + its own avatar copy
$lateBots = 2                 # FakeNode bots that join while the client is in the highway: the real mid-transit creations (M4 criterion 6)
$ghostsAfterJoin = $ghosts + $lateBots
$avatarsAfterJoin = $avatars + $lateBots
$clientSpawns = $spawns + $lateBots   # after the transit the client has created those ghosts as well

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-m3pair-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
$sync = Join-Path $tmp 'sync'; New-Item -ItemType Directory -Force $sync | Out-Null
$adminPassword = 'M3pair-e2e-only-password-12345'      # the GUI admin account (REST)
$nodeAdminPassword = 'M3pair-e2e-host-key-67890'        # the in-game admin proof of the authority
$procs = New-Object System.Collections.Generic.List[object]
$sw = [Diagnostics.Stopwatch]::StartNew()
$exit = 1
$url = "http://127.0.0.1:$HttpPort"
$h = @{ 'X-X4MP' = '1' }
function Stop-All { foreach ($p in $procs) { if (-not $p.HasExited) { try { & taskkill /PID $p.Id /T /F 2>$null | Out-Null } catch { } } }; $procs.Clear() }
function Start-P($exe, $argList, $out, $envVars = @{}) {
    $old = @{}
    foreach ($k in $envVars.Keys) { $old[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, [string]$envVars[$k]) }
    try {
        $q = @($argList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
        $p = Start-Process -FilePath $exe -ArgumentList $q -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError ($out + '.err')
    } finally { foreach ($k in $old.Keys) { if ($null -eq $old[$k]) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue } else { [Environment]::SetEnvironmentVariable($k, $old[$k]) } } }
    $null = $p.Handle; $procs.Add($p); return $p
}
# Condition wait with a deadline; between probes it blocks on the process the condition depends on (no sleeping).
function Wait-Until($what, [scriptblock]$probe, $sec, $watch) {
    $end = (Get-Date).AddSeconds($sec)
    while ($true) {
        try { if (& $probe) { return } } catch { }
        if ($watch.HasExited) { throw "${what}: a process we depend on exited ($($watch.ExitCode))" }
        if ((Get-Date) -gt $end) { throw "Timed out waiting for $what" }
        Wait-Process -Id $watch.Id -Timeout 1 -ErrorAction SilentlyContinue
    }
}
function Get-Sha256Hex([string]$Path) { $s = [IO.File]::OpenRead($Path); try { $h2 = [Security.Cryptography.SHA256]::Create(); try { return ([BitConverter]::ToString($h2.ComputeHash($s)) -replace '-', '').ToLowerInvariant() } finally { $h2.Dispose() } } finally { $s.Dispose() } }
function New-GzSave($path, $seed) {
    $rnd = New-Object byte[] (3MB); (New-Object Random $seed).NextBytes($rnd)
    $fs = [IO.File]::Create($path); $gz = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Compress)
    $head = [Text.Encoding]::UTF8.GetBytes("<?xml version=`"1.0`" encoding=`"utf-8`"?><savegame><info><game id=`"x4mp-m3pair-e2e-$seed`"/></info></savegame>")
    $gz.Write($head, 0, $head.Length); $gz.Write($rnd, 0, $rnd.Length); $gz.Dispose(); $fs.Dispose()
    return Get-Sha256Hex $path
}

try {
    $startSave = Join-Path $tmp 'start_save.xml.gz'; $sha = New-GzSave $startSave 7
    $ckpt1 = Join-Path $tmp 'ckpt1.xml.gz'; $null = New-GzSave $ckpt1 11
    $ckpt2 = Join-Path $tmp 'ckpt2.xml.gz'; $null = New-GzSave $ckpt2 12
    # the galaxy of the bots: the three sectors the scripts' MD report names (gates 1-2 and 2-3), the same macros as the authority's checkpoint
    $galaxy = Join-Path $tmp 'galaxy3.json'
    Set-Content -Path $galaxy -Encoding ASCII -Value '{"format":1,"sectors":[{"macro":"cluster_01_sector001_macro","cluster":"cluster_01_macro","gates":["cluster_02_sector001_macro"]},{"macro":"cluster_02_sector001_macro","cluster":"cluster_02_macro","gates":["cluster_01_sector001_macro","cluster_03_sector001_macro"]},{"macro":"cluster_03_sector001_macro","cluster":"cluster_03_macro","gates":["cluster_02_sector001_macro"]}]}'

    # ---- server + session from an uploaded save ----
    if ($UseRunningServer) {
        # session-4 kit dry run (sitting 3): tools\session4\start-server-lan.ps1 started the server and created the session from a save; only the nodes start here
        if (-not $AdminPasswordFile -or -not (Test-Path $AdminPasswordFile)) { throw '-UseRunningServer needs -AdminPasswordFile' }
        $adminPassword = (Get-Content $AdminPasswordFile -Raw).Trim()
        $nodeAdminPassword = $NodeAdminPasswordValue
        $server = $null
    }
    else {
    $data = Join-Path $tmp 'data'; New-Item -ItemType Directory -Force $data | Out-Null
    $envVars = @{ X4MP__Net__NodeTcpEndpoint = "127.0.0.1:$TcpPort"; X4MP__Net__UdpPort = "$UdpPort"; X4MP__Net__ModBuildStrict = 'false'
        X4MP__Net__MaxPlayers = '12'; X4MP__Net__MaxConnectionsPerIp = '64'; X4MP__Net__AdminPassword = $nodeAdminPassword
        X4MP__Interest__NearRadiusM = '60000'   # the bots wander +-15 km: all of them stay in the 20 Hz Near tier
        # the save/manifest HTTP fallback must point at this run's Kestrel port, not the default 47790
        X4MP__Saves__PublicHttpBaseUrl = "http://127.0.0.1:$HttpPort" }
    $server = Start-P $serverExe @('--data-dir', $data, '--port', "$HttpPort") (Join-Path $tmp 'server.out.txt') $envVars
    Wait-Until 'the server' { if ($server.HasExited) { throw 'server exited' }; (Invoke-WebRequest -UseBasicParsing "$url/healthz" -TimeoutSec 2).StatusCode -eq 200 } 40 $server
    $pwFile = Join-Path $data 'initial-admin-password.txt'
    Wait-Until 'initial-admin-password.txt' { (Test-Path $pwFile) -and (Get-Content $pwFile -Raw).Trim() } 20 $server
    $initial = (Get-Content $pwFile -Raw).Trim()
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/auth/login" -Headers $h -WebSession $s -ContentType 'application/json' -Body (@{ username = 'admin'; password = $initial } | ConvertTo-Json)
    $null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/auth/change-password" -Headers $h -WebSession $s -ContentType 'application/json' -Body (@{ current = $initial; new = $adminPassword } | ConvertTo-Json)
    $web = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/auth/login" -Headers $h -WebSession $web -ContentType 'application/json' -Body (@{ username = 'admin'; password = $adminPassword } | ConvertTo-Json)
    $bytes = [IO.File]::ReadAllBytes($startSave)
    $b = Invoke-RestMethod -Method Post -Uri "$url/api/v1/saves/uploads" -Headers $h -WebSession $web -ContentType 'application/json' -Body (@{ fileName = 'authority_save.xml.gz'; size = $bytes.Length; sha256 = $sha } | ConvertTo-Json)
    $hdr = @{ 'X-X4MP' = '1'; 'Content-Range' = "bytes 0-$($bytes.Length - 1)/$($bytes.Length)" }
    $null = Invoke-RestMethod -Method Put -Uri "$url/api/v1/saves/uploads/$($b.uploadId)" -Headers $hdr -WebSession $web -ContentType 'application/octet-stream' -Body $bytes
    $null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/saves/uploads/$($b.uploadId)/complete" -Headers $h -WebSession $web -ContentType 'application/json' -Body '{}'
    $created = Invoke-RestMethod -Method Post -Uri "$url/api/v1/sessions" -Headers $h -WebSession $web -ContentType 'application/json' -Body (@{ name = 'M3 pair'; saveId = $sha } | ConvertTo-Json)
    $null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/sessions/$($created.id)/start" -Headers $h -WebSession $web -ContentType 'application/json' -Body '{}'
    }

    # ---- the authority ----
    $authWork = Join-Path $tmp 'work-HostAlice'
    $authOut = Join-Path $tmp 'hostsim-HostAlice.out.txt'
    $authArgs = @('--dll', $dll, '--script', $authorityScript, '--work-dir', $authWork,
        '--admin-url', $url, '--admin-user', 'admin', '--admin-password', $adminPassword,
        '--var', "tcp=$TcpPort", '--var', "apw=$nodeAdminPassword", '--var', "sim=$sim", '--var', "ckpt_src=$ckpt1", '--var', "ckpt_src2=$ckpt2",
        '--var', "admin_url=$url", '--var', "admin_pw=$adminPassword", '--var', "sync=$sync", '--var', "avatars=$avatars", '--var', "bots=$Bots", '--var', "avatarsafterjoin=$avatarsAfterJoin",
        '--timeout-scale', '2', '--max-seconds', "$MaxSeconds")
    $auth = Start-P $hostSim $authArgs $authOut
    Wait-Until 'the authority to run the session (auth.running)' { Test-Path (Join-Path $sync 'auth.running') } 180 $auth

    # ---- the bots and the client, at the same time (the string-table gap fix: the bots' avatars are provisioned while the client is still joining) ----
    $botLog = Join-Path $tmp 'fakenode-bots.out.txt'
    $botDuration = $MaxSeconds + 30
    $bot = Start-P $fakeExe @('swarm', '--clients', "$Bots", '--name-prefix', 'Bot', '--avatars', '--chat-echo', '--behavior', 'wander', '--galaxy-file', $galaxy,
        '--seed', '42', '--avatar-timeout', '90', '--server', "127.0.0.1:$TcpPort", '--duration', "$botDuration") $botLog
    $cliWork = Join-Path $tmp 'work-Pia'
    $cliOut = Join-Path $tmp 'hostsim-Pia.out.txt'
    $cliArgs = @('--dll', $dll, '--script', $clientScript, '--work-dir', $cliWork,
        '--admin-url', $url, '--admin-user', 'admin', '--admin-password', $adminPassword,
        '--var', "tcp=$TcpPort", '--var', 'name=Pia', '--var', 'host=HostAlice', '--var', "sync=$sync", '--var', "bots=$Bots", '--var', "ghosts=$ghosts",
        '--var', "spawns=$spawns", '--var', "ghostsafterjoin=$ghostsAfterJoin", '--var', "clientspawns=$clientSpawns", '--var', "reloads=$Reloads", '--timeout-scale', '2', '--max-seconds', "$MaxSeconds")
    $cli = Start-P $hostSim $cliArgs $cliOut

    # ---- M4 criterion 6: the only real mid-transit creation is a player who joins while the client is in the highway
    #      (a sector's entities always arrive in the join batch). Two late bots do exactly that ----
    Wait-Until 'the client to start the highway transit (client.transit_start)' { Test-Path (Join-Path $sync 'client.transit_start') } 240 $cli
    $lateLog = Join-Path $tmp 'fakenode-late.out.txt'
    $late = Start-P $fakeExe @('swarm', '--clients', "$lateBots", '--name-prefix', 'Late', '--avatars', '--chat-echo', '--behavior', 'wander', '--galaxy-file', $galaxy,
        '--seed', '43', '--avatar-timeout', '90', '--server', "127.0.0.1:$TcpPort", '--duration', "$botDuration") $lateLog

    $deadline = (Get-Date).AddSeconds($MaxSeconds + 40)
    $failed = @()
    foreach ($r in @(@{ Name = 'HostAlice (authority)'; Proc = $auth; Out = $authOut; Work = $authWork }, @{ Name = 'Pia (client)'; Proc = $cli; Out = $cliOut; Work = $cliWork })) {
        $left = [int][Math]::Max(1, ($deadline - (Get-Date)).TotalMilliseconds)
        if (-not $r.Proc.WaitForExit($left)) { & taskkill /PID $r.Proc.Id /T /F *> $null; $failed += "$($r.Name) did not finish in time"; continue }
        $r.Proc.WaitForExit()
        Write-Host "--- $($r.Name) (exit $($r.Proc.ExitCode)) ---"
        Get-Content $r.Out -Tail 8 | ForEach-Object { Write-Host "  $_" }
        if ($r.Proc.ExitCode -ne 0) { $failed += "$($r.Name) exited with $($r.Proc.ExitCode); output $($r.Out); mod log $(Join-Path $r.Work 'extension\logs\x4mp.log')" }
    }
    if ($failed.Count -gt 0) { throw ($failed -join '; ') }

    # ---- the numbers: Q3 / criterion 11 from the mod logs themselves ----
    $psHost = (Get-Process -Id $PID).Path
    foreach ($r in @(@{ Name = 'client Pia'; Log = (Join-Path $cliWork 'extension\logs\x4mp.log') }, @{ Name = 'authority HostAlice'; Log = (Join-Path $authWork 'extension\logs\x4mp.log') })) {
        Write-Host ''
        Write-Host "=== sync-report: $($r.Name) ===" -ForegroundColor Cyan
        $rep = Join-Path $tmp ('sync-report-' + ($r.Name -replace '\W', '_') + '.txt')
        # CI limits: the product targets (0.2 ms, 10 lines/s) are in the script defaults; this machine class gets a little room. The ghost windows with the
        # authority flying circles are judged strictly (path error), because that is deterministic.
        $p = Start-P $psHost @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $syncReport, '-Log', $r.Log, '-Strict', '-MaxModP95Ms', "$MaxModP95Ms", '-MaxLogRate', "$MaxLogRate", '-MaxLatP95', '400') $rep
        if (-not $p.WaitForExit(60000)) { & taskkill /PID $p.Id /T /F *> $null; throw "sync-report for $($r.Name) did not finish" }
        $p.WaitForExit()
        Get-Content $rep | ForEach-Object { Write-Host "  $_" }
        if ($p.ExitCode -ne 0) { throw "sync-report found a FAIL for $($r.Name) (exit $($p.ExitCode); see $rep)" }
    }
    # ---- criterion 6 (M4-23): no spawn may land inside the 5 km bubble. Two windows are judged on the client log:
    #      1. the highway transit itself (between the two `highway context` lines). Two FakeNode bots join the moment
    #         the client sets off, so their avatars are created while it is in the highway, 9-20 km away - real
    #         mid-transit ghosts, none of them a pop. -MinSpawns keeps the check from passing on an empty window;
    #      2. one second after the last join pop to the end of the log: the join is one legitimate whole-sector batch
    #         (2000..4455 m in the runs this runner was written against); the flight, gate jump, checkpoint, save load
    #         and reloads all have to stay at zero afterwards.
    $clientLog = Join-Path $cliWork 'extension\logs\x4mp.log'
    $lastPop = $null; $hwOn = $null; $hwOff = $null
    foreach ($line in Get-Content $clientLog) {
        if ($line -match '^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})Z.*spawn-near-player:') {
            $lastPop = [datetime]::ParseExact($matches['ts'], 'yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)
        } elseif ($line -match '^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})Z.*selfship: highway context (?<hw>on|off)') {
            $at = [datetime]::ParseExact($matches['ts'], 'yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)
            if ($matches['hw'] -eq 'on') { if ($null -eq $hwOn) { $hwOn = $at } } else { $hwOff = $at }
        }
    }
    if ($null -eq $hwOn -or $null -eq $hwOff) { throw 'the client log has no highway context on/off pair: the transit phase did not run' }

    Write-Host ''
    Write-Host '=== spawn-near-window: client Pia, the highway transit (criterion 6) ===' -ForegroundColor Cyan
    $nearRep = Join-Path $tmp 'spawn-near-window-transit.txt'
    $nearArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $nearReport, '-Log', $clientLog,
        '-From', $hwOn.ToString('yyyy-MM-dd HH:mm:ss.fff'), '-To', $hwOff.ToString('yyyy-MM-dd HH:mm:ss.fff'), '-MinSpawns', "$lateBots")
    $p = Start-P $psHost $nearArgs $nearRep
    if (-not $p.WaitForExit(60000)) { & taskkill /PID $p.Id /T /F *> $null; throw 'spawn-near-window (transit) did not finish' }
    $p.WaitForExit()
    Get-Content $nearRep | ForEach-Object { Write-Host "  $_" }
    if ($p.ExitCode -ne 0) { throw "criterion 6 failed inside the highway transit (exit $($p.ExitCode); see $nearRep)" }

    Write-Host ''
    Write-Host '=== spawn-near-window: client Pia, after the join batch to the end of the log (criterion 6) ===' -ForegroundColor Cyan
    $nearRep2 = Join-Path $tmp 'spawn-near-window-after-join.txt'
    $nearArgs2 = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $nearReport, '-Log', $clientLog)
    if ($null -ne $lastPop) { $nearArgs2 += @('-From', $lastPop.AddSeconds(1).ToString('yyyy-MM-dd HH:mm:ss.fff')) }
    $p2 = Start-P $psHost $nearArgs2 $nearRep2
    if (-not $p2.WaitForExit(60000)) { & taskkill /PID $p2.Id /T /F *> $null; throw 'spawn-near-window (after join) did not finish' }
    $p2.WaitForExit()
    Get-Content $nearRep2 | ForEach-Object { Write-Host "  $_" }
    if ($p2.ExitCode -ne 0) { throw "criterion 6 failed: a spawn-near-player pop after the join batch (exit $($p2.ExitCode); see $nearRep2)" }
    $exit = 0
}
catch { Write-Host "M3 PAIR E2E FAILED: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    Stop-All
    Write-Host ("M3 pair e2e {0} in {1:N0} s. Temp tree: {2}" -f $(if ($exit -eq 0) { 'PASSED' } else { 'FAILED' }), $sw.Elapsed.TotalSeconds, $tmp)
}
exit $exit
