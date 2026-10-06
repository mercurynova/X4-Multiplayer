<#
.SYNOPSIS
  Session 3: writes (or updates) %LocalAppData%\X4MP\x4mp.json (M3-24; the extension folder in portable mode), the real mod's settings file. Never writes a password.
.DESCRIPTION
  Keys written (all optional): server_host and tcp_port (what the Join dialog proposes is remembered by the game itself, so you rarely need
  these), player_name, selftest (run the self-test at every universe ready), log_level. Existing keys you do not mention are kept.
  Read when X4 starts and on /reloadui. Use -Show to print the current file. Supports -WhatIf.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ServerHost,
    [int]$TcpPort = 0,
    [string]$PlayerName,
    [switch]$SelfTest,
    [switch]$NoSelfTest,
    [ValidateSet('', 'error', 'warn', 'info', 'debug')][string]$LogLevel = '',
    [switch]$Show
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$path = Join-Path (Get-X4MPConfigDir) 'x4mp.json'
$cfg = [ordered]@{}
if (Test-Path $path) { foreach ($p in (Get-Content $path -Raw | ConvertFrom-Json).PSObject.Properties) { $cfg[$p.Name] = $p.Value } }
if ($Show) { if ($cfg.Count) { $cfg | ConvertTo-Json } else { Write-Host "No $path yet." }; return }
if ($ServerHost) { $cfg['server_host'] = $ServerHost }
if ($TcpPort) { $cfg['tcp_port'] = $TcpPort }
if ($PlayerName) { $cfg['player_name'] = $PlayerName }
if ($SelfTest) { $cfg['selftest'] = $true }
if ($NoSelfTest) { $cfg['selftest'] = $false }
if ($LogLevel) { $cfg['log_level'] = $LogLevel }
Write-Host "File: $path"
Write-Host ($cfg | ConvertTo-Json)
if ($PSCmdlet.ShouldProcess($path, 'Write x4mp.json')) {
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    ($cfg | ConvertTo-Json) | Set-Content -Path $path -Encoding UTF8
    Write-Host 'Written. X4 reads it when it starts (and on /reloadui).'
}
