<#
.SYNOPSIS
  M2-06 end-to-end (NOT part of CI): the real x4mp.dll in x4mp-hostsim joins the published server whose authority is a FakeNode
  serving a dummy save. Scenarios: join_flow (join, download, loadSave, reload+resume, universe ready, NodeReady, the server shows the
  player InGame), join_reject_name, join_reject_full, join_reject_banned (the build rejection needs no server: ctest hostsim.join_refused_build).
.DESCRIPTION
  Needs: mod\build.ps1 (x4mp.dll, x4mp-hostsim.exe, x4mp-headless.exe) and tools\e2e.ps1 -Steps Publish (server + FakeNode).
  Ports 47953 (TCP), 47954 (UDP), 47955 (HTTP). Everything lives in a temp folder. About 1.5 minutes.
  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\join_flow_run.ps1
#>
[CmdletBinding()]
param([string]$Config = 'relwithdebinfo', [int]$TcpPort = 47953, [int]$UdpPort = 47954, [int]$HttpPort = 47955)
$ErrorActionPreference = 'Stop'
function Get-Sha256Hex([string]$Path) { $s = [IO.File]::OpenRead($Path); try { $h = [Security.Cryptography.SHA256]::Create(); try { return ([BitConverter]::ToString($h.ComputeHash($s)) -replace '-', '').ToLowerInvariant() } finally { $h.Dispose() } } finally { $s.Dispose() } }  # not Get-FileHash: a 5.1 child of pwsh 7 cannot autoload Microsoft.PowerShell.Utility
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$build = Join-Path $repo "mod\build\msvc-x64-$Config"
$hostSim = Join-Path $build 'x4mp-hostsim.exe'
$dll = Join-Path $build 'x4mp.dll'
$headless = Join-Path $build 'x4mp-headless.exe'
$serverExe = Join-Path $repo 'out\win-x64\x4mp-server.exe'
$fakeExe = Join-Path $repo 'out\fakenode\X4MP.FakeNode.exe'
foreach ($f in $hostSim, $dll, $headless, $serverExe, $fakeExe) { if (-not (Test-Path $f)) { throw "Missing $f (build the mod, then tools\e2e.ps1 -Steps Publish)" } }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-join-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
$adminPassword = 'Join-e2e-only-password-12345'
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
    } finally { foreach ($k in $old.Keys) { [Environment]::SetEnvironmentVariable($k, $old[$k]) } }
    $null = $p.Handle; $procs.Add($p); return $p
}
function Wait-Until($what, [scriptblock]$probe, $sec = 40) {
    $end = (Get-Date).AddSeconds($sec)
    while ($true) { try { if (& $probe) { return } } catch { }; if ((Get-Date) -gt $end) { throw "Timed out waiting for $what" }; Start-Sleep -Milliseconds 250 }
}
function Run-Scenario($name, $workName, $vars) {
    $args2 = @('--dll', $dll, '--script', (Join-Path $PSScriptRoot "$name.hostsim"), '--work-dir', (Join-Path $tmp $workName),
        '--admin-url', "http://127.0.0.1:$HttpPort", '--admin-user', 'admin', '--admin-password', $adminPassword,
        '--var', "tcp=$TcpPort", '--var', "save_sha=$dummySha", '--max-seconds', '120') + $vars
    $out = Join-Path $tmp "$name.out.txt"
    $t0 = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-P $hostSim $args2 $out
    if (-not $p.WaitForExit(140000)) { & taskkill /PID $p.Id /T /F *> $null; throw "$name did not finish in 140 s" }
    $p.WaitForExit()
    Get-Content $out -Tail 6 | ForEach-Object { Write-Host "  $_" }
    if ($p.ExitCode -ne 0) { throw "$name failed (exit $($p.ExitCode)); output: $out" }
    Write-Host ("{0} PASSED in {1:N1} s" -f $name, $t0.Elapsed.TotalSeconds) -ForegroundColor Green
}

