<#
.SYNOPSIS
  M2-12 end-to-end (NOT part of CI): the real x4mp.dll in x4mp-hostsim reads a launch.json, connects to the published server (whose authority
  is a FakeNode serving a dummy save) without any UI, deletes the file, and its NodeStats show up in the admin API (dashboard).
  The expired-file scenario needs no server and runs in ctest (hostsim.launch_expired); it is repeated here against the live server to
  prove that no connection starts.
.DESCRIPTION
  Needs: mod\build.ps1 (x4mp.dll, x4mp-hostsim.exe) and the published server + FakeNode (tools\e2e.ps1 -Steps Publish).
  Ports 47971 (TCP), 47972 (UDP), 47973 (HTTP). Everything lives in a temp folder. About 1 minute.
  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\launch_stats_run.ps1
#>
[CmdletBinding()]
param([string]$Config = 'relwithdebinfo', [int]$TcpPort = 47971, [int]$UdpPort = 47972, [int]$HttpPort = 47973)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$build = Join-Path $repo "mod\build\msvc-x64-$Config"
$hostSim = Join-Path $build 'x4mp-hostsim.exe'
$dll = Join-Path $build 'x4mp.dll'
$serverExe = Join-Path $repo 'out\win-x64\x4mp-server.exe'
$fakeExe = Join-Path $repo 'out\fakenode\X4MP.FakeNode.exe'
foreach ($f in $hostSim, $dll, $serverExe, $fakeExe) { if (-not (Test-Path $f)) { throw "Missing $f (build the mod, then tools\e2e.ps1 -Steps Publish)" } }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-launch-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
$adminPassword = 'Launch-e2e-only-password-12345'
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
        '--var', "tcp=$TcpPort", '--max-seconds', '120') + $vars
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
    # dummy save: a real gzip of a small XML plus 1 MB of incompressible data
    $dummy = Join-Path $tmp 'authority_save.xml.gz'
    $rnd = New-Object byte[] (1MB); (New-Object Random 7).NextBytes($rnd)
    $fs = [IO.File]::Create($dummy); $gz = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Compress)
    $head = [Text.Encoding]::UTF8.GetBytes('<?xml version="1.0" encoding="utf-8"?><savegame><info><game id="x4mp-launch-e2e"/></info></savegame>')
    $gz.Write($head, 0, $head.Length); $gz.Write($rnd, 0, $rnd.Length); $gz.Dispose(); $fs.Dispose()

    $data = Join-Path $tmp 'data'; New-Item -ItemType Directory -Force $data | Out-Null
    $envVars = @{ X4MP__Net__NodeTcpEndpoint = "127.0.0.1:$TcpPort"; X4MP__Net__UdpPort = "$UdpPort"; X4MP__Net__ModBuildStrict = 'false'
        X4MP__Net__MaxPlayers = '4'; X4MP__Net__MaxConnectionsPerIp = '64' }
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

    # FakeNode authority offering the dummy save
    $fakeLog = Join-Path $tmp 'fakenode.out.txt'
    $null = Start-P $fakeExe @('authority', '--server', "127.0.0.1:$TcpPort", '--name', 'FakeAuthority', '--save-file', $dummy) $fakeLog
    Wait-Until 'the authority checkpoint' { (Test-Path $fakeLog) -and (Select-String -Path $fakeLog -Pattern 'checkpoint stored' -Quiet) } 90

    # 1. a valid launch.json (expires in 10 minutes) connects without UI; NodeStats reach the admin API
    $expires = [DateTime]::UtcNow.AddMinutes(10).ToString('yyyy-MM-ddTHH:mm:ssZ')
    Run-Scenario 'launch_connect' 'work-launch' @('--var', "expires=$expires")

    # 2. the same file past its expiry: ignored and deleted, no player joins
    Run-Scenario 'launch_expired' 'work-expired' @()
    $players = Invoke-RestMethod -Uri "$url/api/v1/players" -Headers $h -WebSession $s
    $list = if ($players -is [array]) { $players } else { $players.items }
    if (@($list | Where-Object { $_.name -eq 'Late' }).Count -ne 0) { throw 'the expired launch request still produced a player' }
    $exit = 0
}
catch { Write-Host "LAUNCH E2E FAILED: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    Stop-All
    Write-Host ("Launch e2e {0} in {1:N0} s. Temp tree: {2}" -f $(if ($exit -eq 0) { 'PASSED' } else { 'FAILED' }), $sw.Elapsed.TotalSeconds, $tmp)
}
exit $exit
