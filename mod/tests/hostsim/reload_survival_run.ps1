<#
.SYNOPSIS
  M2-07 end-to-end (NOT part of CI): 20 extension reloads at random phases against the published server + a FakeNode authority.
.DESCRIPTION
  The real x4mp.dll in x4mp-hostsim joins, then is unloaded and re-initialised 20 times (stash kept, like X4Native does for a save load
  and for /reloadui). A seeded generator writes the hostsim script: early reloads (a random number of frames after the join: handshake,
  download, preparing), reloads while loading (after loadSave), and in-game reloads in three styles:
    ui-replay   reload, then on_game_loaded + on_universe_ready at the same game clock   (/reloadui, events replayed)
    ui-silent   reload only                                                             (/reloadui, no events)
    save-load   reload, clock set far away, then on_game_loaded + on_universe_ready     (a save load)
  Checks: every Welcome after the first is resumed=true with the same player_id, the server log has no "left", the epoch rule logged
  "same universe" for every ui-replay and "new universe" for every save-load, join_ms of every unload is logged and within budget.
  Needs: mod\build.ps1 and tools\e2e.ps1 -Steps Publish. Ports 47953-47955. Run:
    powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\reload_survival_run.ps1 [-Seed 207]
#>
[CmdletBinding()]
param([string]$Config = 'relwithdebinfo', [int]$TcpPort = 47953, [int]$UdpPort = 47954, [int]$HttpPort = 47955, [int]$Seed = 207, [int]$Reloads = 20)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$build = Join-Path $repo "mod\build\msvc-x64-$Config"
$hostSim = Join-Path $build 'x4mp-hostsim.exe'
$dll = Join-Path $build 'x4mp.dll'
$serverExe = Join-Path $repo 'out\win-x64\x4mp-server.exe'
$fakeExe = Join-Path $repo 'out\fakenode\X4MP.FakeNode.exe'
foreach ($f in $hostSim, $dll, $serverExe, $fakeExe) { if (-not (Test-Path $f)) { throw "Missing $f (build the mod, then tools\e2e.ps1 -Steps Publish)" } }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-reload-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
$adminPassword = 'Reload-e2e-only-password-12345'
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