try {
    # dummy save: a real gzip of a small XML plus 3 MB of incompressible data (several chunks)
    $dummy = Join-Path $tmp 'authority_save.xml.gz'
    $rnd = New-Object byte[] (3MB); (New-Object Random 7).NextBytes($rnd)
    $fs = [IO.File]::Create($dummy); $gz = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Compress)
    $head = [Text.Encoding]::UTF8.GetBytes('<?xml version="1.0" encoding="utf-8"?><savegame><info><game id="x4mp-join-e2e"/></info></savegame>')
    $gz.Write($head, 0, $head.Length); $gz.Write($rnd, 0, $rnd.Length); $gz.Dispose(); $fs.Dispose()
    $dummySha = Get-Sha256Hex $dummy

    $data = Join-Path $tmp 'data'; New-Item -ItemType Directory -Force $data | Out-Null
    $envVars = @{ X4MP__Net__NodeTcpEndpoint = "127.0.0.1:$TcpPort"; X4MP__Net__UdpPort = "$UdpPort"; X4MP__Net__ModBuildStrict = 'false'
        X4MP__Net__MaxPlayers = '2'; X4MP__Net__MaxConnectionsPerIp = '64' }
    $server = Start-P $serverExe @('--data-dir', $data, '--port', "$HttpPort") (Join-Path $tmp 'server.out.txt') $envVars
    $url = "http://127.0.0.1:$HttpPort"
    Wait-Until 'the server' { if ($server.HasExited) { throw 'server exited' }; (Invoke-WebRequest -UseBasicParsing "$url/healthz" -TimeoutSec 2).StatusCode -eq 200 }

    # bootstrap admin (forced password change), as tools\e2e.ps1 does
    $pwFile = Join-Path $data 'initial-admin-password.txt'
    Wait-Until 'initial-admin-password.txt' { (Test-Path $pwFile) -and (Get-Content $pwFile -Raw).Trim() } 20
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
    Wait-Until 'the authority checkpoint' { (Test-Path $fakeLog) -and (Select-String -Path $fakeLog -Pattern 'checkpoint stored' -Quiet) } 90

    Run-Scenario 'join_flow' 'work-alice' @()

    # ban Alice (the player key stays in work-alice), then she tries again with the same key
    $players = Invoke-RestMethod -Uri "$url/api/v1/players" -Headers $h -WebSession $s2
    $list = if ($players -is [array]) { $players } else { $players.items }
    $alice = $list | Where-Object { $_.name -eq 'Alice' } | Select-Object -First 1
    if (-not $alice) { throw 'Alice is not in the player list' }
    $null = & $post '/api/v1/bans' @{ playerId = $alice.id; reason = 'join e2e' } $s2
    Run-Scenario 'join_reject_banned' 'work-alice' @()

    Run-Scenario 'join_reject_name' 'work-name' @()

    # fill the session (authority + holder = MaxPlayers 2), then a third player is refused as full
    $holder = Start-P $headless @('--port', "$TcpPort", '--name', 'Holder', '--ping', '40', '--timeout', '60') (Join-Path $tmp 'holder.out.txt')
    Wait-Until 'the holder to join' {
        $pl = Invoke-RestMethod -Uri "$url/api/v1/players" -Headers $h -WebSession $s2
        $items = if ($pl -is [array]) { $pl } else { $pl.items }
        @($items | Where-Object { $_.name -eq 'Holder' -and $_.online }).Count -gt 0
    } 20
    Run-Scenario 'join_reject_full' 'work-full' @()

    # Session 3, B4: a player in game, the server (and the authority's session) restart, the resume token is refused. The mod must report the
    # RUNNING universe again (the session save equals it) and the server must show the player InGame; nothing may be loaded.
    if (-not $holder.HasExited) { & cmd.exe /c "taskkill /PID $($holder.Id) /T /F >nul 2>&1" }
    $pidFile = Join-Path $tmp 'restart.pids'
    Set-Content $pidFile "$($server.Id)`r`n$($fake.Id)"
    $restart = Join-Path $tmp 'restart_server.ps1'
    $restartText = @'
