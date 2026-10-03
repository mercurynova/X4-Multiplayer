<#
.SYNOPSIS
  Session-2 kit dry run without X4 (NOT part of CI): the real x4mp_probe.dll in x4mp-hostsim against the published server and
  a FakeNode authority started by tools\session2\start-server.ps1, with the probe config written by write-probe-config.ps1 /
  run-block.ps1. Everything lives in a temp folder; the real Documents folder and the X4 install are never touched.
.DESCRIPTION
  Needs: mod\build.ps1 -Spikes (x4mp_probe.dll + x4mp-hostsim.exe) and tools\e2e.ps1 -Steps Publish (server + FakeNode).
  Ports 47953 (TCP), 47954 (UDP), 47955 (HTTP); never the defaults 47780/47781/47790.
  What it proves: the kit scripts write a config the probe parses (hooks, skip_autosave, spike_block, reloadui_after_s,
  pin_module), the probe connects, downloads the dummy save into the fake save folder with the right SHA-256, logs its
  lifecycle (init build line, Welcome, resume after a reload, pause/unpause, money test, autosave skip, pin), and the
  server logs the join and the resume.
  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\probe_session2_dry_run.ps1
  Exit 0 = pass. Takes about 1.5 minutes.
#>
[CmdletBinding()]
param(
    [string]$Config = 'relwithdebinfo',
    [int]$TcpPort = 47953,
    [int]$UdpPort = 47954,
    [int]$HttpPort = 47955
)
$ErrorActionPreference = 'Stop'
function Get-Sha256Hex([string]$Path) { $s = [IO.File]::OpenRead($Path); try { $h = [Security.Cryptography.SHA256]::Create(); try { return ([BitConverter]::ToString($h.ComputeHash($s)) -replace '-', '').ToLowerInvariant() } finally { $h.Dispose() } } finally { $s.Dispose() } }  # not Get-FileHash: a 5.1 child of pwsh 7 cannot autoload Microsoft.PowerShell.Utility
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$s2 = Join-Path $repo 'tools\session2'
$build = Join-Path $repo "mod\build\msvc-x64-$Config"
$hostSim = Join-Path $build 'x4mp-hostsim.exe'
$probeDll = Join-Path $build 'x4mp_probe.dll'
foreach ($f in $hostSim, $probeDll, (Join-Path $repo 'out\win-x64\x4mp-server.exe'), (Join-Path $repo 'out\fakenode\X4MP.FakeNode.exe')) {
    if (-not (Test-Path $f)) { throw "Missing $f (run mod\build.ps1 -Spikes and tools\e2e.ps1 -Steps Publish first)" }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-s2-dry-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$docs = Join-Path $tmp 'docs'
$userDir = Join-Path $docs 'Egosoft\X4\12345'
$saveDir = Join-Path $userDir 'save'
$cfgDir = Join-Path $docs 'Egosoft\X4\x4mp'
$outDir = Join-Path $tmp 'out'
$work = Join-Path $tmp 'hostsim-work'
New-Item -ItemType Directory -Force $saveDir, $outDir | Out-Null
Write-Host "Temp tree: $tmp"

# Dummy save: a real gzip of a small XML plus 5 MB of incompressible data, so the download takes a few chunks.
$dummy = Join-Path $saveDir 'save_001.xml.gz'
$rnd = New-Object byte[] (5MB); (New-Object Random 42).NextBytes($rnd)
$fs = [IO.File]::Create($dummy)
$gz = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Compress)
$head = [Text.Encoding]::UTF8.GetBytes('<?xml version="1.0" encoding="utf-8"?><savegame><info><game id="x4mp-dry-run"/></info></savegame>')
$gz.Write($head, 0, $head.Length); $gz.Write($rnd, 0, $rnd.Length); $gz.Dispose(); $fs.Dispose()
$dummySha = Get-Sha256Hex $dummy
Write-Host ("Dummy save: {0:N1} MB sha256 {1}" -f ((Get-Item $dummy).Length / 1MB), $dummySha)

