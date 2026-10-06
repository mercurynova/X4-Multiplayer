<#
.SYNOPSIS
  M3-11 end-to-end (NOT part of CI): the real x4mp.dll in x4mp-hostsim is the AUTHORITY, FakeNode bots (--avatars) are the players: the avatars
  are provisioned, driven, parked, listed in the checkpoint manifest and bound again after a "save load" that renumbers every id.
.DESCRIPTION
  avatars.hostsim plays the Lua/MD side (sector map, team faction report, safe position / dress answers). Needs mod\build.ps1 and
  tools\e2e.ps1 -Steps Publish. Ports 47944 (TCP), 47945 (UDP), 47946 (HTTP). About 3 minutes. Run:
    powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\avatars_run.ps1
  (the text below is the M2-09 runner this one was copied from)
  Scenario 1 (authority_flow.hostsim): join as authority, SessionSaveInfo, download, "load" (reload + universe ready), NodeReady,
    RequestSave{SessionStart} answered through the Lua bridge (played by the script), checkpoint uploaded, self-spawn, session Running,
    3 FakeNode clients join and verify the checkpoint, "Save now" makes a second checkpoint.
  Scenario 2 (authority_running_save.hostsim): the authority already runs the start save (sha in ClientHello): no SessionSaveInfo,
    ready report, SessionStart checkpoint, Running.
  Both check the server log for the self-spawn game_time (criterion 11, nonzero).
  Needs: mod\build.ps1 (x4mp.dll, x4mp-hostsim.exe) and tools\e2e.ps1 -Steps Publish (server + FakeNode).
  Ports 47956 (TCP), 47957 (UDP), 47958 (HTTP). Everything lives in a temp folder. About 2 minutes.
  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\authority_flow_run.ps1
