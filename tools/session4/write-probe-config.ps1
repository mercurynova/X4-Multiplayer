<#
.SYNOPSIS
  Session 4, sitting 0: writes Documents\Egosoft\X4\x4mp\x4mp_probe.json for the spike run (the probe reads its settings only from this file).
.DESCRIPTION
  Sitting 0 runs on ONE PC without a server: the probe must not connect anywhere, so this script writes
    scratch_slot = <the save slot you name>     the probe refuses the destructive blocks (takeover, takeover_docked, persist_spawn,
                                                persist_check: they spawn, move you and remove ships) on any other save
    auto_load = false, hooks = false, pin_module = false, skip_autosave = false, spike_block = "", reloadui_after_s = 0
  and REMOVES the keys server and password of an earlier session (no stale address, no password left in the file). The probe reads
  hooks / pin_module only when X4 starts: quit X4 and start it again after changing them. Other keys (spike_block_seq) are kept.
  Run it again with -ClearScratch to remove the scratch slot. Supports -WhatIf.
  The scratch slot itself is YOUR job: copy a save you can afford to lose into <Documents>\Egosoft\X4\<id>\save\<slot>.xml.gz first
  (docs\in-game-session-4.md, Sitting 0, "Scratch slot"). This script only checks that the file is there and warns when it is not.
.PARAMETER ScratchSlot  The save slot name without extension, e.g. save_007 (also accepted: 7).
.PARAMETER Name         Player name in the probe config (default Tester; unused in sitting 0).
.PARAMETER UserId       The numeric folder under Documents\Egosoft\X4 (only needed when there are several).
.PARAMETER ClearScratch Remove scratch_slot instead of setting it.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ScratchSlot,
    [string]$Name = 'Tester',
    [string]$UserId,
    [switch]$ClearScratch
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$dry = [bool]$WhatIfPreference
if (-not $ClearScratch) {
    if (-not $ScratchSlot) { throw 'Name the scratch slot: write-probe-config.ps1 -ScratchSlot save_007 (a save you can lose; see the session doc).' }
    if ($ScratchSlot -match '^\d{1,3}$') { $ScratchSlot = 'save_{0:000}' -f [int]$ScratchSlot }
    if ($ScratchSlot -notmatch '^save_\d{3}$') { throw "ScratchSlot must look like save_007 (a numbered slot), not '$ScratchSlot'. Quicksave and autosave slots are never scratch slots." }
}

$cfg = Read-ProbeConfig
$null = $cfg.Remove('server')
$null = $cfg.Remove('password')
$cfg['name'] = $Name
$cfg['auto_load'] = $false
$cfg['hooks'] = $false
$cfg['pin_module'] = $false
$cfg['skip_autosave'] = $false
$cfg['spike_block'] = ''
$cfg['reloadui_after_s'] = 0
if ($ClearScratch) { $null = $cfg.Remove('scratch_slot') } else { $cfg['scratch_slot'] = $ScratchSlot }

$path = Get-ProbeConfigPath
Write-Host "Probe config: $path"
Write-Host ($cfg | ConvertTo-Json)

if (-not $ClearScratch) {
    try {
        $user = Resolve-X4UserDir $UserId -AllowMissing:$dry
        $file = Join-Path $user "save\$ScratchSlot.xml.gz"
        if (Test-Path $file) {
            $f = Get-Item $file
            Write-Host ('Scratch slot file found: {0}  ({1:N1} MB, {2})' -f $f.Name, ($f.Length / 1MB), $f.LastWriteTime)
            if (Test-SteamCloudSaveFolder (Join-Path $user 'save')) {
                Write-Host 'Note: Steam Cloud manages this save folder: it restores deleted saves and may overwrite the slot with the cloud copy at the next X4 start.'
            }
            Write-Host 'Reminder: the probe overwrites this slot (persist_spawn saves into it). Do not use a save you care about.'
        }
        else {
            Write-Warning "No $file yet. Copy a save you can lose into that name before loading it (session doc, Sitting 0, Scratch slot)."
        }
    }
    catch { Write-Warning "Could not check the save folder: $($_.Exception.Message)" }
}

if ($PSCmdlet.ShouldProcess($path, 'Write x4mp_probe.json')) {
    Write-ProbeConfig $cfg
    Write-Host 'Written. Quit X4 and start it again if it is running (hooks and pin_module are read at start).'
}
