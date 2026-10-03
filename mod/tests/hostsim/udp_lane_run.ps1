<#
.SYNOPSIS
  M3-03 live test (NOT part of ctest): the mod's UDP realtime lane (x4mp-headless, the real Session + net layer) against the published
  server with a FakeNode authority. Scenarios:
    udp_force        --udp force: the lane binds and carries datagrams both ways (no TCP fallback possible), the TCP connection stays up.
    udp_loss5        --udp-loss 5 (5 % of datagrams dropped each way) + a 20 Hz probe stream: stays Active, no fallback, loss visible.
    udp_blocked      --udp-block (a firewall from the start): never binds, Realtime falls back to TCP within 3 s (Fallback), no disconnect.
    udp_block_after  the firewall appears 3 s into a bound session: back on TCP within 3.5 s of the block, no disconnect.
    udp_off          --udp off: the lane never starts (capability not advertised), the session works.
.DESCRIPTION
  Needs: mod\build.ps1 (x4mp-headless.exe) and tools\e2e.ps1 -Steps Publish (server + FakeNode). Ports 47920 (TCP), 47921 (UDP),
  47922 (HTTP): no other suite uses 47920-47929. Everything lives in a temp folder. About 2 minutes. No sleeping: waits are
  process waits with a timeout.
  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\udp_lane_run.ps1
  CI (describe-only, M3-03 handoff): add it as an e2e step 'UdpLane' next to HostSim (tools/e2e.ps1 owns the step list).
#>
[CmdletBinding()]
param([string]$Config = 'relwithdebinfo', [int]$TcpPort = 47920, [int]$UdpPort = 47921, [int]$HttpPort = 47922)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$build = Join-Path $repo "mod\build\msvc-x64-$Config"
$headless = Join-Path $build 'x4mp-headless.exe'
$serverExe = Join-Path $repo 'out\win-x64\x4mp-server.exe'
$fakeExe = Join-Path $repo 'out\fakenode\X4MP.FakeNode.exe'
foreach ($f in $headless, $serverExe, $fakeExe) { if (-not (Test-Path $f)) { throw "Missing $f (build the mod, then tools\e2e.ps1 -Steps Publish)" } }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-udp-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
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
# Waits for a condition; the pause between probes is a process wait (a timeout on a process that is still running), not a sleep.
function Wait-Until($what, $proc, [scriptblock]$probe, $sec = 40) {
    $end = (Get-Date).AddSeconds($sec)
    while ($true) {
        try { if (& $probe) { return } } catch { }
        if ($proc.HasExited) { throw "${what}: the process exited ($($proc.ExitCode))" }
        if ((Get-Date) -gt $end) { throw "Timed out waiting for $what" }
        $null = $proc.WaitForExit(250)
    }
}
$scenarioNo = 0
function Run-Headless($name, $extra, $mustMatch) {
    $script:scenarioNo++
    $out = Join-Path $tmp "$name.out.txt"
    $a = @('--host', '127.0.0.1', '--port', "$TcpPort", '--name', "Udp$($script:scenarioNo)", '--key-file', (Join-Path $tmp "$name.key"), '--timeout', '60') + $extra
    $t0 = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-P $headless $a $out
    if (-not $p.WaitForExit(90000)) { & taskkill /PID $p.Id /T /F *> $null; throw "$name did not finish in 90 s" }
    $p.WaitForExit()
    $text = Get-Content $out -Raw
    Get-Content $out | Where-Object { $_ -match '^udp:|^OK|^FAILED' } | Select-Object -Last 6 | ForEach-Object { Write-Host "  $_" }
    if ($p.ExitCode -ne 0) { throw "$name failed (exit $($p.ExitCode)); output: $out" }
    foreach ($m in $mustMatch) { if ($text -notmatch $m) { throw "${name}: output does not match /$m/ ($out)" } }
    Write-Host ("{0} PASSED in {1:N1} s" -f $name, $t0.Elapsed.TotalSeconds) -ForegroundColor Green
}

try {
    $data = Join-Path $tmp 'data'; New-Item -ItemType Directory -Force $data | Out-Null
    $envVars = @{ X4MP__Net__NodeTcpEndpoint = "127.0.0.1:$TcpPort"; X4MP__Net__UdpPort = "$UdpPort"; X4MP__Net__ModBuildStrict = 'false'
        X4MP__Net__MaxPlayers = '8'; X4MP__Net__MaxConnectionsPerIp = '64' }
    $server = Start-P $serverExe @('--data-dir', $data, '--port', "$HttpPort") (Join-Path $tmp 'server.out.txt') $envVars
    Wait-Until 'the server' $server { (Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$HttpPort/healthz" -TimeoutSec 2).StatusCode -eq 200 }

    $fakeLog = Join-Path $tmp 'fakenode.out.txt'
    $fake = Start-P $fakeExe @('authority', '--server', "127.0.0.1:$TcpPort", '--name', 'FakeAuthority', '--duration', '240', '--sectors', '20', '--ships', '50') $fakeLog
    Wait-Until 'the FakeNode authority to be in game' $fake { (Test-Path $fakeLog) -and (Select-String -Path $fakeLog -Pattern 'in game' -Quiet) } 60

    # 1. UDP forced: Realtime never falls back to TCP, so a bound lane proves the datagrams flow both ways.
    Run-Headless 'udp_force' @('--udp', 'force', '--udp-keepalive-ms', '100', '--ping', '6', '--expect-udp', 'active') @('udp: state=active', 'stayed up')

    # 2. 5 % loss in both directions, a 20 Hz probe stream: stays on UDP.
    Run-Headless 'udp_loss5' @('--udp', 'auto', '--udp-loss', '5', '--udp-seed', '4242', '--udp-keepalive-ms', '50', '--ping', '15', '--expect-udp', 'active') @('udp: state=active', 'stayed up', 'fallbacks=0')

    # 3. UDP blocked from the start: TCP within 3 s, no disconnect (Fallback at the end of a 6 s window).
    Run-Headless 'udp_blocked' @('--udp', 'auto', '--udp-block', '--ping', '6', '--expect-udp', 'fallback') @('udp: state=fallback', 'stayed up')

    # 4. UDP bound, the firewall appears 3 s later: Fallback within 3.5 s of the block.
    Run-Headless 'udp_block_after' @('--udp', 'auto', '--udp-keepalive-ms', '100', '--udp-block-after', '3', '--ping', '9', '--expect-udp', 'fallback') @('udp: BLOCKED', 'udp: fell back to TCP', 'stayed up')

    # 5. UDP off: no lane at all.
    Run-Headless 'udp_off' @('--udp', 'off', '--ping', '4', '--expect-udp', 'off') @('udp: state=off', 'stayed up')
    $exit = 0
}
catch { Write-Host "UDP LANE E2E FAILED: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    Stop-All
    Write-Host ("UDP lane e2e {0} in {1:N0} s. Temp tree: {2}" -f $(if ($exit -eq 0) { 'PASSED' } else { 'FAILED' }), $sw.Elapsed.TotalSeconds, $tmp)
}
exit $exit