#>
[CmdletBinding()]
param([string]$Config = 'relwithdebinfo', [int]$TcpPort = 47944, [int]$UdpPort = 47945, [int]$HttpPort = 47946)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$build = Join-Path $repo "mod\build\msvc-x64-$Config"
$hostSim = Join-Path $build 'x4mp-hostsim.exe'
$dll = Join-Path $build 'x4mp.dll'
$serverExe = Join-Path $repo 'out\win-x64\x4mp-server.exe'
$fakeExe = Join-Path $repo 'out\fakenode\X4MP.FakeNode.exe'
foreach ($f in $hostSim, $dll, $serverExe, $fakeExe) { if (-not (Test-Path $f)) { throw "Missing $f (build the mod, then tools\e2e.ps1 -Steps Publish)" } }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-avatars-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
$adminPassword = 'Authority-e2e-only-password-12345'    # the GUI admin account (REST)
$nodeAdminPassword = 'Authority-e2e-host-key-67890'      # the in-game admin proof (X4MP__Net__AdminPassword)
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
    } finally { foreach ($k in $old.Keys) { [Environment]::SetEnvironmentVariable($k, $old[$k]) } }
    $null = $p.Handle; $procs.Add($p); return $p
}
function Wait-Until($what, [scriptblock]$probe, $sec = 40) {
    $end = (Get-Date).AddSeconds($sec)
    # between probes it blocks on the server process (returns early when it dies); no fixed sleeps
    while ($true) {
        try { if (& $probe) { return } } catch { }
        if ($script:server.HasExited) { throw "the server exited while waiting for $what" }
        if ((Get-Date) -gt $end) { throw "Timed out waiting for $what" }
        Wait-Process -Id $script:server.Id -Timeout 1 -ErrorAction SilentlyContinue
    }
}
function Get-Sha256Hex([string]$Path) { $s = [IO.File]::OpenRead($Path); try { $h = [Security.Cryptography.SHA256]::Create(); try { return ([BitConverter]::ToString($h.ComputeHash($s)) -replace '-', '').ToLowerInvariant() } finally { $h.Dispose() } } finally { $s.Dispose() } }  # not Get-FileHash: a 5.1 child of pwsh 7 cannot autoload Microsoft.PowerShell.Utility
function New-GzSave($path, $seed) {
    $rnd = New-Object byte[] (3MB); (New-Object Random $seed).NextBytes($rnd)
    $fs = [IO.File]::Create($path); $gz = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Compress)
    $head = [Text.Encoding]::UTF8.GetBytes("<?xml version=`"1.0`" encoding=`"utf-8`"?><savegame><info><game id=`"x4mp-authority-e2e-$seed`"/></info></savegame>")
    $gz.Write($head, 0, $head.Length); $gz.Write($rnd, 0, $rnd.Length); $gz.Dispose(); $fs.Dispose()
    return Get-Sha256Hex $path
}
function Start-FreshServer($tag) {
    $data = Join-Path $tmp "data-$tag"; New-Item -ItemType Directory -Force $data | Out-Null
    $envVars = @{ X4MP__Net__NodeTcpEndpoint = "127.0.0.1:$TcpPort"; X4MP__Net__UdpPort = "$UdpPort"; X4MP__Net__ModBuildStrict = 'false'
        X4MP__Net__MaxPlayers = '8'; X4MP__Net__MaxConnectionsPerIp = '64'; X4MP__Net__AdminPassword = $nodeAdminPassword }
    $log = Join-Path $tmp "server-$tag.out.txt"
    $script:server = Start-P $serverExe @('--data-dir', $data, '--port', "$HttpPort") $log $envVars
    Wait-Until 'the server' { if ($script:server.HasExited) { throw 'server exited' }; (Invoke-WebRequest -UseBasicParsing "$url/healthz" -TimeoutSec 2).StatusCode -eq 200 }
    $pwFile = Join-Path $data 'initial-admin-password.txt'
    Wait-Until 'initial-admin-password.txt' { (Test-Path $pwFile) -and (Get-Content $pwFile -Raw).Trim() } 20
    $initial = (Get-Content $pwFile -Raw).Trim()
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/auth/login" -Headers $h -WebSession $s -ContentType 'application/json' -Body (@{ username = 'admin'; password = $initial } | ConvertTo-Json)
    $null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/auth/change-password" -Headers $h -WebSession $s -ContentType 'application/json' -Body (@{ current = $initial; new = $adminPassword } | ConvertTo-Json)
    $s2 = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/auth/login" -Headers $h -WebSession $s2 -ContentType 'application/json' -Body (@{ username = 'admin'; password = $adminPassword } | ConvertTo-Json)
    return @{ Session = $s2; Log = $log }
}
function Publish-SessionFromUpload($web, $savePath, $sha) {
    $bytes = [IO.File]::ReadAllBytes($savePath)
    $b = Invoke-RestMethod -Method Post -Uri "$url/api/v1/saves/uploads" -Headers $h -WebSession $web -ContentType 'application/json' -Body (@{ fileName = 'authority_save.xml.gz'; size = $bytes.Length; sha256 = $sha } | ConvertTo-Json)
    $hdr = @{ 'X-X4MP' = '1'; 'Content-Range' = "bytes 0-$($bytes.Length - 1)/$($bytes.Length)" }
    $null = Invoke-RestMethod -Method Put -Uri "$url/api/v1/saves/uploads/$($b.uploadId)" -Headers $hdr -WebSession $web -ContentType 'application/octet-stream' -Body $bytes
    $null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/saves/uploads/$($b.uploadId)/complete" -Headers $h -WebSession $web -ContentType 'application/json' -Body '{}'
    $created = Invoke-RestMethod -Method Post -Uri "$url/api/v1/sessions" -Headers $h -WebSession $web -ContentType 'application/json' -Body (@{ name = 'From an upload'; saveId = $sha } | ConvertTo-Json)
    $null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/sessions/$($created.id)/start" -Headers $h -WebSession $web -ContentType 'application/json' -Body '{}'
}
function Run-Scenario($name, $workName, $vars) {
    $args2 = @('--dll', $dll, '--script', (Join-Path $PSScriptRoot "$name.hostsim"), '--work-dir', (Join-Path $tmp $workName),
        '--admin-url', $url, '--admin-user', 'admin', '--admin-password', $adminPassword,
        '--var', "tcp=$TcpPort", '--var', "apw=$nodeAdminPassword", '--var', "sim=$(Join-Path $PSScriptRoot 'authority_sim.ps1')",
        '--var', "fake=$fakeExe", '--var', "bots=$(Join-Path $PSScriptRoot 'avatars_sim.ps1')", '--var', "tcpport=$TcpPort", '--var', "tmp=$tmp", '--var', "admin_url=$url", '--var', "admin_pw=$adminPassword", '--max-seconds', '230') + $vars
    $out = Join-Path $tmp "$name.out.txt"
    $t0 = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-P $hostSim $args2 $out
    if (-not $p.WaitForExit(250000)) { & taskkill /PID $p.Id /T /F *> $null; throw "$name did not finish in 250 s" }
    $p.WaitForExit()
    Get-Content $out -Tail 8 | ForEach-Object { Write-Host "  $_" }
    if ($p.ExitCode -ne 0) { throw "$name failed (exit $($p.ExitCode)); output: $out; mod log: $(Join-Path $tmp "$workName\extension\logs\x4mp.log")" }
    Write-Host ("{0} PASSED in {1:N1} s" -f $name, $t0.Elapsed.TotalSeconds) -ForegroundColor Green
}
function Assert-SpawnGameTime($serverLog, $minimum) {
    $line = Select-String -Path $serverLog -Pattern 'EntitySpawn of \d+ entities, first net_id \d+, game_time ([0-9.,]+)' | Select-Object -First 1
    if (-not $line) { throw "criterion 11: no EntitySpawn line in $serverLog" }
    $gt = [double]($line.Matches[0].Groups[1].Value -replace ',', '.')
    if ($gt -lt $minimum) { throw "criterion 11: the self-spawn game_time $gt is below $minimum" }
    Write-Host "  criterion 11: server log EntitySpawn game_time = $gt (nonzero)" -ForegroundColor Green
}