$ErrorActionPreference = 'Stop'
foreach ($old in (Get-Content '@PIDS@')) { if ($old.Trim()) { & cmd.exe /c "taskkill /PID $($old.Trim()) /T /F >nul 2>&1" } }
$env:X4MP__Net__NodeTcpEndpoint = '127.0.0.1:@TCP@'; $env:X4MP__Net__UdpPort = '@UDP@'; $env:X4MP__Net__ModBuildStrict = 'false'
$env:X4MP__Net__MaxPlayers = '2'; $env:X4MP__Net__MaxConnectionsPerIp = '64'
$srv = Start-Process '@SERVER@' -ArgumentList @('--data-dir', '@DATA@', '--port', '@HTTP@') -PassThru -WindowStyle Hidden -RedirectStandardOutput '@OUT@\server2.out.txt' -RedirectStandardError '@OUT@\server2.err.txt'
$end = (Get-Date).AddSeconds(60)
while ($true) {
  if ($srv.HasExited) { throw 'the restarted server exited' }
  try { if ((Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:@HTTP@/healthz' -TimeoutSec 2).StatusCode -eq 200) { break } } catch { }
  if ((Get-Date) -gt $end) { throw 'the restarted server never became healthy' }
  Wait-Process -Id $srv.Id -Timeout 1 -ErrorAction SilentlyContinue
}
$fakeLog2 = '@OUT@\fakenode2.out.txt'
$fk = Start-Process '@FAKE@' -ArgumentList @('authority', '--server', '127.0.0.1:@TCP@', '--name', 'FakeAuthority', '--save-file', '@SAVE@') -PassThru -WindowStyle Hidden -RedirectStandardOutput $fakeLog2 -RedirectStandardError '@OUT@\fakenode2.err.txt'
Set-Content '@PIDS@' "$($srv.Id)`r`n$($fk.Id)"
$end = (Get-Date).AddSeconds(120)
while ($true) {
  if ($fk.HasExited) { throw 'the restarted FakeNode exited' }
  if ((Test-Path $fakeLog2) -and (Select-String -Path $fakeLog2 -Pattern 'checkpoint stored' -Quiet)) { break }
  if ((Get-Date) -gt $end) { throw 'no checkpoint stored after the restart' }
  Wait-Process -Id $fk.Id -Timeout 1 -ErrorAction SilentlyContinue
}
'restarted'
'@
    $restartText = $restartText.Replace('@PIDS@', $pidFile).Replace('@TCP@', "$TcpPort").Replace('@UDP@', "$UdpPort").Replace('@HTTP@', "$HttpPort").Replace('@SERVER@', $serverExe).Replace('@DATA@', $data).Replace('@OUT@', $tmp).Replace('@FAKE@', $fakeExe).Replace('@SAVE@', $dummy)
    Set-Content $restart $restartText
    Run-Scenario 'join_rejoin' 'work-rita' @('--var', "restart=$restart")
    $ritaLog = Get-Content (Join-Path $tmp 'work-rita\extension\logs\x4mp.log')
    $nodeReady = @($ritaLog | Where-Object { $_ -match 'universe ready: NodeReady sent' }).Count
    $loads = @($ritaLog | Where-Object { $_ -match 'raised the Lua event loadSave' }).Count
    $rejoins = @($ritaLog | Where-Object { $_ -match 'rejoin: the session save is the running universe' }).Count
    Write-Host "rejoin verdicts: NodeReady=$nodeReady (expect 2) loadSave=$loads (expect 1) rejoin=$rejoins (expect 1)"
    if ($nodeReady -ne 2 -or $loads -ne 1 -or $rejoins -ne 1) { throw "the in-game rejoin did not behave: NodeReady=$nodeReady loadSave=$loads rejoin=$rejoins" }
    $exit = 0
}
catch { Write-Host "JOIN E2E FAILED: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    Stop-All
    # the processes the B4 restart script started (hostsim ran it, so they are not in $procs)
    if ($pidFile -and (Test-Path $pidFile)) { foreach ($id in (Get-Content $pidFile)) { if ($id.Trim()) { try { & cmd.exe /c "taskkill /PID $($id.Trim()) /T /F >nul 2>&1" } catch { } } } }
    Write-Host ("Join e2e {0} in {1:N0} s. Temp tree: {2}" -f $(if ($exit -eq 0) { 'PASSED' } else { 'FAILED' }), $sw.Elapsed.TotalSeconds, $tmp)
}
exit $exit