# The kit scripts only look at these two variables (test-only overrides, see tools\session2\common.ps1).
$env:X4MP_S2_DOCS_ROOT = $docs
$env:X4MP_S2_OUT_DIR = $outDir
$server = $null
$exit = 1
$sw = [Diagnostics.Stopwatch]::StartNew()
try {
    # 1. Probe config exactly as the user would write it for the B4 run (hooks off), plus a short pause for the test.
    & (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -File (Join-Path $s2 'write-probe-config.ps1') -Server "127.0.0.1:$TcpPort" -Name Tester -NoHooks
    if ($LASTEXITCODE -ne 0) { throw 'write-probe-config.ps1 failed' }
    $cfgFile = Join-Path $cfgDir 'x4mp_probe.json'
    $j = Get-Content $cfgFile -Raw | ConvertFrom-Json
    $j | Add-Member -NotePropertyName pause_seconds -NotePropertyValue 3 -Force
    ($j | ConvertTo-Json) | Set-Content $cfgFile -Encoding UTF8

    # 2. start-server.ps1 (real script, test ports) in its own window-less process.
    $server = Start-Process (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$(Join-Path $s2 'start-server.ps1')`"",
        '-SaveName', 'save_001', '-TcpPort', $TcpPort, '-UdpPort', $UdpPort, '-HttpPort', $HttpPort) -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $tmp 'start-server.out.txt') -RedirectStandardError (Join-Path $tmp 'start-server.err.txt')
    $fakeLog = Join-Path $outDir 'fakenode.log'
    $deadline = (Get-Date).AddSeconds(90)
    while ($true) {
        if ($server.HasExited) { throw "start-server.ps1 exited early ($($server.ExitCode)): $(Get-Content (Join-Path $tmp 'start-server.err.txt') -Raw)" }
        if ((Test-Path $fakeLog) -and (Select-String -Path $fakeLog -Pattern 'checkpoint stored' -Quiet)) { break }
        if ((Get-Date) -gt $deadline) { throw 'no "FakeNode authority: checkpoint stored" within 90 s' }
        Wait-Process -Id $server.Id -Timeout 1 -ErrorAction SilentlyContinue
    }
    Write-Host ((Select-String -Path $fakeLog -Pattern 'checkpoint stored').Line)

    # 3. The probe, in hostsim.
    $hsArgs = @('--dll', $probeDll, '--script', (Join-Path $PSScriptRoot 'probe_session2.hostsim'), '--work-dir', $work, '--ext-id', 'x4mp_probe',
        '--pre-init-wexport', "x4mp_probe_set_config_dir=$cfgDir", '--var', "s2=$s2", '--var', "tcp=$TcpPort", '--max-seconds', '150')
    $hsOut = Join-Path $tmp 'hostsim.out.txt'
    $p = Start-Process $hostSim -ArgumentList ($hsArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -PassThru -NoNewWindow -RedirectStandardOutput $hsOut
    $null = $p.Handle   # keeps the process handle so ExitCode is readable after WaitForExit
    if (-not $p.WaitForExit(170000)) { Stop-Process -Id $p.Id -Force; throw 'hostsim did not finish within 170 s' }
    $p.WaitForExit()
    Get-Content $hsOut -Tail 12 | ForEach-Object { Write-Host "  $_" }
    if ($p.ExitCode -ne 0) { throw "hostsim exit $($p.ExitCode)" }

    # 4. What the server and the file system saw.
    $dl = Get-ChildItem (Join-Path $work 'saves') -Filter 'x4mp_*.xml.gz' | Select-Object -First 1
    if (-not $dl) { throw 'no x4mp_*.xml.gz in the fake save folder' }
    $dlSha = Get-Sha256Hex $dl.FullName
    if ($dlSha -ne $dummySha) { throw "downloaded save differs ($dlSha)" }
    Write-Host "Downloaded save $($dl.Name): same sha256 as the source."
    $serverLog = Get-ChildItem (Join-Path $outDir 'data\logs') -Filter 'server-*.log' | Select-Object -First 1
    $fsr = [IO.File]::Open($serverLog.FullName, "Open", "Read", "ReadWrite"); $sr = New-Object IO.StreamReader($fsr); $text = ($sr.ReadToEnd()) -replace "`r", ""; $sr.Dispose()  # the server still holds the file open
    foreach ($pat in '\(Tester\) joined as', '\(Tester\) resumed \(baseline epoch') {
        if ($text -notmatch $pat) { throw "server log lacks /$pat/" }
    }
    Write-Host 'Server log: Tester joined, then resumed (no left/joined pair for the reload).'
    if ($text -match '\(Tester\) left') { Write-Host 'Server log also has a "left" line for Tester (expected only at the end).' }
    Write-Host ((($text -split "`n") | Where-Object { $_ -match "(Tester)" } | ForEach-Object { "  " + $_ }) -join "`n")

    # 5. collect-logs.ps1 over the temp tree: first -WhatIf, then for real (the zip lands in the temp out folder).
    New-Item -ItemType Directory -Force (Join-Path $userDir 'x4native') | Out-Null
    Copy-Item $hsOut (Join-Path $userDir 'x4mp_s2.log')
    Copy-Item $hsOut (Join-Path $userDir 'x4native\x4mp_probe.log')
    & (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -File (Join-Path $s2 'collect-logs.ps1') -Label dry -WhatIf | Out-Host
    & (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -File (Join-Path $s2 'collect-logs.ps1') -Label dry | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'collect-logs.ps1 failed' }
    $zip = Get-ChildItem $outDir -Filter 'logs-dry-*.zip' | Select-Object -First 1
    if (-not $zip) { throw 'no logs-dry-*.zip' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
    try { $names = @($z.Entries | ForEach-Object { $_.FullName }) } finally { $z.Dispose() }
    Write-Host ('Zip entries: ' + ($names -join ', '))
    foreach ($want in 'x4mp_s2.log', 'fakenode.log') { if ($names -notcontains $want) { throw "zip lacks $want" } }
    if (-not ($names -like 'x4native*x4mp_probe.log')) { throw 'zip lacks x4native\x4mp_probe.log' }
    if (-not ($names -like 'server-logs*server-*.log')) { throw 'zip lacks the server log' }
    if ($names -like '*initial-admin-password*') { throw 'zip contains the admin password file' }
    $exit = 0
}
catch { Write-Host "DRY RUN FAILED: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    if ($server -and -not $server.HasExited) { & taskkill /PID $server.Id /T /F | Out-Null }
    Remove-Item Env:\X4MP_S2_DOCS_ROOT, Env:\X4MP_S2_OUT_DIR -ErrorAction SilentlyContinue
    Write-Host ("Dry run {0} in {1:N0} s. Temp tree kept for inspection: {2}" -f $(if ($exit -eq 0) { 'PASSED' } else { 'FAILED' }), $sw.Elapsed.TotalSeconds, $tmp)
}
exit $exit
