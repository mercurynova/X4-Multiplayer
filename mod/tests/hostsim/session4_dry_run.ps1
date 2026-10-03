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
param()
$ErrorActionPreference = 'Stop'
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
    Write-Host ''
    Write-Host "Session-4 sitting-0 kit dry run: PASS ($script:checks checks)" -ForegroundColor Green
    $exit = 0
}
catch {
    Write-Host ''
    Write-Host "Session-4 sitting-0 kit dry run: FAIL: $($_.Exception.Message)" -ForegroundColor Red
    $exit = 1
}
finally {
    Remove-Item Env:\X4MP_S2_DOCS_ROOT, Env:\X4MP_S4_OUT_DIR -ErrorAction SilentlyContinue
    if ($exit -eq 0) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue } else { Write-Host "Temp tree kept: $tmp" }
}
exit $exit

