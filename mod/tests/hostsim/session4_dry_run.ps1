<#
.SYNOPSIS
  Session-4 sitting-0 kit dry run without X4 (NOT part of CI): runs the tools\session4 scripts against a temp "Documents" folder and a
  temp fake X4 install. The real Documents folder and the real X4 install are never touched.
.DESCRIPTION
  The kit scripts honour the test-only environment variables X4MP_S2_DOCS_ROOT (stand-in for Documents) and X4MP_S4_OUT_DIR (stand-in for
  out\session4); the fake X4 folder is passed with -X4Dir, a stub file stands in for x4mp_probe.dll (-ProbeDll).
  What it proves: -WhatIf of every script changes nothing; install-spike.ps1 parks the product x4mp outside the install, switches it off in
  content.xml (with backup), installs x4native + x4mp_probe + x4mp_spike, and never touches the save folder; write-probe-config.ps1 writes the
  scratch slot and removes stale server/password keys; run-block.ps1 writes "<block> <args>" with a rising sequence and refuses the
  scratch-only blocks without a scratch slot; collect-logs.ps1 builds the zip (game log, spike-lines.txt, x4native, probe config with the password
  blanked, no launch.json, no saves) and rebuilds galaxy-dump.json from the S13.11 DATA lines with >= 140 sectors; extract-galaxy-dump.ps1;
  install-spike.ps1 -Restore brings the product back, enabled, and removes the spike kit.
  Run:  powershell -NoProfile -ExecutionPolicy Bypass -File mod\tests\hostsim\session4_dry_run.ps1
  Exit 0 = pass. Takes about 20 s.
#>
[CmdletBinding()]
param(
    # kit      = every script with -WhatIf and for real against temp folders, no server (CI step Session4Kit, about 40 s). Sitting 0 + sittings 1-3 scripts.
    # topology = sittings 1-3 end to end: start-fake-authority.ps1 / start-fake-clients.ps1 / start-server-lan.ps1 with the real x4mp.dll in hostsim
    #            (needs mod\build.ps1 and tools\e2e.ps1 -Steps Publish; about 6 minutes, local only; ports 47977-47979)
    [ValidateSet('kit', 'topology', 'all')][string]$Part = 'kit',
    [string]$Config = 'relwithdebinfo',
    [int]$TcpPort = 47977,
    [int]$UdpPort = 47978,
    [int]$HttpPort = 47979
)
$ErrorActionPreference = 'Stop'
$runKit = $Part -in 'kit', 'all'
$runTopology = $Part -in 'topology', 'all'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$s4 = Join-Path $repo 'tools\session4'
$powershell = (Get-Process -Id $PID).Path

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-s4-dry-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$docs = Join-Path $tmp 'docs'
$userDir = Join-Path $docs 'Egosoft\X4\12345'
$saveDir = Join-Path $userDir 'save'
$cfgDir = Join-Path $docs 'Egosoft\X4\x4mp'
$outDir = Join-Path $tmp 'out'
$fakeX4 = Join-Path $tmp 'X4 Foundations'
$ext = Join-Path $fakeX4 'extensions'
New-Item -ItemType Directory -Force $saveDir, $cfgDir, $outDir, (Join-Path $ext 'x4mp\ui') | Out-Null
Set-Content (Join-Path $fakeX4 'X4.exe') 'stub' -Encoding ASCII
Set-Content (Join-Path $ext 'x4mp\content.xml') '<content id="x4mp" name="X4MP" version="100"/>' -Encoding ASCII
Set-Content (Join-Path $ext 'x4mp\ui\x4mp_menu.lua') '-- product stub' -Encoding ASCII
Set-Content (Join-Path $saveDir 'save_007.xml.gz') 'not a real save' -Encoding ASCII
Set-Content (Join-Path $saveDir 'save_001.xml.gz') 'precious' -Encoding ASCII
Set-Content (Join-Path $userDir 'content.xml') '<extensions><extension id="x4mp" enabled="true"/><extension id="ego_dlc_split" enabled="true"/></extensions>' -Encoding ASCII
$fakeDll = Join-Path $tmp 'x4mp_probe.dll'
Set-Content $fakeDll 'stub dll' -Encoding ASCII
Write-Host "Temp tree: $tmp"

$env:X4MP_S2_DOCS_ROOT = $docs
$env:X4MP_S4_OUT_DIR = $outDir
$saveHash = { (Get-ChildItem $saveDir -File | Sort-Object Name | ForEach-Object { $_.Name + ':' + (Get-FileHash $_.FullName).Hash }) -join ';' }
$savesBefore = & $saveHash

