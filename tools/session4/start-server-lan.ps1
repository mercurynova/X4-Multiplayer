<#
.SYNOPSIS
  Session 4, sitting 3 (two PCs), on PC A: starts the server so that PC B can reach it over the LAN, and checks the Windows firewall.
.DESCRIPTION
  Binds the game port on ALL interfaces (0.0.0.0:47780) and the admin GUI on all interfaces (47790), UDP 47781 for the realtime lane.
  1. Prints this PC's LAN address(es): PC B joins "<address>:47780".
  2. READS the Windows Defender Firewall (it changes nothing) and tells you which of TCP 47780, UDP 47781, TCP 47790 have an enabled inbound allow
     rule. For missing ones it PRINTS the exact New-NetFirewallRule commands; you run them yourself in an ELEVATED PowerShell (Run as administrator).
     The rules it prints are limited to the local subnet. This script never changes the firewall or any other system setting.
  3. Also prints the two commands for step 3.5 (UDP fallback): Disable-NetFirewallRule / Enable-NetFirewallRule for the UDP rule, to be run by you.
  4. Publishes when needed, starts the server (out\session4\data), signs in as admin (password in out\session4\admin-password.txt, never printed),
     optionally uploads your save (-SaveName) and creates the session; stays up until Ctrl+C.
  -Check only does 1-3 (no server). -WhatIf prints the plan only.
  PC A then starts X4 and hosts as the authority (join address 127.0.0.1:47780, "Host this session as the authority" = Yes, the in-game admin
  password printed below); PC B joins <PC A address>:47780 with the client kit (make-client-kit.ps1).
  The admin GUI is reachable from the LAN too (admin login required): use a password you do not use elsewhere (the script generates one).
.PARAMETER Check          Only show addresses, firewall status and commands; do not start anything.
.PARAMETER SaveName       Optional: your save (name without .xml.gz) to upload and start a session from (as session 3 upload-save.ps1).
.PARAMETER NodeAdminPassword  The in-game admin password for hosting on PC A (default: a fixed THROWAWAY test value, printed).
.PARAMETER JoinPassword   A THROWAWAY server join password (PC B types it into the Join dialog). Recommended on a LAN you do not fully trust.
.PARAMETER Strict         Enforce mod rules strictly (default Warn).
.PARAMETER UserId         The numeric folder under Documents\Egosoft\X4 (only for -SaveName).
.PARAMETER Rebuild        Publish again even if the executables exist.
.PARAMETER TcpPort, UdpPort, HttpPort  Test-only: other ports.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Check,
    [string]$SaveName,
    [string]$NodeAdminPassword = 'x4mp-host-test',
    [string]$JoinPassword,
    [switch]$Strict,
    [string]$UserId,
    [switch]$Rebuild,
    [int]$TcpPort = 0,
    [int]$UdpPort = 0,
    [int]$HttpPort = 0
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'topology.ps1')
if ($TcpPort) { $Ports.Tcp = $TcpPort }
if ($UdpPort) { $Ports.Udp = $UdpPort }
if ($HttpPort) { $Ports.Http = $HttpPort }
$dry = [bool]$WhatIfPreference

# ---- 1. addresses -----------------------------------------------------------------------------------------------------------------
$addrs = @(Get-LanAddresses)
Write-Host 'This PC (PC A) on the network:'
if ($addrs.Count -eq 0) { Write-Warning '  no LAN address found (is the PC connected?).' }
foreach ($a in $addrs) { Write-Host ("  {0,-16} {1}" -f $a.Address, $a.Interface) }
if ($addrs.Count -gt 0) { Write-Host "PC B joins: $($addrs[0].Address):$($Ports.Tcp)   (several adapters listed? use the one on the same network as PC B; over Tailscale use the 100.x address of the Tailscale adapter)" }

