<#
.SYNOPSIS
  Session 4, sitting 0: starts a spike block by editing x4mp_probe.json; the probe notices the change within seconds.
.DESCRIPTION
  run-block.ps1 <block> [args...]   sets spike_block to "<block> <args>" and bumps spike_block_seq (so the same block can be re-run).
                                    The probe handles its native blocks itself and raises every other name as the Lua event
                                    x4mp_spike.run (the spike's Lua accepts "name k=v k=v" and "name;k=v;k=v").
  Native blocks (x4mp_probe):  ghost_spawn, ghost_motion a|b|c, ghost_xsector, sample, seat,
                               takeover, takeover_docked, persist_spawn, persist_check, seta, pause_move, cleanup, s13_check, s13_stop, s13_status (parameters: see the probe README, S13 blocks)
  Spike blocks (Lua/MD):       dress, velocity, seta_off, seta_watch, teams_product, teams_report, teams_end, md_table,
                               galaxy_dump, chat, list, ping           (arguments: docs\in-game-session-4.md, Sitting 0)
  takeover, takeover_docked and persist_spawn change the game (spawn, move you, remove a ship) and only run on the
  scratch save: this script refuses when no scratch_slot is configured (write-probe-config.ps1 -ScratchSlot save_007); the probe checks
  the loaded save's name again.
  run-block.ps1 reloadui              sets reloadui_after_s=5 (the probe triggers the UI reload about 5 s later and resets it).
  run-block.ps1 clear                 empties spike_block (nothing runs).
  Other keys are kept. Supports -WhatIf. Unknown block names are passed through with a warning (a wrong name shows the
  notification "X4MP spike: unknown block ...").
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\session4\run-block.ps1 ghost_motion a
  powershell -ExecutionPolicy Bypass -File tools\session4\run-block.ps1 teams_product minhull=50
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory, Position = 0)][string]$Block,
    [Parameter(Position = 1, ValueFromRemainingArguments)][string[]]$BlockArgs
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$path = Get-ProbeConfigPath
if (-not (Test-Path $path) -and -not $WhatIfPreference) { throw "No probe config at $path. Run tools\session4\write-probe-config.ps1 -ScratchSlot save_007 first." }
$cfg = Read-ProbeConfig
$name = $Block.ToLowerInvariant()
if ($name -eq 'reloadui') {
    $cfg['reloadui_after_s'] = 5
    $what = 'reloadui_after_s = 5'
}
elseif ($name -eq 'clear') {
    $cfg['spike_block'] = ''
    $what = 'spike_block = (empty)'
}
else {
    if ($ScratchOnlyBlocks -contains $name -and -not $cfg.Contains('scratch_slot') -and -not $WhatIfPreference) {
        throw "Block '$name' only runs on the scratch save: run write-probe-config.ps1 -ScratchSlot save_007 first (and load that slot)."
    }
    if (($NativeBlocks + $LuaBlocks) -notcontains $name) {
        Write-Warning "'$name' is not one of the session-4 blocks ($(( $NativeBlocks + $LuaBlocks) -join ', ')). Passing it through anyway (session-2 blocks still exist)."
    }
    $seq = 0
    if ($cfg.Contains('spike_block_seq')) { $seq = [int]$cfg['spike_block_seq'] }
    $line = (@($name) + @($BlockArgs | Where-Object { $_ })) -join ' '
    $cfg['spike_block'] = $line
    $cfg['spike_block_seq'] = $seq + 1
    $what = "spike_block = $line (seq $($seq + 1))"
}
Write-Host "$path : $what"
if ($PSCmdlet.ShouldProcess($path, "Set $what")) {
    Write-ProbeConfig $cfg
    Write-Host 'Done. Watch the on-screen notifications in X4 (the probe re-reads the file within a few seconds).'
}
