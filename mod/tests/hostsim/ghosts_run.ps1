<#
.SYNOPSIS
  M3-10 ghosts e2e (CI step HostSimGhosts): the published server, a FakeNode authority (avatars, host ship, the synthetic galaxy file),
  FakeNode bots that really fly (one stays, one leaves), and ONE x4mp-hostsim process with the real x4mp.dll as the client under test.
.DESCRIPTION
  The hostsim client sees the bots as ghosts in its fake universe (spawn + dress + inert + radar + minimum hull, per-frame motion from
  Replication, velocity hints, parked label after a bot left) and survives N DLL reloads without duplicates. The path error is the one
  the mod measures itself ([sync] lines). See ghosts_scenario.hostsim for the checks.
  Needs: mod\build.ps1 (x4mp.dll, x4mp-hostsim.exe) and tools\e2e.ps1 -Steps Publish (server + FakeNode).
  Ports 47965 (TCP), 47966 (UDP), 47967 (HTTP) by default (the pair runner has 47940-47942; another hostsim run may use 47944-47946). Nothing sleeps: it waits on processes and
  on the server's /healthz with timeouts.
  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\ghosts_run.ps1 [-Reloads 20]
#>
[CmdletBinding()]
param(
    [string]$Config = 'relwithdebinfo',
    [int]$TcpPort = 47965, [int]$UdpPort = 47966, [int]$HttpPort = 47967,
    [string]$Scenario = 'ghosts_scenario.hostsim',
    [int]$Reloads = 20,
    [int]$MaxSeconds = 330
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$build = Join-Path $repo "mod\build\msvc-x64-$Config"
$hostSim = Join-Path $build 'x4mp-hostsim.exe'
$dll = Join-Path $build 'x4mp.dll'
$serverExe = Join-Path $repo 'out\win-x64\x4mp-server.exe'
$fakeExe = Join-Path $repo 'out\fakenode\X4MP.FakeNode.exe'
$galaxy = Join-Path $repo 'server\tests\X4MP.FakeNode.Tests\Fixtures\galaxy-dump-small.json'
foreach ($f in $hostSim, $dll, $serverExe, $fakeExe, $galaxy) { if (-not (Test-Path $f)) { throw "Missing $f (build the mod, then tools\e2e.ps1 -Steps Publish)" } }
$scenarioPath = if ([IO.Path]::IsPathRooted($Scenario)) { $Scenario } else { Join-Path $PSScriptRoot $Scenario }
if (-not (Test-Path $scenarioPath)) { throw "Missing scenario $scenarioPath" }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-ghosts-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
$adminPassword = 'Ghosts-e2e-only-password-12345'
$procs = New-Object System.Collections.Generic.List[object]
$sw = [Diagnostics.Stopwatch]::StartNew()
$exit = 1
function Stop-All { foreach ($p in $procs) { if (-not $p.HasExited) { try { & taskkill /PID $p.Id /T /F 2>$null | Out-Null } catch { } } } }
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

try {
    $dummy = Join-Path $tmp 'authority_save.xml.gz'
    $rnd = New-Object byte[] (1MB); (New-Object Random 11).NextBytes($rnd)
    $fs = [IO.File]::Create($dummy); $gz = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Compress)
    $head = [Text.Encoding]::UTF8.GetBytes('<?xml version="1.0" encoding="utf-8"?><savegame><info><game id="x4mp-ghosts-e2e"/></info></savegame>')
    $gz.Write($head, 0, $head.Length); $gz.Write($rnd, 0, $rnd.Length); $gz.Dispose(); $fs.Dispose()

    $data = Join-Path $tmp 'data'; New-Item -ItemType Directory -Force $data | Out-Null
    $envVars = @{ X4MP__Net__NodeTcpEndpoint = "127.0.0.1:$TcpPort"; X4MP__Net__UdpPort = "$UdpPort"; X4MP__Net__ModBuildStrict = 'false'
        X4MP__Net__MaxPlayers = '8'; X4MP__Net__MaxConnectionsPerIp = '64'
        X4MP__Interest__NearRadiusM = '60000' }  # the bots wander +-15 km: all of them stay in the 20 Hz Near tier
    $server = Start-P $serverExe @('--data-dir', $data, '--port', "$HttpPort") (Join-Path $tmp 'server.out.txt') $envVars
    $url = "http://127.0.0.1:$HttpPort"
    Wait-Until 'the server' { if ($server.HasExited) { throw 'server exited' }; (Invoke-WebRequest -UseBasicParsing "$url/healthz" -TimeoutSec 2).StatusCode -eq 200 } 40 $server

    $pwFile = Join-Path $data 'initial-admin-password.txt'
    Wait-Until 'initial-admin-password.txt' { (Test-Path $pwFile) -and (Get-Content $pwFile -Raw).Trim() } 20 $server
    $initial = (Get-Content $pwFile -Raw).Trim()
    $h = @{ 'X-X4MP' = '1' }
    $post = { param($path, $body, $session) Invoke-RestMethod -Method Post -Uri "$url$path" -Headers $h -WebSession $session -ContentType 'application/json' -Body ($body | ConvertTo-Json) }
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $null = & $post '/api/v1/auth/login' @{ username = 'admin'; password = $initial } $s
    $null = & $post '/api/v1/auth/change-password' @{ current = $initial; new = $adminPassword } $s
    $s2 = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $null = & $post '/api/v1/auth/login' @{ username = 'admin'; password = $adminPassword } $s2

    # FakeNode authority (answers PlayerShip: avatars + the host ship) on the synthetic galaxy; the bots must use the same file and seed
    $seed = '42'
    $fakeLog = Join-Path $tmp 'fakenode-authority.out.txt'
    $fake = Start-P $fakeExe @('authority', '--server', "127.0.0.1:$TcpPort", '--name', 'FakeAuthority', '--save-mb', '1', '--galaxy-file', $galaxy, '--seed', $seed) $fakeLog
    Wait-Until 'the authority checkpoint' { (Test-Path $fakeLog) -and (Select-String -Path $fakeLog -Pattern 'checkpoint stored' -Quiet) } 90 $fake

    # one bot that stays for the whole run, one that leaves after 80 s (its avatar is then parked: "(offline)")
    $botLog = Join-Path $tmp 'fakenode-bot.out.txt'
    $bot = Start-P $fakeExe @('swarm', '--clients', '1', '--name-prefix', 'Bot', '--avatars', '--behavior', 'wander', '--galaxy-file', $galaxy, '--seed', $seed,
        '--server', "127.0.0.1:$TcpPort", '--duration', '600') $botLog
    $leaverLog = Join-Path $tmp 'fakenode-leaver.out.txt'
    $leaver = Start-P $fakeExe @('swarm', '--clients', '1', '--name-prefix', 'Zed', '--avatars', '--behavior', 'wander', '--galaxy-file', $galaxy, '--seed', $seed,
        '--server', "127.0.0.1:$TcpPort", '--duration', '80') $leaverLog

    # M3-14: the client under test joins right after the bots, while their avatars (and the macro / faction strings they add to the session table) are
    # still being provisioned. Before the server fix a node that was still joining could miss those strings and then never resolve a ship macro,
    # which is why this run used to wait for both avatars first. It is now a regression test of the fix.

    $out = Join-Path $tmp 'hostsim-Pia.out.txt'
    $a = @('--dll', $dll, '--script', $scenarioPath, '--work-dir', (Join-Path $tmp 'work-Pia'),
        '--admin-url', $url, '--admin-user', 'admin', '--admin-password', $adminPassword,
        '--var', "tcp=$TcpPort", '--var', 'name=Pia', '--var', 'bot=Bot01', '--var', 'leaver=Zed01', '--var', "reloads=$Reloads",
        '--timeout-scale', '5', '--max-seconds', "$MaxSeconds")
    $run = Start-P $hostSim $a $out
    if (-not $run.WaitForExit(($MaxSeconds + 30) * 1000)) { & taskkill /PID $run.Id /T /F *> $null; throw 'the hostsim client did not finish in time' }
    $run.WaitForExit()
    Write-Host "--- Pia (exit $($run.ExitCode)) ---"
    Get-Content $out -Tail 12 | ForEach-Object { Write-Host "  $_" }
    $syncLines = Get-ChildItem -Path (Join-Path $tmp 'work-Pia') -Recurse -Filter 'x4mp.log' -ErrorAction SilentlyContinue | Select-String -Pattern '\[sync\]' | Select-Object -Last 6
    foreach ($l in $syncLines) { Write-Host "  $($l.Line)" }
    if ($run.ExitCode -ne 0) { throw "the hostsim client exited with $($run.ExitCode)" }
    $exit = 0
}
catch { Write-Host "GHOSTS E2E FAILED: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    Stop-All
    Write-Host ("Ghosts e2e {0} in {1:N0} s. Temp tree: {2}" -f $(if ($exit -eq 0) { 'PASSED' } else { 'FAILED' }), $sw.Elapsed.TotalSeconds, $tmp)
}
exit $exit