$script:checks = 0
function Check([bool]$cond, [string]$what) { $script:checks++; if (-not $cond) { throw "CHECK FAILED: $what" } else { Write-Host "  ok  $what" } }
function Step($name, [scriptblock]$body) { Write-Host ''; Write-Host "=== $name" -ForegroundColor Cyan; & $body }
function Run-Kit([string]$script, [string[]]$kitArgs = @()) {
    $f = Join-Path $s4 $script
    $o = Join-Path $tmp ('kit-' + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.txt')
    $p = Start-Process $powershell -ArgumentList (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$f`"") + $kitArgs) -PassThru -Wait -NoNewWindow -RedirectStandardOutput $o -RedirectStandardError ($o + '.err')
    $null = $p.Handle
    return @{ Exit = $p.ExitCode; Text = ((Get-Content $o -Raw) + (Get-Content ($o + '.err') -Raw)) }
}
function Expect-Kit($r, [string]$what, [int]$exitCode = 0, [string[]]$mustHave = @()) {
    if ($r.Exit -ne $exitCode) { throw "$what : exit $($r.Exit), expected $exitCode`n$($r.Text)" }
    foreach ($m in $mustHave) { if ($r.Text -notmatch $m) { throw "$what : output lacks /$m/`n$($r.Text)" } }
}
$x4 = @('-X4Dir', "`"$fakeX4`"")
$probeCfg = Join-Path $cfgDir 'x4mp_probe.json'

try {
  if ($runKit) {
    Step '-WhatIf of every script changes nothing' {
        foreach ($s in @(
                @('install-spike.ps1', ($x4 + @('-ProbeDll', "`"$fakeDll`"", '-Force', '-WhatIf'))),
                @('install-spike.ps1', ($x4 + @('-Restore', '-Force', '-WhatIf'))),
                @('write-probe-config.ps1', @('-ScratchSlot', 'save_007', '-WhatIf')),
                @('run-block.ps1', @('ghost_spawn', '-WhatIf')),
                @('collect-logs.ps1', @('-Label', 'whatif', '-WhatIf')),
                @('extract-galaxy-dump.ps1', @('-WhatIf')))) {
            $r = Run-Kit $s[0] $s[1]
            Expect-Kit $r "$($s[0]) -WhatIf"
        }
        Check (Test-Path (Join-Path $ext 'x4mp\content.xml')) 'product still installed after -WhatIf'
        Check (-not (Test-Path (Join-Path $ext 'x4mp_spike'))) 'no spike folder after -WhatIf'
        Check (-not (Test-Path $probeCfg)) 'no probe config after -WhatIf'
        Check ((Get-Content (Join-Path $userDir 'content.xml') -Raw) -match 'id="x4mp" enabled="true"') 'content.xml untouched by -WhatIf'
    }

    Step 'install-spike.ps1: park the product, install the kit' {
        $r = Run-Kit 'install-spike.ps1' ($x4 + @('-ProbeDll', "`"$fakeDll`"", '-Force'))
        Expect-Kit $r 'install-spike' 0 @('PARKED', 'Installed and unblocked')
        Check (-not (Test-Path (Join-Path $ext 'x4mp'))) 'product x4mp is gone from extensions\'
        Check (Test-Path (Join-Path $outDir 'parked\x4mp\content.xml')) 'product parked outside the X4 install'
        foreach ($n in 'x4native', 'x4mp_probe', 'x4mp_spike') { Check (Test-Path (Join-Path $ext $n)) "$n installed" }
        Check (Test-Path (Join-Path $ext 'x4mp_probe\native\x4mp_probe.dll')) 'probe dll copied into native\'
        Check (Test-Path (Join-Path $ext 'x4mp_spike\ui\x4mp_spike_s13.lua')) 'spike session-4 Lua installed'
        Check (Test-Path (Join-Path $ext 'x4mp_spike\md\x4mp_spike_s13.xml')) 'spike session-4 MD installed'
        $c = Get-Content (Join-Path $userDir 'content.xml') -Raw
        Check ($c -match 'id="x4mp" enabled="false"') 'product switched off in content.xml'
        Check (Test-Path (Join-Path $userDir 'content.xml.x4mp-bak')) 'content.xml backup made'
        Check ((& $saveHash) -eq $savesBefore) 'save folder untouched by the install'
        # a second run (product already parked) works and keeps the parked copy
        $r = Run-Kit 'install-spike.ps1' ($x4 + @('-ProbeDll', "`"$fakeDll`"", '-Force'))
        Expect-Kit $r 'install-spike (second run)' 0 @('not installed')
        Check (Test-Path (Join-Path $outDir 'parked\x4mp\content.xml')) 'parked product survives a second install'
    }

    Step 'write-probe-config.ps1' {
        Set-Content $probeCfg '{"server":"10.0.0.5:47780","password":"hunter2","name":"Old","spike_block_seq":4,"hooks":true}' -Encoding UTF8
        $r = Run-Kit 'write-probe-config.ps1' @('-ScratchSlot', '7')
        Expect-Kit $r 'write-probe-config' 0 @('save_007', 'Scratch slot file found')
        $j = Get-Content $probeCfg -Raw | ConvertFrom-Json
        Check ($j.scratch_slot -eq 'save_007') 'scratch_slot written (7 -> save_007)'
        Check (-not ($j.PSObject.Properties.Name -contains 'password')) 'stale password removed'
        Check (-not ($j.PSObject.Properties.Name -contains 'server')) 'stale server removed'
        Check ($j.spike_block_seq -eq 4) 'spike_block_seq kept'
        Check ($j.hooks -eq $false -and $j.auto_load -eq $false) 'hooks/auto_load off'
        $r = Run-Kit 'write-probe-config.ps1' @('-ScratchSlot', 'quicksave')
        Expect-Kit $r 'write-probe-config bad slot' 1
        Check ((Get-Content $probeCfg -Raw | ConvertFrom-Json).scratch_slot -eq 'save_007') 'a refused slot leaves the config alone'
    }

    Step 'run-block.ps1' {
        $r = Run-Kit 'run-block.ps1' @('ghost_motion', 'a')
        Expect-Kit $r 'run-block ghost_motion' 0 @('spike_block = ghost_motion a \(seq 5\)')
        $j = Get-Content $probeCfg -Raw | ConvertFrom-Json
        Check ($j.spike_block -eq 'ghost_motion a' -and $j.spike_block_seq -eq 5) 'block + args written, sequence bumped'
        $r = Run-Kit 'run-block.ps1' @('teams_product', 'minhull=50')
        Expect-Kit $r 'run-block teams_product' 0
        $j = Get-Content $probeCfg -Raw | ConvertFrom-Json
        Check ($j.spike_block -eq 'teams_product minhull=50' -and $j.spike_block_seq -eq 6) 'spike (Lua) block with k=v argument'
        $r = Run-Kit 'run-block.ps1' @('nonsense_block')
        Expect-Kit $r 'run-block unknown' 0 @('not one of the session-4 blocks')
        $r = Run-Kit 'run-block.ps1' @('reloadui')
        Expect-Kit $r 'run-block reloadui' 0
        Check ((Get-Content $probeCfg -Raw | ConvertFrom-Json).reloadui_after_s -eq 5) 'reloadui_after_s = 5'
        $null = Run-Kit 'write-probe-config.ps1' @('-ClearScratch')
        $r = Run-Kit 'run-block.ps1' @('takeover')
        Expect-Kit $r 'run-block takeover without scratch slot' 1 @('scratch')
        $null = Run-Kit 'write-probe-config.ps1' @('-ScratchSlot', 'save_007')
        $r = Run-Kit 'run-block.ps1' @('takeover')
        Expect-Kit $r 'run-block takeover with scratch slot' 0
    }

    Step 'collect-logs.ps1 and extract-galaxy-dump.ps1' {
        # a game log with spike lines and a galaxy dump in chunks (same format as ui/x4mp_spike_s13.lua writes)
        $sectors = 1..152 | ForEach-Object { '{{"macro":"cluster_{0:000}_sector001_macro","cluster":"cluster_{0:000}_macro","gates":["cluster_{1:000}_sector001_macro"]}}' -f $_, (($_ % 152) + 1) }
        $json = '{"format":1,"sector_count":152,"sectors":[' + ($sectors -join ',') + "]}`n"
        $flat = $json -replace "`n", '~'
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add('[General Info] 100.00 some engine line')
        $lines.Add('[=ERROR=] 101.00 [X4MP-SPIKE] RUN INFO block=galaxy_dump state=start run=1 args=')
        $n = [math]::Ceiling($flat.Length / 900)
        for ($i = 1; $i -le $n; $i++) { $lines.Add(('[=ERROR=] 102.{0:00} [X4MP-SPIKE] S13.11 DATA part={0}/{1} json={2}' -f $i, $n, $flat.Substring(($i - 1) * 900, [math]::Min(900, $flat.Length - ($i - 1) * 900)))) }
        $lines.Add('[=ERROR=] 103.00 [X4MP-PROBE] ghost_spawn ok')
        [IO.File]::WriteAllLines((Join-Path $userDir 'x4mp_s4.log'), $lines)
        New-Item -ItemType Directory -Force (Join-Path $userDir 'x4native\x4mp_probe') | Out-Null
        Set-Content (Join-Path $userDir 'x4native\x4mp_probe\x4mp_probe.log') 'probe log line' -Encoding ASCII
        Set-Content (Join-Path $cfgDir 'launch.json') '{"secret":"token"}' -Encoding ASCII
        $j = Get-Content $probeCfg -Raw | ConvertFrom-Json
        $j | Add-Member -NotePropertyName password -NotePropertyValue 'hunter2' -Force
        ($j | ConvertTo-Json) | Set-Content $probeCfg -Encoding UTF8

        $r = Run-Kit 'collect-logs.ps1' @('-Label', 's0')
        Expect-Kit $r 'collect-logs' 0 @('Created', 'rebuilt from the game log: 152 sectors \(>= 140\)')
        $zip = Get-ChildItem $outDir -Filter 'logs-s0-*.zip' | Select-Object -First 1
        Check ($null -ne $zip) 'zip logs-s0-*.zip created'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $za = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
        try {
            $names = @($za.Entries | ForEach-Object { $_.FullName -replace '\\', '/' })
            foreach ($want in 'x4mp_s4.log', 'spike-lines.txt', 'galaxy-dump.json', 'x4mp/x4mp_probe.json') { Check ($names -contains $want) "zip contains $want" }
            Check (@($names | Where-Object { $_ -match 'x4native.x4mp_probe.x4mp_probe\.log' }).Count -eq 1) 'zip contains the probe log'
            Check (@($names | Where-Object { $_ -match 'launch\.json|\.xml\.gz|password' }).Count -eq 0) 'no launch.json, saves or password files in the zip'
            $e = $za.Entries | Where-Object { ($_.FullName -replace '\\', '/') -eq 'x4mp/x4mp_probe.json' }
            $sr = New-Object IO.StreamReader($e.Open()); $cfgText = $sr.ReadToEnd(); $sr.Dispose()
            Check ($cfgText -notmatch 'hunter2' -and $cfgText -match 'blanked') 'password value blanked in the zipped probe config'
        }
        finally { $za.Dispose() }
        $g = Get-Content (Join-Path $outDir 'galaxy-dump.json') -Raw | ConvertFrom-Json
        Check (@($g.sectors).Count -eq 152) 'out\session4\galaxy-dump.json has 152 sectors'
        Remove-Item (Join-Path $outDir 'galaxy-dump.json') -Force
        $r = Run-Kit 'extract-galaxy-dump.ps1' @()
        Expect-Kit $r 'extract-galaxy-dump' 0 @('152 sectors')
        Check (Test-Path (Join-Path $outDir 'galaxy-dump.json')) 'extract-galaxy-dump wrote the file'
        # a file written by the spike itself wins over the log
        Set-Content (Join-Path $cfgDir 'galaxy-dump.json') $json -Encoding UTF8
        $r = Run-Kit 'collect-logs.ps1' @('-Label', 's0file')
        Expect-Kit $r 'collect-logs with file' 0 @('written by the spike\): 152 sectors')
        Check ((& $saveHash) -eq $savesBefore) 'save folder untouched by every script'
    }

    Step 'install-spike.ps1 -Restore' {
        $r = Run-Kit 'install-spike.ps1' ($x4 + @('-Restore', '-Force'))
        Expect-Kit $r 'install-spike -Restore' 0 @('restored')
        foreach ($n in 'x4mp_probe', 'x4mp_spike') { Check (-not (Test-Path (Join-Path $ext $n))) "$n removed" }
        Check (Test-Path (Join-Path $ext 'x4mp\ui\x4mp_menu.lua')) 'product x4mp is back'
        Check (Test-Path (Join-Path $ext 'x4native')) 'x4native stays for the product'
        Check ((Get-Content (Join-Path $userDir 'content.xml') -Raw) -match 'id="x4mp" enabled="true"') 'product enabled again in content.xml'
        Check (-not (Test-Path (Join-Path $outDir 'parked\x4mp'))) 'nothing left parked'
        $r = Run-Kit 'install-spike.ps1' ($x4 + @('-Restore', '-Force'))
        Expect-Kit $r 'install-spike -Restore (second run)' 0 @('no parked product')
        Check ((& $saveHash) -eq $savesBefore) 'save folder untouched at the end'
    }

    # =====================================================================================================================================
    # Sittings 1-3 (M3-14): the topology, install, log and report scripts
    # =====================================================================================================================================
    $galaxyFixture = Join-Path $repo 'server\tests\X4MP.FakeNode.Tests\Fixtures\galaxy-dump-small.json'
    $saveScanFixtures = Join-Path $repo 'tools\X4MP.SaveScan.Tests\fixtures'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    function Get-ZipNames([string]$zipPath) {
        $za = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try { return @($za.Entries | ForEach-Object { $_.FullName -replace '\\', '/' }) } finally { $za.Dispose() }
    }
    function Read-ZipText([string]$zipPath, [string]$entryName) {
        $za = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $e = $za.Entries | Where-Object { ($_.FullName -replace '\\', '/') -eq $entryName } | Select-Object -First 1
            if (-not $e) { return $null }
            $sr = New-Object IO.StreamReader($e.Open()); try { return $sr.ReadToEnd() } finally { $sr.Dispose() }
        }
        finally { $za.Dispose() }
    }
    # A mod log in the real formats (mod/native/core/ghost/sync_stats.cpp, features/ghosts/ghost_feature.cpp, features/stats/stats_feature.cpp, host/mod_host.cpp):
    # 2 ghosts x 8 windows of 5 s. -Bad makes the path error, the frame cost and the rates exceed every limit.
    function New-SyntheticModLog([string]$path, [switch]$Bad) {
        $lines = New-Object System.Collections.Generic.List[string]
        $t = [datetime]::new(2026, 10, 4, 12, 0, 0, [DateTimeKind]::Utc)
        $inv = [Globalization.CultureInfo]::InvariantCulture
        for ($w = 0; $w -lt 8; $w++) {
            $t = $t.AddSeconds(5)
            $ts = $t.ToString('yyyy-MM-dd HH:mm:ss.fff', $inv) + 'Z'
            foreach ($who in 'Wing01', 'Wing02') {
                $p95 = if ($Bad) { '80.00' } else { '2.50' }
                $lines.Add("$ts [INFO] [ghost] [sync] player=$who net=7 frames=295 err_p50/p95/max=0.80/$p95/6.00 m steady_p95=$p95 fast_p95=0.00 lat_p95=170 ms speed_max=250 snaps=0 extrap=0 held=0 delay_ms=150 newest_age_ms=60")
            }
            $lines.Add("$ts [INFO] [ghost] [sync] ghosts tracked=2 visible=2 spawned=2 respawned=0 hidden=0 removed=0 adopted=0 hints=100 rep_msgs=$($w * 100) rep_entries=$($w * 500) frame_p95_us=30 frame_max_us=60 clk_ms=-1000 bias_ms=30 rep_bad=0 spawn_failed=0 unmapped=0 unresolved=0")
            $mod = if ($Bad) { '0.900' } else { '0.090' }
            $rate = if ($Bad) { 40000 } else { 3000 }
            $lines.Add("$ts [INFO] [perf] stats: fps=60.0 frame_p95=16.00ms mod_p95=${mod}ms mod_max=0.300ms connected=1 rtt=0.5ms rx=${rate}B/s tx=1500B/s node_stats_sent=$w")
            $lines.Add("$ts [INFO] [perf] frames=300 p50=30us p95=90us avg=40us max=300us over_budget=0 budget=1500us features_disabled=0 guard_ids=2 remove_blocked_by_guard=0 main_thread_violations=0 log_dropped=0")
        }
        [IO.File]::WriteAllLines($path, $lines)
    }

    Step 'sittings 1-3: -WhatIf of every script (and what it prints)' {
        $dlcX4 = @('-X4Dir', "`"$fakeX4`"")
        # a fake DLC in the fake install so the DLC list the bots report is checked too
        New-Item -ItemType Directory -Force (Join-Path $ext 'ego_dlc_split') | Out-Null
        Set-Content (Join-Path $ext 'ego_dlc_split\content.xml') '<content id="ego_dlc_split" name="Split Vendetta" version="900"/>' -Encoding ASCII
        $r = Run-Kit 'start-fake-authority.ps1' (@('-SaveName', 'save_007', '-Wingmen', '2', '-GalaxyFile', "`"$galaxyFixture`"", '-JoinPassword', 'Testpw-314159', '-FreshDownload', '-WhatIf') + $dlcX4)
        Expect-Kit $r 'start-fake-authority -Wingmen -WhatIf' 0 @('swarm --with-authority', '--wingman Tester', '--clients 2', '--name-prefix Wing', '--host-stand-pos 25000,0,-20000', '--galaxy-file', '--password Testpw-314159', 'ego_dlc_split@9.00', 'FreshDownload')
        $r = Run-Kit 'start-fake-authority.ps1' (@('-SaveName', 'save_007', '-WhatIf') + $dlcX4)
        Expect-Kit $r 'start-fake-authority (no wingmen) -WhatIf' 0 @('FakeNode\s+: .* authority --server', 'Wingmen\s+: none', 'galaxy-dump\.json')
        Expect-Kit (Run-Kit 'start-fake-authority.ps1' @('-List')) 'start-fake-authority -List' 0 @('save_007')
        Expect-Kit (Run-Kit 'start-fake-authority.ps1' @('-SaveName', 'save_007', '-Wingmen', '9', '-WhatIf')) 'start-fake-authority -Wingmen 9' 1 @('Wingmen must be')
        $r = Run-Kit 'start-fake-clients.ps1' (@('-Wingmen', '3', '-Target', 'Pilot', '-GalaxyFile', "`"$galaxyFixture`"", '-SaveName', 'save_007', '-WhatIf') + $dlcX4)
        Expect-Kit $r 'start-fake-clients -WhatIf' 0 @('swarm', '--wingman Pilot', '--clients 3', '--avatar-timeout 90', '--chat-echo', "In-game admin password", 'uploaded from save_007')
        Expect-Kit (Run-Kit 'start-fake-clients.ps1' @('-Wingmen', '0', '-WhatIf')) 'start-fake-clients -Wingmen 0' 1 @('Wingmen must be')
        $r = Run-Kit 'start-server-lan.ps1' @('-SaveName', 'save_007', '-JoinPassword', 'Testpw-314159', '-WhatIf')
        Expect-Kit $r 'start-server-lan -WhatIf' 0 @('PC B joins', 'Windows Defender Firewall', 'ALL interfaces', 'join password: set', 'Disable-NetFirewallRule')
        Expect-Kit (Run-Kit 'upload-save.ps1' @('-SaveName', 'save_007', '-WhatIf')) 'upload-save -WhatIf' 0 @('Plan')
        Expect-Kit (Run-Kit 'make-client-kit.ps1' @('-WhatIf')) 'make-client-kit -WhatIf' 0 @('tools\\session4\\install.ps1', 'START-HERE-PC-B.txt', 'x4mp\.dll')
        Expect-Kit (Run-Kit 'install.ps1' (@('-WhatIf') + $dlcX4)) 'install -WhatIf' 0 @('Would run')
        Expect-Kit (Run-Kit 'uninstall.ps1' (@('-WhatIf') + $dlcX4)) 'uninstall -WhatIf'
        Expect-Kit (Run-Kit 'sync-report.ps1' @('-Log', (Join-Path $tmp 'nonexistent.log'))) 'sync-report: missing log' 2 @('log not found')
        Expect-Kit (Run-Kit 'savescan.ps1' @('-Save', (Join-Path $tmp 'nonexistent.xml.gz'))) 'savescan: missing save' 2 @('save not found')
        Check (-not (Test-Path (Join-Path $outDir 'data'))) 'no server data folder was created by a -WhatIf run'
        Check ((& $saveHash) -eq $savesBefore) 'save folder untouched by the -WhatIf runs'
        # the DLC report the bots send (a real run writes the file; -WhatIf only names it): exactly the enabled DLCs, "9.00"
        $chk = Join-Path $tmp 'dlc_check.ps1'
        Set-Content $chk @"
`$ErrorActionPreference = 'Stop'
. '$(Join-Path $s4 'common.ps1')'
. '$(Join-Path $s4 'topology.ps1')'
`$r = New-DlcExtensionsFile '$fakeX4' `$null 'dlc-check.json'
if (-not `$r.File) { throw 'no DLC file' }
(Get-Content `$r.File -Raw) | Set-Content '$(Join-Path $tmp 'dlc-check-copy.json')'
'ok'
"@
        $o = Join-Path $tmp 'dlc_check.txt'
        $p = Start-Process $powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$chk`"") -PassThru -Wait -NoNewWindow -RedirectStandardOutput $o -RedirectStandardError ($o + '.err')
        $null = $p.Handle
        Check ($p.ExitCode -eq 0) 'New-DlcExtensionsFile runs'
        $dlcJson = Get-Content (Join-Path $tmp 'dlc-check-copy.json') -Raw | ConvertFrom-Json
        Check (@($dlcJson).Count -eq 1 -and @($dlcJson)[0].id -eq 'ego_dlc_split' -and @($dlcJson)[0].version -eq '9.00') 'the bots report the enabled DLC of the install as ego_dlc_split 9.00'
        Remove-Item (Join-Path $ext 'ego_dlc_split') -Recurse -Force   # the topology part below runs without DLCs (the hostsim client reports none)
    }

    Step 'start-server-lan.ps1 -Check: addresses, firewall status and commands; it changes nothing' {
        $r = Run-Kit 'start-server-lan.ps1' @('-Check')
        Expect-Kit $r 'start-server-lan -Check' 0 @('PC B joins: \d+\.\d+\.\d+\.\d+:47780', 'TCP 47780', 'UDP 47781', 'TCP 47790', 'New-NetFirewallRule|firewall', 'Disable-NetFirewallRule -DisplayName ''X4MP game UDP''', 'Enable-NetFirewallRule -DisplayName ''X4MP game UDP''')
        if ($r.Text -match '(?m)^\s*New-NetFirewallRule') { Check ($r.Text -match '-RemoteAddress LocalSubnet') 'the printed rule is limited to the local subnet' }
        Check ($null -eq (Get-NetFirewallRule -DisplayName 'X4MP game TCP', 'X4MP game UDP', 'X4MP admin HTTP' -ErrorAction SilentlyContinue)) 'the script created no firewall rule'
        Check (-not (Test-Path (Join-Path $outDir 'data'))) '-Check started no server'
    }

    Step 'sync-report.ps1: PASS / FAIL on synthetic logs in the real formats, from a file and from a zip' {
        $good = Join-Path $tmp 'good.log'; $bad = Join-Path $tmp 'bad.log'
        New-SyntheticModLog $good; New-SyntheticModLog $bad -Bad
        $r = Run-Kit 'sync-report.ps1' @('-Log', "`"$good`"", '-Strict', '-Json', "`"$(Join-Path $tmp 'good.json')`"")
        Expect-Kit $r 'sync-report good' 0 @('Wing01', 'Wing02', 'PASS\s+path error p95', 'PASS\s+mod main-thread', 'PASS\s+download rate', '0 FAIL')
        $j = Get-Content (Join-Path $tmp 'good.json') -Raw | ConvertFrom-Json
        Check ($j.modP95Ms -lt 0.2) 'JSON: mod p95 < 0.2 ms'
        $r = Run-Kit 'sync-report.ps1' @('-Log', "`"$bad`"", '-Strict')
        Expect-Kit $r 'sync-report bad -Strict' 1 @('FAIL\s+path error p95', 'FAIL\s+mod main-thread', 'FAIL\s+download rate')
        $r = Run-Kit 'sync-report.ps1' @('-Log', "`"$bad`"")
        Expect-Kit $r 'sync-report bad (not strict)' 0 @('FAIL\s+path error p95')
        $zipPath = Join-Path $tmp 'logs-test.zip'
        $zs = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
        try { $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zs, $good, 'x4mp/logs/x4mp.log') } finally { $zs.Dispose() }
        Expect-Kit (Run-Kit 'sync-report.ps1' @('-Zip', "`"$zipPath`"", '-Strict')) 'sync-report -Zip' 0 @('Wing01', '0 FAIL')
    }

    Step 'collect-logs.ps1, product mode: mod lines, sync report, mod files, no launch.json' {
        New-Item -ItemType Directory -Force (Join-Path $cfgDir 'logs') | Out-Null
        New-SyntheticModLog (Join-Path $cfgDir 'logs\x4mp.log')
        Add-Content (Join-Path $cfgDir 'logs\x4mp.log') '2026-10-04 12:01:00.000Z [INFO] [ghost] takeover: the guard confirmed (10 frames, 0.29 s after the request started): player in ship 400001, net_id 8; 1 local copies to remove'
        Add-Content (Join-Path $cfgDir 'logs\x4mp.log') '2026-10-04 12:01:00.100Z [INFO] [ghost] janitor: swept: 0 leftover ''[MP] '' object(s) removed, 0 refused, among 1 scanned (0 carry the prefix); kept: ghosts 0, avatars 0, own copy 0, guarded 0; 1 pass(es)'
        Add-Content (Join-Path $cfgDir 'logs\x4mp.log') '2026-10-04 12:01:00.200Z [INFO] [md] unrelated line that is not interesting'
        Set-Content (Join-Path $cfgDir 'x4mp.json') '{"last_address":"127.0.0.1:47780","last_name":"Tester"}' -Encoding ASCII
        Set-Content (Join-Path $cfgDir 'avatar-records.txt') 'v1' -Encoding ASCII
        Set-Content (Join-Path $cfgDir 'launch.json') '{"password":"must-not-be-zipped"}' -Encoding ASCII
        Remove-Item (Join-Path $outDir 'logs-s1-*.zip') -ErrorAction SilentlyContinue
        $r = Run-Kit 'collect-logs.ps1' @('-Label', 's1')
        Expect-Kit $r 'collect-logs product mode' 0 @('Created', 'x4mp-lines.txt: \d+ lines of the mod log')
        $zip = Get-ChildItem $outDir -Filter 'logs-s1-*.zip' | Select-Object -First 1
        Check ($null -ne $zip) 'zip logs-s1-*.zip created'
        $names = Get-ZipNames $zip.FullName
        foreach ($want in 'x4mp-lines.txt', 'sync-report.txt', 'x4mp/x4mp.json', 'x4mp/avatar-records.txt', 'x4mp/logs/x4mp.log') { Check ($names -contains $want) "zip contains $want" }
        Check (@($names | Where-Object { $_ -match 'launch\.json|\.xml\.gz|password' }).Count -eq 0) 'no launch.json, saves or password files in the zip'
        $lines = Read-ZipText $zip.FullName 'x4mp-lines.txt'
        Check ($lines -match '\[sync\] player=Wing01' -and $lines -match 'takeover: the guard confirmed' -and $lines -match 'janitor: swept' -and $lines -notmatch 'unrelated line') 'x4mp-lines.txt keeps the interesting lines only'
        Check ((Read-ZipText $zip.FullName 'sync-report.txt') -match 'Summary: \d+ PASS') 'sync-report.txt holds the report'
        Remove-Item (Join-Path $cfgDir 'launch.json') -Force
    }

    Step 'savescan.ps1 on the synthetic save fixtures (through the kit script)' {
        foreach ($f in 'clean', 'client-quicksave') {
            $src = Join-Path $saveScanFixtures "$f.xml"
            $dst = Join-Path $tmp "$f.xml.gz"
            $in = [IO.File]::OpenRead($src); $outFs = [IO.File]::Create($dst); $gz = New-Object IO.Compression.GZipStream($outFs, [IO.Compression.CompressionMode]::Compress)
            try { $in.CopyTo($gz) } finally { $gz.Dispose(); $outFs.Dispose(); $in.Dispose() }
        }
        Expect-Kit (Run-Kit 'savescan.ps1' @('-Save', "`"$(Join-Path $tmp 'clean.xml.gz')`"", '-Role', 'Any')) 'savescan clean' 0
        Expect-Kit (Run-Kit 'savescan.ps1' @('-Save', "`"$(Join-Path $tmp 'client-quicksave.xml.gz')`"", '-Role', 'Client', '-OwnAvatar', 'AVA-001')) 'savescan client quicksave (leftovers)' 1
    }

    Step 'install.ps1 / uninstall.ps1 (product): refuses next to the sitting-0 kit, deploys, enables, removes; saves untouched' {
        # the sitting-0 kit is installed again (stub probe dll), the product is parked: install.ps1 must refuse
        Expect-Kit (Run-Kit 'install-spike.ps1' ($x4 + @('-ProbeDll', "`"$fakeDll`"", '-Force'))) 'install-spike (again)' 0
        Expect-Kit (Run-Kit 'install.ps1' ($x4 + @('-Force'))) 'install.ps1 next to the kit' 1 @('Refusing')
        $r = Run-Kit 'install.ps1' ($x4 + @('-RemoveTestExtensions', '-Force'))
        Expect-Kit $r 'install.ps1 -RemoveTestExtensions -Force' 0 @('Removed', 'Deployed', 'Protected UI Mode OFF', 'x4mp_s4\.log')
        foreach ($gone in 'x4mp_probe', 'x4mp_spike') { Check (-not (Test-Path (Join-Path $ext $gone))) "$gone removed" }
        foreach ($there in 'x4mp\native\x4mp.dll', 'x4mp\content.xml', 'x4mp\ui.xml', 'x4native\content.xml') { Check (Test-Path (Join-Path $ext $there)) "installed: $there" }
        $c = Get-Content (Join-Path $userDir 'content.xml') -Raw
        Check ($c -match 'id="x4mp" enabled="true"') 'x4mp enabled in content.xml'
        Check ($c -match 'id="x4mp_probe" enabled="false"' -or $c -notmatch 'x4mp_probe') 'the probe is not enabled'
        Check (Test-Path (Join-Path $outDir 'parked\x4mp')) 'the stale parked product is reported, not deleted (the script warned)'
        Check ($r.Text -match 'still parked') 'install.ps1 warned about the parked product'
        Remove-Item (Join-Path $outDir 'parked') -Recurse -Force
        $r = Run-Kit 'uninstall.ps1' $x4
        Expect-Kit $r 'uninstall.ps1' 0 @('Removed', 'never deletes saves')
        foreach ($gone in 'x4mp', 'x4native') { Check (-not (Test-Path (Join-Path $ext $gone))) "$gone removed by uninstall" }
        Check ((& $saveHash) -eq $savesBefore) 'save folder untouched by install / uninstall'
    }

    Step 'make-client-kit.ps1: the PC B zip is self-contained (no repo, no build tools), no secrets, no personal paths' {
        Remove-Item (Join-Path $outDir 'x4mp-client-kit-*.zip') -ErrorAction SilentlyContinue
        $r = Run-Kit 'make-client-kit.ps1' @()
        Expect-Kit $r 'make-client-kit' 0 @('Created .*x4mp-client-kit-.*\.zip', 'files')
        $kit = Get-ChildItem $outDir -Filter 'x4mp-client-kit-*.zip' | Select-Object -First 1
        Check ($null -ne $kit) 'kit zip created'
        $names = Get-ZipNames $kit.FullName
        foreach ($want in 'START-HERE-PC-B.txt', 'tools/session4/install.ps1', 'tools/session4/uninstall.ps1', 'tools/session4/collect-logs.ps1', 'tools/session4/sync-report.ps1', 'tools/session4/common.ps1',
            'tools/session3/common.ps1', 'tools/session2/common.ps1', 'mod/tools/deploy.ps1', 'mod/tools/check-packaging.ps1', 'mod/extension/x4mp/content.xml', 'mod/build/msvc-x64-relwithdebinfo/x4mp.dll', 'docs/in-game-session-4.md') {
            Check ($names -contains $want) "kit contains $want"
        }
        Check (@($names | Where-Object { $_ -like 'mod/third_party/x4native/*/x4native/native/x4native_64.dll' }).Count -eq 1) 'kit contains the vendored X4Native runtime'
        Check (@($names | Where-Object { $_ -match 'launch\.json|\.pdb$|\.xml\.gz$|password|\.db$|^out/|^server/|^\.git' }).Count -eq 0) 'no secrets, saves, pdb, server or git files in the kit'
        # unzip somewhere else and run the kit's own install.ps1 from there: it must work without the repo
        $pcb = Join-Path $tmp 'pc-b'
        [IO.Compression.ZipFile]::ExtractToDirectory($kit.FullName, $pcb)
        $x4b = Join-Path $tmp 'X4 PC B'
        New-Item -ItemType Directory -Force (Join-Path $x4b 'extensions') | Out-Null
        Set-Content (Join-Path $x4b 'X4.exe') 'stub' -Encoding ASCII
        $text = Get-ChildItem $pcb -Recurse -File | Where-Object { $_.Extension -in '.ps1', '.txt', '.md' } | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }
        Check (-not (($text -join "`n").Contains($repo))) 'no repo path inside the kit scripts and texts'
        $env:X4MP_S4_OUT_DIR = Join-Path $pcb 'out\session4'
        try {
            $f = Join-Path $pcb 'tools\session4\install.ps1'
            $o = Join-Path $tmp 'pcb-install.txt'
            $p = Start-Process $powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$f`"", '-X4Dir', "`"$x4b`"", '-Force') -PassThru -Wait -NoNewWindow -RedirectStandardOutput $o -RedirectStandardError ($o + '.err')
            $null = $p.Handle
            $txt = (Get-Content $o -Raw) + (Get-Content ($o + '.err') -Raw)
            Check ($p.ExitCode -eq 0 -and $txt -match 'Deployed') "the kit's install.ps1 runs on PC B without the repo ($($p.ExitCode))"
            if ($p.ExitCode -ne 0) { Write-Host $txt }
            foreach ($there in 'x4mp\native\x4mp.dll', 'x4native\content.xml') { Check (Test-Path (Join-Path $x4b "extensions\$there")) "PC B: installed $there" }
            $f2 = Join-Path $pcb 'tools\session4\collect-logs.ps1'
            $p = Start-Process $powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$f2`"", '-Label', 's3b') -PassThru -Wait -NoNewWindow -RedirectStandardOutput $o -RedirectStandardError ($o + '.err')
            $null = $p.Handle
            Check ($p.ExitCode -eq 0 -and @(Get-ChildItem (Join-Path $pcb 'out\session4') -Filter 'logs-s3b-*.zip').Count -eq 1) "the kit's collect-logs.ps1 runs on PC B"
        }
        finally { $env:X4MP_S4_OUT_DIR = $outDir }
    }
    Check ((& $saveHash) -eq $savesBefore) 'save folder untouched by every sittings 1-3 kit script'
  }

  if ($runTopology) {
    # =====================================================================================================================================
    # Sittings 1-3 end to end (M3-14): the topology scripts as the user runs them, the real x4mp.dll in x4mp-hostsim as "the real X4".
    # Needs mod\build.ps1 and tools\e2e.ps1 -Steps Publish. Ports 47977-47979 (the kit scripts take them through -TcpPort / -UdpPort / -HttpPort).
    # =====================================================================================================================================
    $build = Join-Path $repo "mod\build\msvc-x64-$Config"
    $hostSim = Join-Path $build 'x4mp-hostsim.exe'
    $dll = Join-Path $build 'x4mp.dll'
    foreach ($f in $hostSim, $dll) { if (-not (Test-Path $f)) { throw "Missing $f (run mod\build.ps1 first)" } }
    $galaxyFixture = Join-Path $repo 'server\tests\X4MP.FakeNode.Tests\Fixtures\galaxy-dump-small.json'
    $portArgs = @('-TcpPort', "$TcpPort", '-UdpPort', "$UdpPort", '-HttpPort', "$HttpPort")
    $nodePw = 'x4mp-host-test'
    $procs = New-Object System.Collections.Generic.List[object]
    function Stop-All {
        foreach ($p in $procs) { if (-not $p.HasExited) { try { & taskkill /PID $p.Id /T /F 2>$null | Out-Null } catch { } } }
        $procs.Clear()
        # the kit scripts start the server / FakeNode with Start-Process: killing the script's tree can miss them (session-3 lesson); stop the ones of THIS repo by name
        foreach ($l in @(Get-Process -Name 'x4mp-server', 'X4MP.FakeNode', 'x4mp-hostsim' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$repo*" })) {
            Write-Host "  stopping leftover $($l.Name) $($l.Id)"
            try { Stop-Process -Id $l.Id -Force -ErrorAction SilentlyContinue; Wait-Process -Id $l.Id -Timeout 10 -ErrorAction SilentlyContinue } catch { }
        }
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
    # waits (blocking on the process, no sleeping) until the file holds the pattern the given number of times
    function Wait-File([string]$what, [string]$path, [string]$pattern, $proc, [int]$sec = 180, [int]$count = 1) {
        $end = (Get-Date).AddSeconds($sec)
        while ($true) {
            if (Test-Path $path) { try { if (([regex]::Matches((Read-Shared $path), $pattern)).Count -ge $count) { return } } catch { } }
            if ($proc.HasExited) { throw "$what : the process exited ($($proc.ExitCode)) before '$pattern' appeared in $path" }
            if ((Get-Date) -gt $end) { throw "Timed out waiting for $what ('$pattern' in $path)" }
            Wait-Process -Id $proc.Id -Timeout 1 -ErrorAction SilentlyContinue
        }
    }
    function New-GzSave([string]$path, [int]$seed, [int]$mb = 3) {
        $rnd = New-Object byte[] ($mb * 1MB); (New-Object Random $seed).NextBytes($rnd)
        $fs = [IO.File]::Create($path); $gz = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Compress)
        $head = [Text.Encoding]::UTF8.GetBytes('<?xml version="1.0" encoding="utf-8"?><savegame><info><game id="x4mp-s4-dry"/></info></savegame>')
        $gz.Write($head, 0, $head.Length); $gz.Write($rnd, 0, $rnd.Length); $gz.Dispose(); $fs.Dispose()
    }
    function Run-HostSim([string]$name, [string]$script, [string]$workName, [string[]]$vars, [int]$maxSec = 240) {
        $work = Join-Path $tmp $workName
        $out = Join-Path $tmp "$name.out.txt"
        $a = @('--dll', $dll, '--script', (Join-Path $PSScriptRoot $script), '--work-dir', $work, '--max-seconds', "$maxSec", '--timeout-scale', '2') + $vars
        $t0 = [Diagnostics.Stopwatch]::StartNew()
        $p = Start-P $hostSim $a $out
        if (-not $p.WaitForExit(($maxSec + 30) * 1000)) { & taskkill /PID $p.Id /T /F *> $null; throw "$name did not finish in $($maxSec + 30) s" }
        $p.WaitForExit()
        Get-Content $out -Tail 6 | ForEach-Object { Write-Host "  $_" }
        if ($p.ExitCode -ne 0) { throw "$name failed (exit $($p.ExitCode)); output: $out; mod log: $(Join-Path $work 'extension\logs\x4mp.log')" }
        Write-Host ("  {0} PASSED in {1:N1} s" -f $name, $t0.Elapsed.TotalSeconds) -ForegroundColor Green
    }
    function Reset-KitServer { Remove-Item -Recurse -Force (Join-Path $outDir 'data'), (Join-Path $outDir 'admin-password.txt'), (Join-Path $outDir 'fakenode.log'), (Join-Path $outDir 'fakenode-clients.log') -ErrorAction SilentlyContinue }
    $adminPw = { (Get-Content (Join-Path $outDir 'admin-password.txt') -Raw).Trim() }
    $adminUrl = "http://127.0.0.1:$HttpPort"

    try {
        # the fake machine: a valid gzip save, a fake DLC (the bots must report it), the galaxy dump of "sitting 0"
        New-GzSave (Join-Path $saveDir 'save_004.xml.gz') 4
        Copy-Item $galaxyFixture (Join-Path $outDir 'galaxy-dump.json') -Force
        $ckpt1 = Join-Path $tmp 'ckpt1.xml.gz'; New-GzSave $ckpt1 11
        $ckpt2 = Join-Path $tmp 'ckpt2.xml.gz'; New-GzSave $ckpt2 12
        $sim = Join-Path $PSScriptRoot 'authority_sim.ps1'

        Step 'publish incl. the web GUI (the kit helper)' {
            $cmd = ". '$(Join-Path $s4 'common.ps1')'; `$null = Ensure-Published"
            $p = Start-Process $powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', "`"$cmd`"") -PassThru -Wait -NoNewWindow
            if ($p.ExitCode -ne 0) { throw "Ensure-Published failed ($($p.ExitCode))" }
        }

        Step 'sitting 1: start-fake-authority.ps1 -Wingmen 2 -FreshDownload, the real DLL joins as Tester (takeover, ghosts, chat, reloads)' {
            Reset-KitServer
            $fa = Start-P $powershell (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $s4 'start-fake-authority.ps1'), '-SaveName', 'save_004', '-Wingmen', '2', '-FreshDownload', '-X4Dir', $fakeX4) + $portArgs) (Join-Path $tmp 'fa.out.txt')
            $fakeLog = Join-Path $outDir 'fakenode.log'
            Wait-File 'the FakeNode authority: checkpoint stored' $fakeLog 'checkpoint stored' $fa 300
            Wait-File 'both wingmen have their avatars' $fakeLog 'avatar net_id=' $fa 120 2
            $faOut = Get-Content (Join-Path $tmp 'fa.out.txt') -Raw
            Check ($faOut -match 'Wingmen\s+: 2 bots \(Wing01\.\.\)' -and $faOut -match "player 'Tester'") 'the script says what the wingmen do'
            Check ($faOut -notmatch [regex]::Escape((& $adminPw))) 'the admin password was not printed'
            Check (([string](Get-Content (Join-Path $tmp 'fa.out.txt.err') -Raw)) -notmatch 'WARNING(?!: No enabled DLC)') 'the script printed no warning (but the missing-DLC note: the fake X4 has none, like the hostsim client)'
            Run-HostSim 's4_sitting1' 'session4_client.hostsim' 'work-s1' @('--var', "tcp=$TcpPort", '--var', 'name=Tester', '--var', 'wing=Wing', '--var', 'reloads=2',
                '--admin-url', $adminUrl, '--admin-user', 'admin', '--admin-password', (& $adminPw)) 300
            Stop-All
        }

        Step 'sitting 2: start-fake-clients.ps1 -Wingmen 2 uploads the save, the real DLL hosts as Tester, the wingmen start when the host ship exists' {
            Reset-KitServer
            $fc = Start-P $powershell (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $s4 'start-fake-clients.ps1'), '-Wingmen', '2', '-SaveName', 'save_004', '-Target', 'Tester', '-X4Dir', $fakeX4) + $portArgs) (Join-Path $tmp 'fc.out.txt')
            Wait-File 'the session created from the upload' (Join-Path $tmp 'fc.out.txt') 'created from the upload and started' $fc 300
            Run-HostSim 's4_sitting2' 'session4_authority.hostsim' 'work-s2' @('--var', "tcp=$TcpPort", '--var', "apw=$nodePw", '--var', "sim=$sim", '--var', "ckpt_src=$ckpt1", '--var', "ckpt_src2=$ckpt2",
                '--var', "admin_url=$adminUrl", '--var', "admin_pw=$(& $adminPw)", '--var', 'name=Tester', '--var', 'wing=Wing', '--var', 'wings=2',
                '--admin-url', $adminUrl, '--admin-user', 'admin', '--admin-password', (& $adminPw)) 420
            $fcOut = Get-Content (Join-Path $tmp 'fc.out.txt') -Raw
            Check ($fcOut -match 'Your ship exists for the session\. Starting 2 wingman bot' ) 'the script waited for the host ship before it started the wingmen'
            Check ($fcOut -notmatch [regex]::Escape((& $adminPw))) 'the admin password was not printed'
            Stop-All
        }

        Step 'sitting 3: start-server-lan.ps1 (binds on all interfaces) + two real DLLs (authority + client) + a bot, through m3_pair_run.ps1 -UseRunningServer' {
            Reset-KitServer
            $sl = Start-P $powershell (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $s4 'start-server-lan.ps1'), '-SaveName', 'save_004') + $portArgs) (Join-Path $tmp 'sl.out.txt')
            Wait-File 'the LAN server and the session' (Join-Path $tmp 'sl.out.txt') 'created from the upload and started' $sl 300
            $listen = @(Get-NetTCPConnection -LocalPort $TcpPort -State Listen -ErrorAction SilentlyContinue)
            Check ($listen.Count -gt 0 -and @($listen | Where-Object { $_.LocalAddress -in '0.0.0.0', '::' }).Count -gt 0) "the game port $TcpPort listens on all interfaces (LAN)"
            $pair = Start-P $powershell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'm3_pair_run.ps1'), '-UseRunningServer', '-AdminPasswordFile', (Join-Path $outDir 'admin-password.txt'),
                '-NodeAdminPasswordValue', $nodePw, '-Bots', '1', '-Reloads', '1', '-MaxSeconds', '300', '-TcpPort', "$TcpPort", '-UdpPort', "$UdpPort", '-HttpPort', "$HttpPort") (Join-Path $tmp 'pair.out.txt')
            if (-not $pair.WaitForExit(420000)) { throw 'm3_pair_run.ps1 did not finish in 420 s' }
            $pair.WaitForExit()
            Get-Content (Join-Path $tmp 'pair.out.txt') -Tail 12 | ForEach-Object { Write-Host "  $_" }
            Check ($pair.ExitCode -eq 0) 'two real DLLs + a bot through the LAN-bound server: PASS (m3_pair_run.ps1 -UseRunningServer)'
            Stop-All
        }
    }
    finally { Stop-All }
  }
    Write-Host ''
    Write-Host "Session-4 kit dry run ($Part): PASS ($script:checks checks)" -ForegroundColor Green
    $exit = 0
}
catch {
    Write-Host ''
    Write-Host "Session-4 kit dry run ($Part): FAIL: $($_.Exception.Message)" -ForegroundColor Red
    $exit = 1
}
finally {
    Remove-Item Env:\X4MP_S2_DOCS_ROOT, Env:\X4MP_S4_OUT_DIR -ErrorAction SilentlyContinue
    if ($exit -eq 0) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue } else { Write-Host "Temp tree kept: $tmp" }
}
exit $exit

