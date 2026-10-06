<#
.SYNOPSIS
  Session 4: zips the logs into out\session4\logs[-<label>]-<timestamp>.zip. Uploads nothing. Run it EVERY time you quit X4, before starting X4 again.
.DESCRIPTION
  Collects (what exists):
    <game log>                       the X4 -logfile (x4mp_s4.log by default) of the X4 user folder
    spike-lines.txt                  every [X4MP-SPIKE] / [X4MP-PROBE] line of the game log, in order (the quick read for Claude)
    x4native\                        the X4 user folder's x4native folder (probe logs: x4native\x4mp_probe\x4mp_probe.log)
    x4mp\                            the mod's per-machine folder (%LocalAppData%\X4MP, or the extension folder in portable mode; M3-24): logs\x4mp.log;
                                     plus Documents\Egosoft\X4\x4mp\ : x4mp_probe.json (password value blanked), galaxy-dump.json (sitting 0);
                                     launch.json and player.key are NEVER included
    galaxy-dump.json                 if the file is missing there, rebuilt from the S13.11 DATA lines of the game log (also saved to
                                     out\session4\galaxy-dump.json, which the FakeNode --galaxy-file takes in sittings 1-2)
    x4mp-lines.txt, sync-report.txt  (product mode, sittings 1-3) the [sync] / [perf] / takeover: / ghosts: / avatars: / janitor: / selfship: / chat /
                                     warning lines of this PC's x4mp.log, and the output of sync-report.ps1 on that log;
                                     x4mp\x4mp.json, x4mp\avatar-records.txt, x4mp\authority-saves.json (the mod's own files; no password is ever in them)
    server-logs\, fakenode.log, fakenode-clients.log, server.out.log, server.err.log   from out\session4\ (sittings 1-3; absent in sitting 0)
  Never included: saves, initial-admin-password*, anything named *password*, the database, launch.json. Supports -WhatIf.
  X4 overwrites its -logfile (and the x4native logs) each time it starts: collect before the next start. -Label puts a word into the zip
  name so the zips stay apart (logs-<label>-<date-time>.zip); sitting 0 uses -Label s0.
.PARAMETER UserId       The numeric folder under Documents\Egosoft\X4 (only needed when there are several).
.PARAMETER GameLogName  The -logfile name from the Steam launch options (default x4mp_s4.log).
.PARAMETER Label        A word for the zip name, e.g. s0, s1, s2, s3a.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$UserId,
    [string]$GameLogName = '',
    [string]$Label = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
if (-not $GameLogName) { $GameLogName = $GameLogDefault }

$dry = [bool]$WhatIfPreference
$user = Resolve-X4UserDir $UserId -AllowMissing:$dry
$cfgDir = Get-X4MPConfigDir            # M3-24: per-machine (LocalAppData\X4MP or the portable extension folder)
$legacyDir = Get-X4MPLegacyConfigDir   # Documents\Egosoft\X4\x4mp: spike files (galaxy-dump.json) and logs of mods older than M3-24
$items = New-Object System.Collections.Generic.List[object]
function Add-Item([string]$path, [string]$entry) {
    if (Test-Path $path) { $items.Add(@{ Path = $path; Entry = $entry }) } else { Write-Host "  (missing, skipped) $path" }
}

$gameLog = Join-Path $user $GameLogName
Add-Item $gameLog $GameLogName
Add-Item (Join-Path $user 'x4native') 'x4native'
# The mod log: this PC's folder only. A log in the old Documents folder is used only when this PC has none (a mod from before M3-24); on a
# OneDrive-redirected Documents it can belong to another PC, so a warning says so.
$modLogDir = Join-Path $cfgDir 'logs'
if (-not (Test-Path (Join-Path $modLogDir 'x4mp.log')) -and (Test-Path (Join-Path $legacyDir 'logs\x4mp.log'))) {
    Write-Warning "No x4mp.log in $cfgDir; using the OLD location $legacyDir\logs (mod from before M3-24). With a OneDrive-redirected Documents it may be another PC's log."
    $modLogDir = Join-Path $legacyDir 'logs'
}
Add-Item $modLogDir 'x4mp\logs'
Add-Item (Join-Path $legacyDir 'galaxy-dump.json') 'x4mp\galaxy-dump.json'
foreach ($n in 'x4mp.json', 'avatar-records.txt', 'authority-saves.json') { Add-Item (Join-Path $cfgDir $n) ('x4mp\' + $n) }   # product mod: settings (never a password), avatar records, authority bookkeeping
Add-Item (Join-Path $OutDir 'fakenode-clients.log') 'fakenode-clients.log'
Add-Item (Join-Path $OutDir 'data\logs') 'server-logs'
Add-Item (Join-Path $OutDir 'fakenode.log') 'fakenode.log'
Add-Item (Join-Path $OutDir 'server.out.log') 'server.out.log'
Add-Item (Join-Path $OutDir 'server.err.log') 'server.err.log'
$probeCfg = Get-ProbeConfigPath

$labelPart = if ($Label) { '-' + ($Label -replace '[^A-Za-z0-9_.-]', '_') } else { '' }
$zip = Join-Path $OutDir ('logs{0}-{1}.zip' -f $labelPart, (Get-Date -Format 'yyyyMMdd-HHmmss'))
Write-Host "Zip: $zip"
foreach ($i in $items) { Write-Host "  + $($i.Path)" }
if (Test-Path $probeCfg) { Write-Host "  + $probeCfg (password value blanked)" }

function Copy-Shared([string]$from, [string]$to) {
    # copy first (not zip in place): the game or server may still hold the file open
    $src = [IO.File]::Open($from, 'Open', 'Read', 'ReadWrite')
    try { $dst = [IO.File]::Create($to); try { $src.CopyTo($dst) } finally { $dst.Dispose() } } finally { $src.Dispose() }
}

if ($PSCmdlet.ShouldProcess($zip, 'Create log archive')) {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-logs-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $stage | Out-Null
    try {
        foreach ($i in $items) {
            if ((Get-Item $i.Path).PSIsContainer) {
                New-Item -ItemType Directory -Force (Join-Path $stage $i.Entry) | Out-Null
                foreach ($f in Get-ChildItem $i.Path -Recurse -File) {
                    $rel = $f.FullName.Substring($i.Path.Length).TrimStart('\')
                    $target = Join-Path (Join-Path $stage $i.Entry) $rel
                    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
                    Copy-Shared $f.FullName $target
                }
            }
            else {
                $target = Join-Path $stage $i.Entry
                New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
                Copy-Shared $i.Path $target
            }
        }
        # the probe config: keep it (it names the scratch slot and the last block) but never a password value
        if (Test-Path $probeCfg) {
            New-Item -ItemType Directory -Force (Join-Path $stage 'x4mp') | Out-Null
            $obj = Get-Content $probeCfg -Raw | ConvertFrom-Json
            foreach ($p in @($obj.PSObject.Properties)) { if ($p.Name -match 'password|token|secret') { $p.Value = '<blanked>' } }
            ($obj | ConvertTo-Json -Depth 4) | Set-Content -Path (Join-Path $stage 'x4mp\x4mp_probe.json') -Encoding UTF8
        }
        # every spike / probe line of the game log in one small file
        if (Test-Path $gameLog) {
            $lines = New-Object System.Collections.Generic.List[string]
            $fs = [IO.File]::Open($gameLog, 'Open', 'Read', 'ReadWrite'); $sr = New-Object IO.StreamReader($fs)
            try { while ($null -ne ($l = $sr.ReadLine())) { if ($l -match '\[X4MP-(SPIKE|PROBE)') { $lines.Add($l) } } } finally { $sr.Dispose() }
            [IO.File]::WriteAllLines((Join-Path $stage 'spike-lines.txt'), $lines)
            $withoutData = @($lines | Where-Object { $_ -notmatch 'S13\.11 DATA ' }).Count
            Write-Host ("  spike-lines.txt: {0} lines ({1} without the galaxy DATA chunks)" -f $lines.Count, $withoutData)
            if ($lines.Count -eq 0) { Write-Host "  (no [X4MP-SPIKE] / [X4MP-PROBE] lines in ${GameLogName}: normal in sittings 1-3, where the product mod runs; in sitting 0 it means the launch option -debug all -logfile $GameLogName is missing or the kit was not active)" }
        }
        # product mode (sittings 1-3): the lines that matter of the mod log, in order, plus the sync-report summary (docs\in-game-session-4.md)
        $modLog = Join-Path $modLogDir 'x4mp.log'
        if (Test-Path $modLog) {
            $keep = New-Object System.Collections.Generic.List[string]
            $fs = [IO.File]::Open($modLog, 'Open', 'Read', 'ReadWrite'); $sr = New-Object IO.StreamReader($fs)
            try { while ($null -ne ($l = $sr.ReadLine())) { if ($l -match '\[sync\]|\[perf\]|takeover:|ghosts:|avatars:|janitor:|selfship:|\[chat\]|chat:|SELFTEST|teams?:|seta|SETA|udp|UDP|\[WARN\]|\[ERROR\]') { $keep.Add($l) } } } finally { $sr.Dispose() }
            [IO.File]::WriteAllLines((Join-Path $stage 'x4mp-lines.txt'), $keep)
            Write-Host ("  x4mp-lines.txt: {0} lines of the mod log" -f $keep.Count)
            try {
                $report = & (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'sync-report.ps1') -Log $modLog 2>&1
                [IO.File]::WriteAllLines((Join-Path $stage 'sync-report.txt'), [string[]]@($report | ForEach-Object { "$_" }))
            }
            catch { Write-Warning "sync-report could not run: $($_.Exception.Message)" }
        }
        # galaxy dump: file written by the spike, else rebuilt from the log chunks
        $dumpFile = Join-Path $legacyDir 'galaxy-dump.json'
        if (-not (Test-Path $dumpFile)) {
            $json = Convert-GalaxyDumpFromLog $gameLog
            if ($json) {
                $check = Test-GalaxyDumpJson $json
                [IO.File]::WriteAllText((Join-Path $stage 'galaxy-dump.json'), $json)
                $savedTo = Join-Path $OutDir 'galaxy-dump.json'
                [IO.File]::WriteAllText($savedTo, $json)
                Write-Host "  galaxy-dump.json rebuilt from the game log: $($check.Message) -> $savedTo"
            }
        }
        else {
            $check = Test-GalaxyDumpJson (Get-Content $dumpFile -Raw)
            Write-Host "  galaxy-dump.json (written by the spike): $($check.Message)"
            Copy-Item $dumpFile (Join-Path $OutDir 'galaxy-dump.json') -Force
        }
        Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -match 'password|launch\.json|player\.key' } | Remove-Item -Force
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
    }
    finally { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host ('Created {0} ({1:N1} MB). Nothing was uploaded; tell Claude the file name.' -f $zip, ((Get-Item $zip).Length / 1MB))
}

