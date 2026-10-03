<#
.SYNOPSIS
  Session 3 (criterion 16): writes Documents\Egosoft\X4\x4mp\launch.json, the one-shot "connect without any UI" request. Never prints the password.
.DESCRIPTION
  The mod reads launch.json once, when X4 starts, deletes it immediately, and connects with its content (no Join dialog). The file holds
  {"server":"host:port","name":"...","password":"...","expires_utc":"<UTC time like 2026-10-03T12:00:00Z>"} (and, for -Role authority,
  "role":"authority" and "admin_password"). The file is written without a byte-order mark.
  -Minutes N sets expires_utc to now + N minutes (default 10). -Expired writes a time that has already passed: the mod must ignore it and
  still delete the file (log line "launch.json expired"). The password is optional; use a THROWAWAY test value. Supports -WhatIf.
.PARAMETER Server         host:port of the X4MP server (default 127.0.0.1:47780).
.PARAMETER Name           Player name.
.PARAMETER Password       Session (join) password, if the server has one.
.PARAMETER Role           client (default) or authority.
.PARAMETER AdminPassword  The in-game admin password, only for -Role authority.
.PARAMETER Minutes        Minutes the file stays valid (default 10).
.PARAMETER Expired        Write an already expired file (tests that expired files are ignored).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Server = '127.0.0.1:47780',
    [string]$Name = 'Tester',
    [string]$Password,
    [ValidateSet('client', 'authority')][string]$Role = 'client',
    [string]$AdminPassword,
    [int]$Minutes = 10,
    [switch]$Expired
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$path = Join-Path (Get-X4MPConfigDir) 'launch.json'
$when = if ($Expired) { (Get-Date).ToUniversalTime().AddMinutes(-5) } else { (Get-Date).ToUniversalTime().AddMinutes($Minutes) }
$cfg = [ordered]@{ server = $Server; name = $Name }
if ($Password) { $cfg['password'] = $Password }
if ($Role -eq 'authority') { $cfg['role'] = 'authority'; if ($AdminPassword) { $cfg['admin_password'] = $AdminPassword } }
$cfg['expires_utc'] = $when.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
Write-Host "File    : $path"
Write-Host "Content : server=$Server name=$Name role=$Role password=$(if ($Password) { '<set>' } else { '<none>' }) expires_utc=$($cfg['expires_utc'])$(if ($Expired) { '  (already expired on purpose)' })"
if ($PSCmdlet.ShouldProcess($path, 'Write launch.json')) {
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    [IO.File]::WriteAllText($path, ($cfg | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding($false)))
    Write-Host 'Written. Start X4 now: it connects by itself and deletes the file at once. Delete the file yourself if you do not start X4.'
}