# ---- 2. firewall (read only) ------------------------------------------------------------------------------------------------------
Write-Host ''
Write-Host 'Windows Defender Firewall, inbound allow rules (read only; nothing is changed):'
$fw = @(Get-FirewallStatus)
$missing = @()
foreach ($r in $fw) {
    # M3-24: report the rule NAMED exactly like the step-3.5 lines expect, not just "some rule covers the port" (an unrelated program rule on all ports
    # made the old check say ok while Disable/Enable-NetFirewallRule -DisplayName 'X4MP game UDP' failed with no such rule).
    if ($null -eq $r.Covered) { $state = 'UNKNOWN (could not read the rules)' }
    elseif ($r.Named -eq $true -and $r.NamedEnabled -eq $true) {
        $ts = if ($null -eq $r.NamedTailscale) { '' } elseif ($r.NamedTailscale) { ', includes Tailscale 100.64.0.0/10' } else { ', does NOT include Tailscale 100.64.0.0/10 (a tailnet peer cannot connect)' }
        $state = "ok (rule '$($r.Name)' present$ts)"
    }
    elseif ($r.Named -eq $true) { $state = "rule '$($r.Name)' exists but is DISABLED or not an Allow rule (step 3.5 would enable it; for now the port is $(if ($r.Covered) { 'covered by another rule' } else { 'closed' }))" }
    elseif ($r.Covered) { $state = 'covered only by another rule (step 3.5 will not work: no rule named ' + "'$($r.Name)')" }
    else { $state = 'MISSING' }
    Write-Host ("  {0} {1,-5} {2,-18} {3}" -f $r.Protocol, $r.Port, $r.Name, $state)
    if ($r.Named -ne $true) { $missing += $r }
}
if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host 'To add the rule(s) under the exact names step 3.5 uses (also where another rule already covers the port), run this in an ELEVATED PowerShell on PC A (right-click PowerShell > Run as administrator). This script does not run it:'
    foreach ($c in Get-FirewallCommands -Only $missing) { Write-Host "  $c" }
    Write-Host '  (The rules apply to every network profile but only accept connections from the local subnet and from Tailscale (tailnet, 100.64.0.0/10) peers.)'
}
$udpCmd = Get-UdpToggleCommands
Write-Host ''
Write-Host 'Step 3.5 (UDP fallback) later: to block UDP run this in an elevated PowerShell, and to allow it again run the second line:'
Write-Host "  $($udpCmd.Off)"
Write-Host "  $($udpCmd.On)"
Write-Host '  (they only work when the rules above exist under exactly those names.)'
if ($Check) { return }

# ---- 3. the server ----------------------------------------------------------------------------------------------------------------
$serverExe = Join-Path $Repo 'out\win-x64\x4mp-server.exe'
$guiUrl = "http://127.0.0.1:$($Ports.Http)"
Write-Host ''
Write-Host "Server  : $serverExe (TCP $($Ports.Tcp) / UDP $($Ports.Udp) / HTTP $($Ports.Http) on ALL interfaces); mods enforcement: $(if ($Strict) { 'Strict' } else { 'Warn' }); join password: $(if ($JoinPassword) { 'set (PC B types it)' } else { 'none' })"
Write-Host "In-game admin password (PC A types it into the Join dialog when hosting): $NodeAdminPassword"
Write-Host "GUI     : $guiUrl on PC A, http://<PC A address>:$($Ports.Http) from the LAN (log in as 'admin'; password file $(Join-Path $OutDir 'admin-password.txt'), never printed)"
Write-Host "Session : $(if ($SaveName) { "uploaded from $SaveName" } else { 'none yet: pass -SaveName, or run tools\session4\upload-save.ps1 in a second window' })"
if (-not $PSCmdlet.ShouldProcess($OutDir, 'Publish, start the LAN server' + $(if ($SaveName) { ', upload the save and start a session' }))) { return }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$null = Ensure-Published -Force:$Rebuild
$envVars = @{
    X4MP__Net__NodeTcpEndpoint = "0.0.0.0:$($Ports.Tcp)"      # LAN: every interface (Start-S3Server defaults to this PC only)
    X4MP__Net__AdminPassword   = $NodeAdminPassword
    X4MP__Mods__Enforcement    = $(if ($Strict) { 'Strict' } else { 'Warn' })
    X4MP__Net__MaxConnectionsPerIp = '16'
}
if ($JoinPassword) { $envVars['X4MP__Net__JoinPassword'] = $JoinPassword }
$server = Start-S3Server $serverExe $envVars
try {
    $null = Initialize-AdminSession
    Set-S3NexusEntry
    Set-S3Enforcement -Strict:$Strict
    Write-Host "Server is up on all interfaces. Admin GUI: $guiUrl"
    if ($SaveName) { & (Join-Path $PSScriptRoot 'upload-save.ps1') -SaveName $SaveName -UserId $UserId -HttpPort $Ports.Http }
    Write-Host ''
    Write-Host "Now: PC A starts X4 and hosts (127.0.0.1:$($Ports.Tcp)). When PC A is in game, PC B starts X4 and joins."
    Write-Host 'Leave this window open; Ctrl+C stops the server.'
    Wait-Process -Id $server.Id
}
finally {
    if ($server -and -not $server.HasExited) { Write-Host 'Stopping the server...'; Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
}
