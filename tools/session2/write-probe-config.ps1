<#
.SYNOPSIS
  Session 2: writes Documents\Egosoft\X4\x4mp\x4mp_probe.json (the probe reads its settings only from this file).
.DESCRIPTION
  Keys written: server, name, password (ONLY when -Password is given; otherwise omitted), auto_load, hooks, pin_module,
  skip_autosave, spike_block, reloadui_after_s. Asks for server and name when run interactively without parameters.
  Supports -WhatIf. Every run sets hooks, pin_module and skip_autosave explicitly, so running the script again (without
  the switches) is how you "switch back" to the normal settings. Other keys (spike_block_seq) are kept.
.PARAMETER NoHooks
  Writes hooks=false (the probe installs no GetCurrentGameTime / TriggerAutosave hooks). Needed for the clean B4 reading
  of "was the DLL unloaded on reload?": with hooks on, the probe pins its DLL. The probe reads this key only when
  X4 starts, so quit X4 and start it again after changing it.
.PARAMETER Pin
  Writes pin_module=true (B6b only; also read when X4 starts).
.PARAMETER AutoLoad
  $false: the probe downloads the save but does not load it by itself (Part D8 restarts).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Server,
    [string]$Name,
    [string]$Password,
    [bool]$AutoLoad = $true,
    [switch]$NoHooks,
    [switch]$Pin
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$dry = [bool]$WhatIfPreference
$defaultServer = "127.0.0.1:$($Ports.Tcp)"
if (-not $Server) {
    if ($dry) { $Server = $defaultServer }
    else { $a = Read-Host "Server [$defaultServer]"; $Server = if ($a) { $a } else { $defaultServer } }
}
if (-not $Name) {
    if ($dry) { $Name = 'Tester' }
    else { $a = Read-Host 'Player name [Tester]'; $Name = if ($a) { $a } else { 'Tester' } }
}

$cfg = Read-ProbeConfig
$cfg['server'] = $Server
$cfg['name'] = $Name
if ($Password) { $cfg['password'] = $Password } else { $cfg.Remove('password') }
$cfg['auto_load'] = $AutoLoad
$cfg['hooks'] = (-not $NoHooks)
$cfg['pin_module'] = [bool]$Pin
$cfg['skip_autosave'] = $false
$cfg['spike_block'] = ''
$cfg['reloadui_after_s'] = 0

$path = Get-ProbeConfigPath
Write-Host "Probe config: $path"
$shown = [ordered]@{}
foreach ($k in $cfg.Keys) { $shown[$k] = if ($k -eq 'password') { '<set>' } else { $cfg[$k] } }
Write-Host ($shown | ConvertTo-Json)
if ($PSCmdlet.ShouldProcess($path, 'Write x4mp_probe.json')) {
    Write-ProbeConfig $cfg
    Write-Host 'Written.'
    if ($NoHooks) { Write-Host 'hooks = false: this run is for B4 (clean DLL-unload reading). Quit X4 and start it again for the change to apply.' }
    else { Write-Host 'hooks = true (normal). If X4 is already running, quit it and start it again for a changed hooks/pin setting to apply.' }
}
