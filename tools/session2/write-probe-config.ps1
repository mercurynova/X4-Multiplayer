<#
.SYNOPSIS
  Session 2: writes Documents\Egosoft\X4\x4mp\x4mp_probe.json (the probe reads its settings only from this file).
.DESCRIPTION
  Keys written: server, name, password (ONLY when -Password is given; otherwise omitted), auto_load, pin_module,
  spike_block, reloadui_after_s. Asks for server and name when run interactively without parameters. Supports -WhatIf.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Server,
    [string]$Name,
    [string]$Password,
    [bool]$AutoLoad = $true
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
if (-not $cfg.Contains('pin_module')) { $cfg['pin_module'] = $false }
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
}
