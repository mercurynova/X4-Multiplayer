<#
.SYNOPSIS
  M2-X3 end-to-end (NOT part of CI): the real x4mp.dll in x4mp-hostsim is refused by the published server because its mods differ from the
  FakeNode authority's in all four ways; the grouped lists must arrive in Lua as x4mp.mod_refusal.
.DESCRIPTION
  Server + FakeNode authority (mod_refusal_authority.json: two DLC, a Workshop mod, a Nexus-only mod, two more Workshop mods) + an admin
  entry that gives the Nexus-only mod a Nexus URL, then mod_refusal.hostsim (install 2 / enable 1 / disable 1 / update 1).
  Needs: mod\build.ps1 (x4mp.dll, x4mp-hostsim.exe) and tools\e2e.ps1 -Steps Publish (server + FakeNode).
  Ports 47968 (TCP), 47969 (UDP), 47970 (HTTP). Everything lives in a temp folder. About 1 minute.
  No sleeping: waits block on the child processes' own output (Get-Content -Wait in a job with a timeout).
  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\mod_refusal_run.ps1
#>
[CmdletBinding()]
param([string]$Config = 'relwithdebinfo', [int]$TcpPort = 47968, [int]$UdpPort = 47969, [int]$HttpPort = 47970)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$build = Join-Path $repo "mod\build\msvc-x64-$Config"
$hostSim = Join-Path $build 'x4mp-hostsim.exe'
$dll = Join-Path $build 'x4mp.dll'
$serverExe = Join-Path $repo 'out\win-x64\x4mp-server.exe'
$fakeExe = Join-Path $repo 'out\fakenode\X4MP.FakeNode.exe'
foreach ($f in $hostSim, $dll, $serverExe, $fakeExe) { if (-not (Test-Path $f)) { throw "Missing $f (build the mod, then tools\e2e.ps1 -Steps Publish)" } }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-modref-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
$adminPassword = 'ModRefusal-e2e-only-password-12345'
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
# Blocks until a line of $file matches $pattern (a job runs Get-Content -Wait), or throws after $sec seconds. No polling loop of ours.
function Wait-Line($what, $file, $pattern, $sec = 60) {
    $job = Start-Job -ArgumentList $file, $pattern -ScriptBlock {
        param($f, $p)
        Get-Content -LiteralPath $f -Wait | Where-Object { $_ -match $p } | Select-Object -First 1
    }
    try {
        if (-not (Wait-Job $job -Timeout $sec)) { throw "Timed out waiting for $what" }
        $null = Receive-Job $job
    } finally { Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue }
}

try {
    $dummy = Join-Path $tmp 'authority_save.xml.gz'
    $rnd = New-Object byte[] (1MB); (New-Object Random 11).NextBytes($rnd)
    $fs = [IO.File]::Create($dummy); $gz = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Compress)
    $head = [Text.Encoding]::UTF8.GetBytes('<?xml version="1.0" encoding="utf-8"?><savegame><info><game id="x4mp-modref-e2e"/></info></savegame>')
    $gz.Write($head, 0, $head.Length); $gz.Write($rnd, 0, $rnd.Length); $gz.Dispose(); $fs.Dispose()

    $data = Join-Path $tmp 'data'; New-Item -ItemType Directory -Force $data | Out-Null
    $envVars = @{ X4MP__Net__NodeTcpEndpoint = "127.0.0.1:$TcpPort"; X4MP__Net__UdpPort = "$UdpPort"; X4MP__Net__ModBuildStrict = 'false'
        X4MP__Net__MaxPlayers = '4'; X4MP__Net__MaxConnectionsPerIp = '64' }
    $serverOut = Join-Path $tmp 'server.out.txt'
    $server = Start-P $serverExe @('--data-dir', $data, '--port', "$HttpPort") $serverOut $envVars
    Wait-Line 'the server to listen' $serverOut 'Now listening on' 60
    $url = "http://127.0.0.1:$HttpPort"

    # bootstrap admin (forced password change), as tools\e2e.ps1 does
    $pwFile = Join-Path $data 'initial-admin-password.txt'
    if (-not (Test-Path $pwFile)) { throw 'the server did not write initial-admin-password.txt' }
    $initial = (Get-Content $pwFile -Raw).Trim()
    $h = @{ 'X-X4MP' = '1' }
    $send = { param($method, $path, $body, $session) Invoke-RestMethod -Method $method -Uri "$url$path" -Headers $h -WebSession $session -ContentType 'application/json' -Body ($body | ConvertTo-Json) }
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $null = & $send Post '/api/v1/auth/login' @{ username = 'admin'; password = $initial } $s
    $null = & $send Post '/api/v1/auth/change-password' @{ current = $initial; new = $adminPassword } $s
    $s2 = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $null = & $send Post '/api/v1/auth/login' @{ username = 'admin'; password = $adminPassword } $s2

    # an admin entry gives the Nexus-only mod its Nexus URL (the entry wins over the authority-defined rule)
    $null = & $send Put '/api/v1/mods/entries/sn_better_traders' @{ name = 'Better Traders'; rule = 'Required'; enabled = $true; versionRule = 'Exact'; version = '2.0'
        nexusUrl = 'https://www.nexusmods.com/x4foundations/mods/1234'; notes = '' } $s2

    # FakeNode authority with the fixture extension list
    $fakeLog = Join-Path $tmp 'fakenode.out.txt'
    $null = Start-P $fakeExe @('authority', '--server', "127.0.0.1:$TcpPort", '--name', 'FakeAuthority', '--save-file', $dummy,
        '--extensions', (Join-Path $PSScriptRoot 'mod_refusal_authority.json')) $fakeLog
    Wait-Line 'the authority checkpoint' $fakeLog 'checkpoint stored' 90

    $out = Join-Path $tmp 'mod_refusal.out.txt'
    $t0 = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-P $hostSim @('--dll', $dll, '--script', (Join-Path $PSScriptRoot 'mod_refusal.hostsim'), '--work-dir', (Join-Path $tmp 'work-modder'),
        '--var', "tcp=$TcpPort", '--max-seconds', '60') $out
    if (-not $p.WaitForExit(80000)) { & taskkill /PID $p.Id /T /F *> $null; throw 'mod_refusal did not finish in 80 s' }
    $p.WaitForExit()
    Get-Content $out -Tail 8 | ForEach-Object { Write-Host "  $_" }
    if ($p.ExitCode -ne 0) { throw "mod_refusal failed (exit $($p.ExitCode)); output: $out" }
    Write-Host ("mod_refusal PASSED in {0:N1} s" -f $t0.Elapsed.TotalSeconds) -ForegroundColor Green
    $exit = 0
}
catch { Write-Host "MOD REFUSAL E2E FAILED: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    Stop-All
    Write-Host ("Mod refusal e2e {0} in {1:N0} s. Temp tree: {2}" -f $(if ($exit -eq 0) { 'PASSED' } else { 'FAILED' }), $sw.Elapsed.TotalSeconds, $tmp)
}
exit $exit