try {
    $dummy = Join-Path $tmp 'authority_save.xml.gz'
    $rnd = New-Object byte[] (3MB); (New-Object Random 7).NextBytes($rnd)
    $fs = [IO.File]::Create($dummy); $gz = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Compress)
    $head = [Text.Encoding]::UTF8.GetBytes('<?xml version="1.0" encoding="utf-8"?><savegame><info><game id="x4mp-reload-e2e"/></info></savegame>')
    $gz.Write($head, 0, $head.Length); $gz.Write($rnd, 0, $rnd.Length); $gz.Dispose(); $fs.Dispose()

    $data = Join-Path $tmp 'data'; New-Item -ItemType Directory -Force $data | Out-Null
    $envVars = @{ X4MP__Net__NodeTcpEndpoint = "127.0.0.1:$TcpPort"; X4MP__Net__UdpPort = "$UdpPort"; X4MP__Net__ModBuildStrict = 'false'
        X4MP__Net__MaxPlayers = '2'; X4MP__Net__MaxConnectionsPerIp = '64' }
    $server = Start-P $serverExe @('--data-dir', $data, '--port', "$HttpPort") (Join-Path $tmp 'server.out.txt') $envVars
    $url = "http://127.0.0.1:$HttpPort"
    Wait-Until 'the server' { if ($server.HasExited) { throw 'server exited' }; (Invoke-WebRequest -UseBasicParsing "$url/healthz" -TimeoutSec 2).StatusCode -eq 200 }
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
    $fakeLog = Join-Path $tmp 'fakenode.out.txt'
    $null = Start-P $fakeExe @('authority', '--server', "127.0.0.1:$TcpPort", '--name', 'FakeAuthority', '--save-file', $dummy) $fakeLog
    Wait-Until 'the authority checkpoint' { (Test-Path $fakeLog) -and (Select-String -Path $fakeLog -Pattern 'checkpoint stored' -Quiet) } 90

    # ---- generate the scenario (seeded) ----
    $rng = New-Object Random $Seed
    $lines = New-Object System.Collections.Generic.List[string]
    $plan = New-Object System.Collections.Generic.List[string]
    $lines.Add('init'); $lines.Add('frame 60Hz 10')
    $lines.Add('lua-raw x4mp.ui_ready {"v":1,"startmenu":true}')
    $lines.Add('lua-raw x4mp.join {"v":1,"address":"127.0.0.1:${tcp}","name":"Alice","password":"","team":"auto"}')
    $online = 'expect-admin /api/v1/players $[name==Alice].online == true'
    $nEarly = 4; $nLoading = 3; $nInGame = $Reloads - $nEarly - $nLoading
    $sameCount = 0; $newCount = 0; $clock = 100000.0
    for ($i = 0; $i -lt $nEarly; $i++) {   # before loadSave: handshake / download / preparing, wherever the frame count lands
        $n = $rng.Next(1, 14)
        $plan.Add("early(frames=$n)")
        $lines.Add("frame 60Hz $n"); $lines.Add($online); $lines.Add('reload')
    }
    $lines.Add('frame 60Hz 120')
    for ($i = 0; $i -lt $nLoading; $i++) {  # loadSave raised, universe not ready
        $plan.Add('loading')
        if ($i -eq 0) { $lines.Add('expect-lua x4mp.load_save 8000 contains x4mp_') } else { $lines.Add('expect-lua x4mp.status 8000 json $.state == loading') }  # a resumed Loading node does not raise loadSave again
        $lines.Add($online); $lines.Add('reload'); $lines.Add('frame 60Hz 15')
    }
    $lines.Add('set game_time 5000'); $lines.Add('load_save x4mp_reload_e2e'); $lines.Add('frame 60Hz 30')
    $lines.Add('expect-lua x4mp.status 8000 json $.state == ingame')
    for ($i = 0; $i -lt $nInGame; $i++) {
        $kind = $rng.Next(0, 3)
        $f = $rng.Next(65, 140)   # long enough for the once-a-second fingerprint sample
        $lines.Add("frame 60Hz $f"); $lines.Add($online); $lines.Add('reload')
        switch ($kind) {
            0 { $plan.Add('ui-replay'); $sameCount++; $lines.Add('frame 60Hz 5'); $lines.Add('load_save x4mp_reload_e2e') }
            1 { $plan.Add('ui-silent'); $lines.Add('frame 60Hz 8') }
            2 { $plan.Add('save-load'); $newCount++; $clock += $rng.Next(100, 5000); $lines.Add("set game_time $clock"); $lines.Add('frame 60Hz 5'); $lines.Add('load_save x4mp_reload_e2e') }
        }
        $lines.Add('frame 60Hz 20')
        $lines.Add('expect-lua x4mp.status 8000 json $.state == ingame')
        $lines.Add($online)
    }
    $lines.Add('lua-raw x4mp.disconnect {"v":1}'); $lines.Add('frame 60Hz 30'); $lines.Add('shutdown')
    $scriptFile = Join-Path $tmp 'reload_survival.hostsim'
    Set-Content -Path $scriptFile -Value $lines -Encoding ascii
    Write-Host "seed=$Seed plan: $($plan -join ', ')"

    $work = Join-Path $tmp 'work'
    $out = Join-Path $tmp 'hostsim.out.txt'
    $args2 = @('--dll', $dll, '--script', $scriptFile, '--work-dir', $work, '--admin-url', $url, '--admin-user', 'admin', '--admin-password', $adminPassword,
        '--var', "tcp=$TcpPort", '--max-seconds', '400')
    $t0 = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-P $hostSim $args2 $out
    if (-not $p.WaitForExit(420000)) { & taskkill /PID $p.Id /T /F *> $null; throw 'hostsim did not finish in 420 s' }
    $p.WaitForExit()
    Get-Content $out -Tail 8 | ForEach-Object { Write-Host "  $_" }
    if ($p.ExitCode -ne 0) { throw "hostsim failed (exit $($p.ExitCode)); output: $out" }
    Write-Host ('hostsim run finished in {0:N1} s' -f $t0.Elapsed.TotalSeconds)

    # ---- verdicts from the mod log and the server log ----
    $log = Get-Content (Join-Path $work 'extension\logs\x4mp.log')
    $welcomes = @($log | Where-Object { $_ -match 'Welcome: player_id=(\d+) .* resumed=(\w+)' } | ForEach-Object { [pscustomobject]@{ id = $Matches[1]; resumed = $Matches[2] } })
    $fresh = @($welcomes | Where-Object { $_.resumed -ne 'true' })
    $ids = @($welcomes | Select-Object -ExpandProperty id -Unique)
    $joinMs = @($log | Where-Object { $_ -match 'join_ms=(\d+)' } | ForEach-Object { [int]$Matches[1] })
    $same = @($log | Where-Object { $_ -match 'reload resume: same universe' }).Count
    $new = @($log | Where-Object { $_ -match 'reload resume: new universe' }).Count
    $refused = @($log | Where-Object { $_ -match 'did not resume the slot|over the .* ms shutdown budget' }).Count
    # the server's rolling log file: the final ClientQuit leave (the script's own disconnect) is expected, any other leave is a failure
    $logFile = Get-ChildItem (Join-Path $data 'logs') -Filter 'server-*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
    $fsl = [IO.File]::Open($logFile.FullName, 'Open', 'Read', 'ReadWrite'); $rd = New-Object IO.StreamReader $fsl
    $serverLines = $rd.ReadToEnd() -split "`r?`n"; $rd.Dispose(); $fsl.Dispose()
    $left = @($serverLines | Where-Object { $_ -match 'Alice\) left' -and $_ -notmatch 'ClientQuit' })
    $resumedSrv = @($serverLines | Where-Object { $_ -match 'Alice\) resumed \(baseline' }).Count
    Write-Host ("welcomes={0} fresh={1} distinct_player_ids={2} unloads(join_ms)={3} max_join_ms={4} same_universe={5}/{6} new_universe={7}/{8} budget_or_refused_warnings={9} server_resumed={10} server_left={11}" -f `
        $welcomes.Count, $fresh.Count, $ids.Count, $joinMs.Count, ($joinMs | Measure-Object -Maximum).Maximum, $same, $sameCount, $new, $newCount, $refused, $resumedSrv, $left.Count)
    $problems = @()
    if ($fresh.Count -ne 1) { $problems += "expected exactly 1 non-resumed Welcome (the first join), got $($fresh.Count)" }
    if ($welcomes.Count -lt $Reloads + 1) { $problems += "expected at least $($Reloads + 1) Welcomes, got $($welcomes.Count)" }
    if ($ids.Count -ne 1) { $problems += "player id changed: $($ids -join ',')" }
    if ($joinMs.Count -lt $Reloads) { $problems += "join_ms logged $($joinMs.Count) times, expected >= $Reloads" }
    if ($same -ne $sameCount) { $problems += "same-universe verdicts $same, expected $sameCount" }
    if ($new -ne $newCount) { $problems += "new-universe verdicts $new, expected $newCount" }
    if ($refused -ne 0) { $problems += "$refused refused-resume / over-budget warnings" }
    if ($resumedSrv -lt $Reloads) { $problems += "the server logged $resumedSrv resumes, expected >= $Reloads" }
    if ($left.Count -ne 0) { $problems += "the server logged $($left.Count) 'left' line(s) for Alice" }
    if ($problems.Count -gt 0) { throw ($problems -join '; ') }
    $exit = 0
}
catch { Write-Host "RELOAD E2E FAILED: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    Stop-All
    Write-Host ("Reload e2e {0} in {1:N0} s. Temp tree: {2}" -f $(if ($exit -eq 0) { 'PASSED' } else { 'FAILED' }), $sw.Elapsed.TotalSeconds, $tmp)
}
exit $exit
