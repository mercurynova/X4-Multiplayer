<#
.SYNOPSIS
  Session 2: starts a spike block (or a probe command) by editing x4mp_probe.json; the probe notices the change within seconds.
.DESCRIPTION
  run-block.ps1 <block>   sets spike_block=<block> and bumps spike_block_seq (so the same block can be re-run); the probe raises
                          the x4mp_spike.run event. Block names are those of docs\in-game-session-2.md (saves1, saves1_block,
                          saves2, saves4, clock, money, v12, s9gate, ui, ui_standalone, hud, extensions, links, onfoot1,
                          onfoot2, diplo1..diplo7, hq1..hq8, hq3d); they are not validated here.
  run-block.ps1 reloadui  sets reloadui_after_s=5 (the probe triggers the UI reload about 5 s later and resets it).
  run-block.ps1 pin_on    sets pin_module=true (B6b); pin_off sets it back to false.
  Other keys are kept. Supports -WhatIf.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory, Position = 0)][string]$Block
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$path = Get-ProbeConfigPath
if (-not (Test-Path $path) -and -not $WhatIfPreference) { throw "No probe config at $path. Run tools\session2\write-probe-config.ps1 first." }
$cfg = Read-ProbeConfig
switch ($Block.ToLowerInvariant()) {
    'reloadui' { $cfg['reloadui_after_s'] = 5; $what = 'reloadui_after_s = 5' }
    'pin_on'   { $cfg['pin_module'] = $true;  $what = 'pin_module = true' }
    'pin_off'  { $cfg['pin_module'] = $false; $what = 'pin_module = false' }
    default {
        $seq = 0
        if ($cfg.Contains('spike_block_seq')) { $seq = [int]$cfg['spike_block_seq'] }
        $cfg['spike_block'] = $Block
        $cfg['spike_block_seq'] = $seq + 1
        $what = "spike_block = $Block (seq $($seq + 1))"
    }
}
Write-Host "$path : $what"
if ($PSCmdlet.ShouldProcess($path, "Set $what")) {
    Write-ProbeConfig $cfg
    Write-Host 'Done. Watch the on-screen notifications in X4 (the probe re-reads the file within a few seconds).'
}
