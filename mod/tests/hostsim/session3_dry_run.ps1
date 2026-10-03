<#
.SYNOPSIS
  Session-3 kit dry run without X4 (NOT part of CI): runs every tools\session3 script against a temp "Documents" folder, a temp fake X4 install,
  non-default ports, and the real x4mp.dll in x4mp-hostsim. Everything it starts is stopped at the end.
.DESCRIPTION
  Needs: mod\build.ps1 (x4mp.dll, x4mp-hostsim.exe) and tools\e2e.ps1 -Steps Publish (server + FakeNode; this script builds the web GUI too
  when server\web\dist is missing, through the kit's own Ensure-Published).
  Ports 47974 (TCP), 47975 (UDP), 47976 (HTTP). The kit scripts honour the test-only environment variables X4MP_S2_DOCS_ROOT (stand-in for
  Documents) and X4MP_S3_OUT_DIR (stand-in for out\session3); the fake X4 folder is passed with -X4Dir. The real Documents folder and the
  real X4 install are never touched.
  What it proves (parts): -WhatIf of every script; install.ps1 refuses while x4mp_probe / x4mp_spike exist, removes them with
  -RemoveTestExtensions -Force and deploys x4mp + x4native; topology 1 (start-fake-authority.ps1 with a join password, the real DLL joins and
  reloads, selftest from the written x4mp.json, a server restart with the same command, reconnect); find-password.ps1 NO HIT / planted HIT;
  collect-logs.ps1 contents; write-launch.ps1 (consumed and expired files); topology 2 (start-fake-clients.ps1 uploads the save, the real DLL
  hosts as the authority, 3 FakeNode clients verify the checkpoint, "Save now"); uninstall.ps1.
  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\session3_dry_run.ps1
  Exit 0 = pass. Takes about 3 minutes (plus the web build the first time, about 35 s).
#>
[CmdletBinding()]
param(
    [string]$Config = 'relwithdebinfo',
    [int]$TcpPort = 47974,
    [int]$UdpPort = 47975,
    [int]$HttpPort = 47976
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$s3 = Join-Path $repo 'tools\session3'
$build = Join-Path $repo "mod\build\msvc-x64-$Config"
$hostSim = Join-Path $build 'x4mp-hostsim.exe'
$dll = Join-Path $build 'x4mp.dll'
$powershell = (Get-Process -Id $PID).Path
foreach ($f in $hostSim, $dll) { if (-not (Test-Path $f)) { throw "Missing $f (run mod\build.ps1 first)" } }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-s3-dry-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$docs = Join-Path $tmp 'docs'
$userDir = Join-Path $docs 'Egosoft\X4\12345'
$saveDir = Join-Path $userDir 'save'
$cfgDir = Join-Path $docs 'Egosoft\X4\x4mp'
$outDir = Join-Path $tmp 'out'
$fakeX4 = Join-Path $tmp 'X4 Foundations'
$pw = 'Testpw-314159'
New-Item -ItemType Directory -Force $saveDir, $outDir, (Join-Path $fakeX4 'extensions') | Out-Null
Write-Host "Temp tree: $tmp"

$env:X4MP_S2_DOCS_ROOT = $docs
$env:X4MP_S3_OUT_DIR = $outDir
$sw = [Diagnostics.Stopwatch]::StartNew()
$procs = New-Object System.Collections.Generic.List[object]
$exit = 1
function Stop-All { foreach ($p in $procs) { if (-not $p.HasExited) { try { & taskkill /PID $p.Id /T /F 2>$null | Out-Null } catch { } } } }
function Step($name, [scriptblock]$body) {
    $t = [Diagnostics.Stopwatch]::StartNew()
    Write-Host ''
    Write-Host "=== $name" -ForegroundColor Cyan
    & $body
    Write-Host ("--- {0}: OK in {1:N1} s" -f $name, $t.Elapsed.TotalSeconds) -ForegroundColor Green
}
# Runs a kit script in a child Windows PowerShell and returns @{ Exit; Text }.
function Run-Kit([string]$script, [string[]]$kitArgs = @()) {
    $f = Join-Path $s3 $script
    $o = Join-Path $tmp ('kit-' + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.txt')
    $p = Start-Process $powershell -ArgumentList (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$f`"") + $kitArgs) -PassThru -Wait -NoNewWindow -RedirectStandardOutput $o -RedirectStandardError ($o + '.err')
    $null = $p.Handle
    $text = (Get-Content $o -Raw) + (Get-Content ($o + '.err') -Raw)
    return @{ Exit = $p.ExitCode; Text = $text }
}
function Expect-Kit($r, [string]$what, [int]$exitCode = 0, [string[]]$mustHave = @()) {
    if ($r.Exit -ne $exitCode) { throw "$what : exit $($r.Exit), expected $exitCode`n$($r.Text)" }
    foreach ($m in $mustHave) { if ($r.Text -notmatch $m) { throw "$what : output lacks /$m/`n$($r.Text)" } }
}
function Start-P([string]$exe, [string[]]$argList, [string]$out) {
    $q = @($argList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
    $p = Start-Process -FilePath $exe -ArgumentList $q -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError ($out + '.err')
    $null = $p.Handle; $procs.Add($p); return $p
}
function Read-Shared([string]$path) {
    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite'); $sr = New-Object IO.StreamReader($fs)
    try { return $sr.ReadToEnd() } finally { $sr.Dispose() }
}
function Wait-File([string]$what, [string]$path, [string]$pattern, $proc, [int]$sec = 120, [int]$count = 1) {
    $end = (Get-Date).AddSeconds($sec)
    while ($true) {
        if ($proc.HasExited) { throw "$what : the process exited ($($proc.ExitCode))" }
        if (Test-Path $path) { try { if (([regex]::Matches((Read-Shared $path), $pattern)).Count -ge $count) { return } } catch { } }
        if ((Get-Date) -gt $end) { throw "Timed out waiting for $what ($pattern in $path)" }
        Wait-Process -Id $proc.Id -Timeout 1 -ErrorAction SilentlyContinue
    }
}
function New-GzSave([string]$path, [int]$seed, [int]$mb = 3) {
    $rnd = New-Object byte[] ($mb * 1MB); (New-Object Random $seed).NextBytes($rnd)
    $fs = [IO.File]::Create($path); $gz = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Compress)
    $head = [Text.Encoding]::UTF8.GetBytes('<?xml version="1.0" encoding="utf-8"?><savegame><info><game id="x4mp-s3-dry"/></info></savegame>')
    $gz.Write($head, 0, $head.Length); $gz.Write($rnd, 0, $rnd.Length); $gz.Dispose(); $fs.Dispose()
}
function Run-HostSim([string]$name, [string]$script, [string]$workName, [string[]]$vars, [int]$maxSec = 150) {
    $work = Join-Path $tmp $workName
    $out = Join-Path $tmp "$name.out.txt"
    $a = @('--dll', $dll, '--script', (Join-Path $PSScriptRoot $script), '--work-dir', $work, '--max-seconds', $maxSec) + $vars
    $t0 = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-P $hostSim $a $out
    if (-not $p.WaitForExit(($maxSec + 20) * 1000)) { & taskkill /PID $p.Id /T /F *> $null; throw "$name did not finish in $($maxSec + 20) s" }
    $p.WaitForExit()
    Get-Content $out -Tail 8 | ForEach-Object { Write-Host "  $_" }
    if ($p.ExitCode -ne 0) { throw "$name failed (exit $($p.ExitCode)); output: $out; mod log: $(Join-Path $work 'extension\logs\x4mp.log')" }
    Write-Host ("  {0} PASSED in {1:N1} s" -f $name, $t0.Elapsed.TotalSeconds) -ForegroundColor Green
}
$portArgs = @('-TcpPort', $TcpPort, '-UdpPort', $UdpPort, '-HttpPort', $HttpPort)

try {
    # ---------------------------------------------------------------- the fake machine
    New-GzSave (Join-Path $saveDir 'save_004.xml.gz') 4
    New-Item -ItemType File -Force (Join-Path $fakeX4 'X4.exe') | Out-Null
    foreach ($e in 'x4native', 'x4mp_probe', 'x4mp_spike') {
        New-Item -ItemType Directory -Force (Join-Path $fakeX4 "extensions\$e") | Out-Null
        Set-Content (Join-Path $fakeX4 "extensions\$e\content.xml") "<content id=`"$e`" name=`"old`" version=`"1`"/>"
    }
    # Two DLCs like a real install (content.xml version in hundredths); the user disabled one. The fake authority must report exactly the
    # enabled one, as "9.00" (the session-3 bug: no DLCs at all, so every real player with DLCs was refused).
    foreach ($d in @(@('ego_dlc_split', 'Split Vendetta'), @('ego_dlc_boron', 'Kingdom End'))) {
        New-Item -ItemType Directory -Force (Join-Path $fakeX4 "extensions\$($d[0])") | Out-Null
        Set-Content (Join-Path $fakeX4 "extensions\$($d[0])\content.xml") ('<content id="' + $d[0] + '" name="' + $d[1] + '" version="900" enabled="1"/>')
    }
    Set-Content (Join-Path $userDir 'content.xml') '<extensions><extension id="x4mp_probe" enabled="true"/><extension id="x4mp_spike" enabled="true"/><extension id="ego_dlc_boron" enabled="false"/></extensions>'
    $x4a = @('-X4Dir', ('"' + $fakeX4 + '"'))

    Step 'publish incl. the web GUI (the kit helper)' {
        $cmd = ". '$(Join-Path $s3 'common.ps1')'; `$null = Ensure-Published"
        $p = Start-Process $powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', "`"$cmd`"") -PassThru -Wait -NoNewWindow
        if ($p.ExitCode -ne 0) { throw "Ensure-Published failed ($($p.ExitCode))" }
        if (-not (Test-Path (Join-Path $repo 'server\web\dist\index.html'))) { throw 'server\web\dist was not built' }
    }

    Step '-WhatIf of every script' {
        Expect-Kit (Run-Kit 'install.ps1' (@('-WhatIf') + $x4a)) 'install -WhatIf' 0 @('x4mp_probe', 'Would run')
        Expect-Kit (Run-Kit 'uninstall.ps1' (@('-WhatIf') + $x4a)) 'uninstall -WhatIf'
        Expect-Kit (Run-Kit 'start-fake-authority.ps1' (@('-SaveName', 'save_004', '-JoinPassword', $pw, '-WhatIf', '-X4Dir', ('"' + $fakeX4 + '"')) + $portArgs)) 'start-fake-authority -WhatIf' 0 @('FakeNode', 'join password: set', 'your DLCs: ego_dlc_split@9.00')
        Expect-Kit (Run-Kit 'start-fake-authority.ps1' (@('-List') + $portArgs)) 'start-fake-authority -List' 0 @('save_004')
        Expect-Kit (Run-Kit 'start-fake-clients.ps1' (@('-SaveName', 'save_004', '-WhatIf') + $portArgs)) 'start-fake-clients -WhatIf' 0 @('3 FakeNode clients')
        Expect-Kit (Run-Kit 'upload-save.ps1' @('-SaveName', 'save_004', '-HttpPort', $HttpPort, '-WhatIf')) 'upload-save -WhatIf' 0 @('Plan')
        Expect-Kit (Run-Kit 'find-password.ps1' @('-WhatIf')) 'find-password -WhatIf'
        Expect-Kit (Run-Kit 'collect-logs.ps1' @('-WhatIf', '-Label', 'x')) 'collect-logs -WhatIf'
        Expect-Kit (Run-Kit 'write-config.ps1' @('-SelfTest', '-WhatIf')) 'write-config -WhatIf'
        Expect-Kit (Run-Kit 'write-launch.ps1' @('-Password', $pw, '-WhatIf')) 'write-launch -WhatIf'
        if (Test-Path $cfgDir) { throw 'a -WhatIf run wrote into the config folder' }
    }

    Step 'install: refuses, then removes the session-2 extensions and deploys' {
        Expect-Kit (Run-Kit 'install.ps1' $x4a) 'install without -RemoveTestExtensions' 1 @('Refusing')
        Expect-Kit (Run-Kit 'install.ps1' (@('-RemoveTestExtensions', '-Force') + $x4a)) 'install -RemoveTestExtensions -Force' 0 @('Removed', 'Deployed')
        foreach ($gone in 'x4mp_probe', 'x4mp_spike') { if (Test-Path (Join-Path $fakeX4 "extensions\$gone")) { throw "$gone still installed" } }
        foreach ($there in 'x4mp\native\x4mp.dll', 'x4mp\content.xml', 'x4mp\t\0001-l044.xml', 'x4native\content.xml') {
            if (-not (Test-Path (Join-Path $fakeX4 "extensions\$there"))) { throw "missing after install: $there" }
        }
        if ((Get-Content (Join-Path $fakeX4 'extensions\x4native\content.xml') -Raw) -match 'name="old"') { throw 'x4native was not replaced by the vendored copy' }
        Write-Host '  x4mp + x4native installed from the repo, probe and spike gone'
    }

    Step 'write-config.ps1 -SelfTest' {
        Expect-Kit (Run-Kit 'write-config.ps1' @('-SelfTest')) 'write-config' 0 @('Written')
        $j = Get-Content (Join-Path $cfgDir 'x4mp.json') -Raw | ConvertFrom-Json
        if ($j.selftest -ne $true) { throw 'selftest is not true' }
        if ((Get-Content (Join-Path $cfgDir 'x4mp.json') -Raw) -match [regex]::Escape($pw)) { throw 'password in x4mp.json' }
    }

    # ---------------------------------------------------------------- topology 1
    $fa = $null
    $faArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $s3 'start-fake-authority.ps1'), '-SaveName', 'save_004', '-JoinPassword', $pw, '-X4Dir', $fakeX4) + ($portArgs | ForEach-Object { "$_" })
    $fakeLog = Join-Path $outDir 'fakenode.log'
    $pidFile = Join-Path $tmp 'fa.pid'
    $restart = Join-Path $tmp 'restart_s3.ps1'
    Set-Content $restart @"
`$ErrorActionPreference = 'Stop'
`$old = [int](Get-Content '$pidFile' -Raw)
& taskkill /PID `$old /T /F | Out-Null
Remove-Item '$fakeLog' -ErrorAction SilentlyContinue
`$p = Start-Process '$powershell' -ArgumentList @($(($faArgs | ForEach-Object { $q = if ($_ -match '\s') { '"' + $_ + '"' } else { "$_" }; "'" + ($q -replace "'", "''") + "'" }) -join ',')) -PassThru -WindowStyle Hidden -RedirectStandardOutput '$(Join-Path $tmp 'fa2.out.txt')' -RedirectStandardError '$(Join-Path $tmp 'fa2.err.txt')'
Set-Content '$pidFile' `$p.Id
`$end = (Get-Date).AddSeconds(120)
while (`$true) {
  if (`$p.HasExited) { throw 'start-fake-authority.ps1 exited after the restart' }
  if ((Test-Path '$fakeLog') -and (Select-String -Path '$fakeLog' -Pattern 'checkpoint stored' -Quiet)) { break }
  if ((Get-Date) -gt `$end) { throw 'no checkpoint stored after the restart' }
  Wait-Process -Id `$p.Id -Timeout 1 -ErrorAction SilentlyContinue
}
'restarted'
"@
    Step 'topology 1: start-fake-authority.ps1 (join password) and the real DLL as a client' {
        $script:fa = Start-P $powershell $faArgs (Join-Path $tmp 'fa.out.txt')
        Set-Content $pidFile $script:fa.Id
        Wait-File 'FakeNode authority: checkpoint stored' $fakeLog 'checkpoint stored' $script:fa 300
        Write-Host ('  ' + ((Get-Content $fakeLog | Where-Object { $_ -match 'checkpoint stored' } | Select-Object -First 1)))
        $out = Get-Content (Join-Path $tmp 'fa.out.txt') -Raw
        if ($out -notmatch 'Admin GUI: http://127.0.0.1:' + $HttpPort) { throw "no GUI address in the script output`n$out" }
        if ($out -match [regex]::Escape((Get-Content (Join-Path $outDir 'admin-password.txt') -Raw).Trim())) { throw 'the admin password was printed' }
        if ((Get-Content (Join-Path $tmp 'fa.out.txt.err') -Raw) -match 'WARNING') { throw 'the script printed a warning' }
        $authExt = Get-Content (Join-Path $outDir 'authority-extensions.json') -Raw | ConvertFrom-Json
        if (@($authExt).Count -ne 1 -or @($authExt)[0].id -ne 'ego_dlc_split' -or @($authExt)[0].version -ne '9.00') { throw 'authority-extensions.json should hold exactly ego_dlc_split 9.00 (boron is disabled)' }
        Write-Host '  authority-extensions.json: ego_dlc_split 9.00 only (disabled boron left out)'
        $page = (Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$HttpPort/").Content
        if ($page -match 'GUI not built') { throw 'the server shows "GUI not built"' }
        Write-Host '  GUI root page: real SPA (not the placeholder)'
        New-Item -ItemType Directory -Force (Join-Path $tmp 'work-client\extension') | Out-Null
        Copy-Item (Join-Path $cfgDir 'x4mp.json') (Join-Path $tmp 'work-client\extension\x4mp.json') -Force
        Run-HostSim 'session3_client' 'session3_client.hostsim' 'work-client' @('--var', "tcp=$TcpPort", '--var', "pw=$pw", '--var', "restart=$restart",
            '--admin-url', "http://127.0.0.1:$HttpPort", '--admin-user', 'admin', '--admin-password', ((Get-Content (Join-Path $outDir 'admin-password.txt') -Raw).Trim())) 200
        # the doc's server-log expectations
        $serverLog = Get-ChildItem (Join-Path $outDir 'data\logs') -Filter 'server-*.log' | Select-Object -First 1
        $text = Read-Shared $serverLog.FullName
        foreach ($pat in '\(Tester\) joined as', '\(Tester\) detached: "ClientReload"', '\(Tester\) resumed \(baseline epoch', 'node:Tester SELFTEST summary:') {
            if ($text -notmatch $pat) { throw "server log lacks /$pat/" }
        }
        Write-Host '  server log: joined, detached: "ClientReload", resumed, node:Tester SELFTEST summary'
    }

    Step 'find-password.ps1: NO HIT, then a planted HIT' {
        $workExt = Join-Path $tmp 'work-client\extension'
        New-Item -ItemType Directory -Force (Join-Path $cfgDir 'logs') | Out-Null
        Copy-Item (Join-Path $workExt 'logs\x4mp.log') (Join-Path $cfgDir 'logs\x4mp.log') -Force
        Set-Content (Join-Path $userDir 'x4mp_s3.log') 'game log without the secret'
        $r = Run-Kit 'find-password.ps1' @('-Password', $pw, '-X4Dir', "`"$fakeX4`"")
        Expect-Kit $r 'find-password (clean)' 0 @('RESULT: no hit', 'x4mp\.log', 'NO HIT')
        if ($r.Text -match [regex]::Escape($pw)) { throw 'find-password printed the password' }
        # planted: UTF-16 inside a game log, URL-quoted inside uidata.xml
        [IO.File]::WriteAllBytes((Join-Path $userDir 'x4mp_s3.log'), [Text.Encoding]::Unicode.GetBytes("junk $pw junk"))
        $r = Run-Kit 'find-password.ps1' @('-Password', $pw, '-X4Dir', "`"$fakeX4`"")
        Expect-Kit $r 'find-password (planted UTF-16)' 1 @('HIT\s+.*x4mp_s3\.log', 'FAILS')
        Set-Content (Join-Path $userDir 'x4mp_s3.log') 'clean again'
        Set-Content (Join-Path $userDir 'uidata.xml') ('<a v="' + [uri]::EscapeDataString($pw) + '"/>')
        $r = Run-Kit 'find-password.ps1' @('-Password', $pw, '-X4Dir', "`"$fakeX4`"")
        Expect-Kit $r 'find-password (planted URL-quoted)' 1 @('HIT\s+.*uidata\.xml')
        Remove-Item (Join-Path $userDir 'uidata.xml')
        Write-Host '  NO HIT on clean files; HIT for UTF-16 and URL-quoted plants; the password was never printed'
    }

    Step 'launch.json: write-launch.ps1, consumed and expired' {
        Expect-Kit (Run-Kit 'write-launch.ps1' @('-Server', "127.0.0.1:$TcpPort", '-Name', 'Launcher', '-Password', $pw, '-Minutes', '10')) 'write-launch' 0 @('Written', 'password=<set>')
        $bytes = [IO.File]::ReadAllBytes((Join-Path $cfgDir 'launch.json'))
        if ($bytes[0] -eq 0xEF) { throw 'launch.json has a byte-order mark' }
        Write-Host ('  launch.json: ' + ([Text.Encoding]::UTF8.GetString($bytes) -replace [regex]::Escape($pw), '<password>'))
        New-Item -ItemType Directory -Force (Join-Path $tmp 'work-launch\extension') | Out-Null
        Move-Item (Join-Path $cfgDir 'launch.json') (Join-Path $tmp 'work-launch\extension\launch.json') -Force
        Run-HostSim 'session3_launch' 'session3_launch.hostsim' 'work-launch' @('--var', "tcp=$TcpPort", '--var', "pw=$pw",
            '--admin-url', "http://127.0.0.1:$HttpPort", '--admin-user', 'admin', '--admin-password', ((Get-Content (Join-Path $outDir 'admin-password.txt') -Raw).Trim())) 100
        Expect-Kit (Run-Kit 'write-launch.ps1' @('-Server', "127.0.0.1:$TcpPort", '-Name', 'Late', '-Password', $pw, '-Expired')) 'write-launch -Expired' 0 @('already expired')
        New-Item -ItemType Directory -Force (Join-Path $tmp 'work-expired\extension') | Out-Null
        Move-Item (Join-Path $cfgDir 'launch.json') (Join-Path $tmp 'work-expired\extension\launch.json') -Force
        Run-HostSim 'session3_launch_expired' 'session3_launch_expired.hostsim' 'work-expired' @('--var', "pw=$pw") 60
    }

    Step 'collect-logs.ps1' {
        Set-Content (Join-Path $cfgDir 'launch.json') '{"password":"must-not-be-zipped"}'   # a launch.json X4 never consumed
        New-Item -ItemType Directory -Force (Join-Path $userDir 'x4native') | Out-Null
        Set-Content (Join-Path $userDir 'x4native\x4native.log') 'x4native log'
        $r = Run-Kit 'collect-logs.ps1' @('-Label', 'topo1')
        Expect-Kit $r 'collect-logs' 0 @('Created', 'Nothing was uploaded')
        $zip = Get-ChildItem $outDir -Filter 'logs-topo1-*.zip' | Select-Object -First 1
        if (-not $zip) { throw 'no logs-topo1-*.zip' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $z = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
        try { $names = @($z.Entries | ForEach-Object { $_.FullName }) } finally { $z.Dispose() }
        Write-Host ('  zip: ' + ($names -join ', '))
        foreach ($want in 'x4mp_s3.log', 'fakenode.log', 'x4native*x4native.log', 'x4mp-config*x4mp.json', 'x4mp-config*x4mp.log', 'server-logs*server-*.log') {
            if (-not ($names -like $want)) { throw "zip lacks $want" }
        }
        foreach ($bad in '*launch.json*', '*admin-password*', '*.db', '*.xml.gz') { if ($names -like $bad) { throw "zip contains $bad" } }
        Remove-Item (Join-Path $cfgDir 'launch.json')
    }

    Step 'stop topology 1' {
        $cur = [int](Get-Content $pidFile -Raw)
        & taskkill /PID $cur /T /F | Out-Null
        Wait-Process -Id $cur -Timeout 20 -ErrorAction SilentlyContinue
        # the restarted script's server must be gone too (it was a child of that process tree)
        $left = @(Get-Process -Name 'x4mp-server', 'X4MP.FakeNode' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$repo*" })
        foreach ($l in $left) { Write-Host "  stopping leftover $($l.Name) $($l.Id)"; Stop-Process -Id $l.Id -Force }
        Write-Host '  stopped'
    }

    # ---------------------------------------------------------------- B7: strict + modded, then back to normal
    function Start-FA([string[]]$extra, [string]$tag) {
        Remove-Item $fakeLog -ErrorAction SilentlyContinue
        $p = Start-P $powershell (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $s3 'start-fake-authority.ps1'), '-SaveName', 'save_004', '-X4Dir', $fakeX4) + $extra + ($portArgs | ForEach-Object { "$_" })) (Join-Path $tmp "fa-$tag.out.txt")
        Wait-File "the fake authority ($tag)" $fakeLog 'checkpoint stored' $p 120
        return $p
    }
    function Get-ModEntries {
        $h = @{ 'X-X4MP' = '1' }; $u = "http://127.0.0.1:$HttpPort"
        $web = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $apw = (Get-Content (Join-Path $outDir 'admin-password.txt') -Raw).Trim()
        $null = Invoke-RestMethod -Method Post -Uri "$u/api/v1/auth/login" -Headers $h -WebSession $web -ContentType 'application/json' -Body (@{ username = 'admin'; password = $apw } | ConvertTo-Json)
        return (Invoke-RestMethod -Uri "$u/api/v1/mods" -Headers $h -WebSession $web)
    }
    Step 'B7: -Strict -AuthorityExtensions modded refuses a mod-less client with the Nexus address; a normal start removes the entry' {
        $m = Start-FA @('-Strict', '-AuthorityExtensions', 'modded') 'modded'
        $state = Get-ModEntries
        $e = @($state.policy.entries | Where-Object { $_.id -eq 'sn_better_traders' })
        if ($e.Count -ne 1 -or $e[0].nexusUrl -notmatch '/mods/1234') { throw 'the Nexus entry for Better Traders is missing' }
        Write-Host ('  enforcement: ' + $state.policy.enforcement + '; Nexus entry present')
        if ($state.policy.enforcement -ne 'Strict') { throw "enforcement is $($state.policy.enforcement), expected Strict" }
        Run-HostSim 'session3_refusal' 'session3_refusal.hostsim' 'work-refusal' @('--var', "tcp=$TcpPort") 60
        & taskkill /PID $m.Id /T /F | Out-Null
        Wait-Process -Id $m.Id -Timeout 20 -ErrorAction SilentlyContinue
        $n = Start-FA @() 'normal'
        $state = Get-ModEntries
        if (@($state.policy.entries | Where-Object { $_.id -eq 'sn_better_traders' }).Count -ne 0) { throw 'the Nexus entry stayed after a normal start' }
        Write-Host ('  normal start: entry removed; enforcement: ' + $state.policy.enforcement)
        if ($state.policy.enforcement -ne 'Warn') { throw "enforcement is $($state.policy.enforcement), expected Warn" }
        & taskkill /PID $n.Id /T /F | Out-Null
        Wait-Process -Id $n.Id -Timeout 20 -ErrorAction SilentlyContinue
        $left = @(Get-Process -Name 'x4mp-server', 'X4MP.FakeNode' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$repo*" })
        foreach ($l in $left) { Stop-Process -Id $l.Id -Force }
    }

    # ---------------------------------------------------------------- topology 2
    Remove-Item -Recurse -Force (Join-Path $outDir 'data'), (Join-Path $outDir 'admin-password.txt') -ErrorAction SilentlyContinue
    Step 'topology 2: start-fake-clients.ps1 uploads the save, the real DLL hosts as the authority' {
        New-GzSave (Join-Path $tmp 'ckpt1.xml.gz') 11; New-GzSave (Join-Path $tmp 'ckpt2.xml.gz') 12
        $fc = Start-P $powershell (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $s3 'start-fake-clients.ps1'), '-SaveName', 'save_004') + ($portArgs | ForEach-Object { "$_" })) (Join-Path $tmp 'fc.out.txt')
        Wait-File 'the script to wait for the authority' (Join-Path $tmp 'fc.out.txt') 'Waiting up to' $fc 300
        $out = Get-Content (Join-Path $tmp 'fc.out.txt') -Raw
        foreach ($m in 'x4mp-host-test', 'Uploaded', "created from the upload and started") { if ($out -notmatch $m) { throw "start-fake-clients output lacks '$m'`n$out" } }
        $apw = (Get-Content (Join-Path $outDir 'admin-password.txt') -Raw).Trim()
        Run-HostSim 'session3_authority' 'session3_authority.hostsim' 'work-host' @('--var', "tcp=$TcpPort", '--var', 'apw=x4mp-host-test',
            '--var', "sim=$(Join-Path $PSScriptRoot 'authority_sim.ps1')", '--var', "admin_url=http://127.0.0.1:$HttpPort", '--var', "admin_pw=$apw",
            '--var', "ckpt_src=$(Join-Path $tmp 'ckpt1.xml.gz')", '--var', "ckpt_src2=$(Join-Path $tmp 'ckpt2.xml.gz')",
            '--admin-url', "http://127.0.0.1:$HttpPort", '--admin-user', 'admin', '--admin-password', $apw) 200
        $clientsLog = Join-Path $outDir 'fakenode-clients.log'
        Wait-File 'the 3 FakeNode clients to download the checkpoint' $clientsLog 'joined with the save after' $fc 90 3
        Write-Host ((Get-Content $clientsLog | Where-Object { $_ -match 'joined with the save after' } | Select-Object -First 3) -join "`n")
        $h = @{ 'X-X4MP' = '1' }; $u = "http://127.0.0.1:$HttpPort"
        $web = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $null = Invoke-RestMethod -Method Post -Uri "$u/api/v1/auth/login" -Headers $h -WebSession $web -ContentType 'application/json' -Body (@{ username = 'admin'; password = $apw } | ConvertTo-Json)
        $cur = Invoke-RestMethod -Uri "$u/api/v1/sessions/current" -Headers $h -WebSession $web
        # (the hostsim script already asserted Running while the DLL was connected; after it quits the authority is gone)
        Write-Host "  session state after the authority quit: $($cur.state)"
        $saves = @(Invoke-RestMethod -Uri "$u/api/v1/saves" -Headers $h -WebSession $web | ForEach-Object { $_ })
        Write-Host ('  Saves page rows: ' + (($saves | ForEach-Object { "$($_.displayName) [$($_.source)] ghostsCleaned=$($_.ghostsCleaned) current=$($_.current)" }) -join '; '))
        if ($saves.Count -lt 3) { throw "expected the upload plus two checkpoints, found $($saves.Count) saves" }
        $players = @(Invoke-RestMethod -Uri "$u/api/v1/players" -Headers $h -WebSession $web | ForEach-Object { $_ })
        Write-Host ('  Players: ' + (($players | ForEach-Object { $_.name }) -join ', '))
    }

    Step 'uninstall.ps1' {
        Expect-Kit (Run-Kit 'uninstall.ps1' $x4a) 'uninstall' 0 @('Removed')
        foreach ($gone in 'x4mp', 'x4native') { if (Test-Path (Join-Path $fakeX4 "extensions\$gone")) { throw "$gone still installed" } }
    }
    $exit = 0
}
catch { Write-Host "SESSION 3 DRY RUN FAILED: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    Stop-All
    $left = @(Get-Process -Name 'x4mp-server', 'X4MP.FakeNode', 'x4mp-hostsim' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$repo*" })
    foreach ($l in $left) { try { Stop-Process -Id $l.Id -Force } catch { } }
    Remove-Item Env:\X4MP_S2_DOCS_ROOT, Env:\X4MP_S3_OUT_DIR -ErrorAction SilentlyContinue
    Write-Host ("Session-3 dry run {0} in {1:N0} s. Temp tree kept for inspection: {2}" -f $(if ($exit -eq 0) { 'PASSED' } else { 'FAILED' }), $sw.Elapsed.TotalSeconds, $tmp)
}
exit $exit