try {
    $dummy = Join-Path $tmp 'start_save.xml.gz';   $sha = New-GzSave $dummy 7
    $ckpt1 = Join-Path $tmp 'ckpt1.xml.gz';        $null = New-GzSave $ckpt1 11
    $ckpt2 = Join-Path $tmp 'ckpt2.xml.gz';        $null = New-GzSave $ckpt2 12
    $srv = Start-FreshServer 'a'
    Publish-SessionFromUpload $srv.Session $dummy $sha
    Run-Scenario 'avatars' 'work-host' @('--var', "ckpt_src=$ckpt1", '--var', "ckpt_src2=$ckpt2")

    # ---- M3-25 (finding 16): the authority's game was closed and starts again while the SERVER keeps running and the observer Obs01 stays connected:
    # the world is rolled back to the checkpoint it loads (the old host ship goes), the ids continue at the checkpoint's next_net_id, one ghost per player ----
    $recordsA = Join-Path $tmp 'work-host\extension\avatar-records.txt'
    $workJ = Join-Path $tmp 'work-rejoin'
    New-Item -ItemType Directory -Force (Join-Path $workJ 'extension'), (Join-Path $workJ 'saves') | Out-Null
    Copy-Item $recordsA (Join-Path $workJ 'extension')
    Copy-Item (Join-Path $tmp 'work-host\saves\x4mp_ckpt_*.xml.gz') (Join-Path $workJ 'saves')
    Copy-Item (Join-Path $tmp 'work-host\extension\player.key') (Join-Path $workJ 'extension')
    $ckpt3 = Join-Path $tmp 'ckpt3.xml.gz'; $null = New-GzSave $ckpt3 13
    Run-Scenario 'avatars_rejoin' 'work-rejoin' @('--var', "ckpt_src3=$ckpt3")
    if (-not (Select-String -Path $srv.Log -Pattern 'world rolled back to net ids below 4 \(1 entities removed\)' -Quiet)) { throw "M3-25: the server log has no 'world rolled back to net ids below 4 (1 entities removed)' line ($($srv.Log))" }
    Write-Host '  M3-25: the rejoining authority found the world rolled back to its checkpoint: one host ship, two avatars, no stale ghost' -ForegroundColor Green

    $pidFile = Join-Path $tmp 'bots.pid'
    if (Test-Path $pidFile) { foreach ($l in Get-Content $pidFile) { try { & taskkill /PID ([int]$l) /T /F 2>$null | Out-Null } catch { } } ; Remove-Item $pidFile -Force }
    $obsPid = Join-Path $tmp 'obs.pid'
    if (Test-Path $obsPid) { foreach ($l in Get-Content $obsPid) { try { & taskkill /PID ([int]$l) /T /F 2>$null | Out-Null } catch { } } ; Remove-Item $obsPid -Force }
    Stop-All

    # ---- M3-22: the SERVER is restarted (same data dir: the session and its checkpoints persist, the process has a new ServerHello session GUID) and the
    # authority's game restarts: it is sent the newest checkpoint and the avatars that checkpoint listed are kept (the loaded save decides, not the session id) ----
    $records = Join-Path $tmp 'work-host\extension\avatar-records.txt'
    if (-not (Test-Path $records)) { throw "session A left no avatar-records.txt at $records" }
    if ((Get-Content $records -Raw) -notmatch '(?m)^A\|') { throw 'session A left no avatar records' }
    $workR = Join-Path $tmp 'work-restart'
    New-Item -ItemType Directory -Force (Join-Path $workR 'extension'), (Join-Path $workR 'saves') | Out-Null
    Copy-Item $records (Join-Path $workR 'extension')
    Copy-Item (Join-Path $tmp 'work-host\saves\x4mp_ckpt_*.xml.gz') (Join-Path $workR 'saves')
    Copy-Item (Join-Path $tmp 'work-host\extension\player.key') (Join-Path $workR 'extension')
    $dataA = Join-Path $tmp 'data-a'
    $envR = @{ X4MP__Net__NodeTcpEndpoint = "127.0.0.1:$TcpPort"; X4MP__Net__UdpPort = "$UdpPort"; X4MP__Net__ModBuildStrict = 'false'
        X4MP__Net__MaxPlayers = '8'; X4MP__Net__MaxConnectionsPerIp = '64'; X4MP__Net__AdminPassword = $nodeAdminPassword }
    $script:server = Start-P $serverExe @('--data-dir', $dataA, '--port', "$HttpPort") (Join-Path $tmp 'server-a2.out.txt') $envR
    Wait-Until 'the restarted server' { (Invoke-WebRequest -UseBasicParsing "$url/healthz" -TimeoutSec 2).StatusCode -eq 200 }
    # the admin starts the session again from the newest stored checkpoint (the checkpoint files live in the server's store)
    $webR = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/auth/login" -Headers $h -WebSession $webR -ContentType 'application/json' -Body (@{ username = 'admin'; password = $adminPassword } | ConvertTo-Json)
    $shaCk2 = Get-Sha256Hex $ckpt2
    $createdR = Invoke-RestMethod -Method Post -Uri "$url/api/v1/sessions" -Headers $h -WebSession $webR -ContentType 'application/json' -Body (@{ name = 'Restarted from the checkpoint'; saveId = $shaCk2 } | ConvertTo-Json)
    $null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/sessions/$($createdR.id)/start" -Headers $h -WebSession $webR -ContentType 'application/json' -Body '{}'
    Run-Scenario 'avatars_restart' 'work-restart' @()
    Write-Host '  M3-22: after a server restart the loaded checkpoint kept the avatars its manifest listed' -ForegroundColor Green
    Stop-All

    # ---- M3-22: a NEW session (new server = new session id) from a plain save, on the authority that still has session A's avatar-records.txt ----
    $work2 = Join-Path $tmp 'work-host2'
    New-Item -ItemType Directory -Force (Join-Path $work2 'extension') | Out-Null
    Copy-Item $records (Join-Path $work2 'extension')
    $plainB = Join-Path $tmp 'start_save_b.xml.gz'; $shaB = New-GzSave $plainB 9
    $srvB = Start-FreshServer 'b'
    Publish-SessionFromUpload $srvB.Session $plainB $shaB
    Run-Scenario 'avatars_session2' 'work-host2' @('--var', "ckpt_src=$ckpt1")
    Write-Host '  M3-22: the second session started with no avatars and provisioned only on a PlayerShip request' -ForegroundColor Green
    $exit = 0
}
catch { Write-Host "AVATARS E2E FAILED: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    foreach ($f in 'bots.pid', 'obs.pid') {
        $pidFile = Join-Path $tmp $f
        if (Test-Path $pidFile) { foreach ($l in Get-Content $pidFile) { try { & taskkill /PID ([int]$l) /T /F 2>$null | Out-Null } catch { } } }
    }
    Stop-All
    Write-Host ("Avatars e2e {0} in {1:N0} s. Temp tree: {2}" -f $(if ($exit -eq 0) { 'PASSED' } else { 'FAILED' }), $sw.Elapsed.TotalSeconds, $tmp)
}
exit $exit
