<#
.SYNOPSIS
  M3-04 hostsim pair runner (CI step HostSimPair): the published server, a FakeNode authority serving a dummy save, and TWO
  x4mp-hostsim processes, each loading its own copy of the real x4mp.dll (two game instances on one machine). The scenario
  (pair_scenario.hostsim) is trivial today; the M3 wave-2 tasks copy it and script ghosts, avatars and own-ship tracking.
.DESCRIPTION
  Needs: mod\build.ps1 (x4mp.dll, x4mp-hostsim.exe) and tools\e2e.ps1 -Steps Publish (server + FakeNode).
  Ports 47940 (TCP), 47941 (UDP), 47942 (HTTP) by default (the pair range is 47940-47949). Everything lives in a temp folder
  (printed as "Temp tree:"); nothing sleeps: it waits on processes and on the server's /healthz with timeouts.
  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\pair_run.ps1 [-Scenario pair_scenario.hostsim]
  Scenario variables the runner provides: tcp, name, other, sync (see pair_scenario.hostsim). The players are Pia and Pax.
#>
[CmdletBinding()]
param(
    [string]$Config = 'relwithdebinfo',
    [int]$TcpPort = 47940, [int]$UdpPort = 47941, [int]$HttpPort = 47942,
    [string]$Scenario = 'pair_scenario.hostsim',
    [string[]]$Players = @('Pia', 'Pax')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$build = Join-Path $repo "mod\build\msvc-x64-$Config"
$hostSim = Join-Path $build 'x4mp-hostsim.exe'
$dll = Join-Path $build 'x4mp.dll'
$serverExe = Join-Path $repo 'out\win-x64\x4mp-server.exe'
$fakeExe = Join-Path $repo 'out\fakenode\X4MP.FakeNode.exe'
foreach ($f in $hostSim, $dll, $serverExe, $fakeExe) { if (-not (Test-Path $f)) { throw "Missing $f (build the mod, then tools\e2e.ps1 -Steps Publish)" } }
$scenarioPath = if ([IO.Path]::IsPathRooted($Scenario)) { $Scenario } else { Join-Path $PSScriptRoot $Scenario }
if (-not (Test-Path $scenarioPath)) { throw "Missing scenario $scenarioPath" }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-pair-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
$sync = Join-Path $tmp 'sync'
New-Item -ItemType Directory -Force $sync | Out-Null
$adminPassword = 'Pair-e2e-only-password-12345'
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
# Condition wait with a deadline. No sleeping: between probes it blocks on the process the condition depends on
# (Wait-Process -Timeout returns early if that process dies, which fails the wait at once).
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
    # dummy save: a real gzip of a small XML plus 1 MB of incompressible data
    $dummy = Join-Path $tmp 'authority_save.xml.gz'
    $rnd = New-Object byte[] (1MB); (New-Object Random 11).NextBytes($rnd)
    $fs = [IO.File]::Create($dummy); $gz = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Compress)
    $head = [Text.Encoding]::UTF8.GetBytes('<?xml version="1.0" encoding="utf-8"?><savegame><info><game id="x4mp-pair-e2e"/></info></savegame>')
    $gz.Write($head, 0, $head.Length); $gz.Write($rnd, 0, $rnd.Length); $gz.Dispose(); $fs.Dispose()

    $data = Join-Path $tmp 'data'; New-Item -ItemType Directory -Force $data | Out-Null
    $envVars = @{ X4MP__Net__NodeTcpEndpoint = "127.0.0.1:$TcpPort"; X4MP__Net__UdpPort = "$UdpPort"; X4MP__Net__ModBuildStrict = 'false'
        X4MP__Net__MaxPlayers = '8'; X4MP__Net__MaxConnectionsPerIp = '64' }
    $server = Start-P $serverExe @('--data-dir', $data, '--port', "$HttpPort") (Join-Path $tmp 'server.out.txt') $envVars
    $url = "http://127.0.0.1:$HttpPort"
    Wait-Until 'the server' { if ($server.HasExited) { throw 'server exited' }; (Invoke-WebRequest -UseBasicParsing "$url/healthz" -TimeoutSec 2).StatusCode -eq 200 } 40 $server

    # bootstrap admin (forced password change), as tools\e2e.ps1 does
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

    # FakeNode authority offering the dummy save
    $fakeLog = Join-Path $tmp 'fakenode.out.txt'
    $fake = Start-P $fakeExe @('authority', '--server', "127.0.0.1:$TcpPort", '--name', 'FakeAuthority', '--save-file', $dummy) $fakeLog
    Wait-Until 'the authority checkpoint' { (Test-Path $fakeLog) -and (Select-String -Path $fakeLog -Pattern 'checkpoint stored' -Quiet) } 90 $fake

    # TWO hostsim processes at the same time, each with its own work dir (own config, key file, stash, log) and its own x4mp.dll instance
    $runs = @()
    for ($i = 0; $i -lt $Players.Count; $i++) {
        $name = $Players[$i]; $other = $Players[($i + 1) % $Players.Count]
        $out = Join-Path $tmp "hostsim-$name.out.txt"
        $a = @('--dll', $dll, '--script', $scenarioPath, '--work-dir', (Join-Path $tmp "work-$name"),
            '--admin-url', $url, '--admin-user', 'admin', '--admin-password', $adminPassword,
            '--var', "tcp=$TcpPort", '--var', "name=$name", '--var', "other=$other", '--var', "sync=$sync",
            '--timeout-scale', '5', '--max-seconds', '120')
        $runs += [pscustomobject]@{ Name = $name; Out = $out; Proc = (Start-P $hostSim $a $out) }
    }
    $deadline = (Get-Date).AddSeconds(150)
    $failed = @()
    foreach ($r in $runs) {
        $left = [int][Math]::Max(1, ($deadline - (Get-Date)).TotalMilliseconds)
        if (-not $r.Proc.WaitForExit($left)) { & taskkill /PID $r.Proc.Id /T /F *> $null; $failed += "$($r.Name) did not finish in time"; continue }
        $r.Proc.WaitForExit()
        Write-Host "--- $($r.Name) (exit $($r.Proc.ExitCode)) ---"
        Get-Content $r.Out -Tail 6 | ForEach-Object { Write-Host "  $_" }
        if ($r.Proc.ExitCode -ne 0) { $failed += "$($r.Name) exited with $($r.Proc.ExitCode)" }
    }
    if ($failed.Count -gt 0) { throw ($failed -join '; ') }
    $exit = 0
}
catch { Write-Host "PAIR E2E FAILED: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    Stop-All
    Write-Host ("Pair e2e {0} in {1:N0} s. Temp tree: {2}" -f $(if ($exit -eq 0) { 'PASSED' } else { 'FAILED' }), $sw.Elapsed.TotalSeconds, $tmp)
}
exit $exit
