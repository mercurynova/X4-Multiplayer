<#
.SYNOPSIS
  Session 3, topology 2: local server with NO fake authority; the real X4 hosts as the authority and FakeNode clients join it.
.DESCRIPTION
  1. Publishes when needed, starts the server (out\session3\data, ports 47780/47781/47790, game port on 127.0.0.1 only) with an in-game
     admin password (the "host key" you type into the Join dialog), signs in as GUI admin (password file out\session3\admin-password.txt).
  2. With -SaveName: uploads that save and creates + starts the session from it (what upload-save.ps1 does), criterion 9.
  3. Waits until the session is Running (the real X4 authority has joined, loaded the save, and its SessionStart checkpoint is stored),
     then runs `fakenode swarm --clients N` (no fake authority; no --verify, there is no ground truth for a real game): each client downloads the checkpoint and verifies its SHA-256.
  4. Stays up until Ctrl+C so you can press "Save now" in the GUI (Sessions page) and look at the Players page.
  Everything is tee'd to out\session3\server.out.log and out\session3\fakenode-clients.log. -WhatIf prints the plan only.
.PARAMETER SaveName   Optional: your save (name without .xml.gz) to upload and start a session from.
.PARAMETER Clients    Number of FakeNode clients (default 3).
.PARAMETER NodeAdminPassword  The in-game admin password for hosting (default: a fixed THROWAWAY test value, printed, since the server is loopback-only).
.PARAMETER Strict     Enforce mod rules strictly (default Warn).
.PARAMETER WaitMinutes  How long to wait for the session to become Running (default 30).
.PARAMETER TcpPort, UdpPort, HttpPort  Test-only: other ports.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SaveName,
    [string]$UserId,
    [int]$Clients = 3,
    [string]$NodeAdminPassword = 'x4mp-host-test',
    [switch]$Strict,
    [int]$WaitMinutes = 30,
    [switch]$Rebuild,
    [int]$TcpPort = 0,
    [int]$UdpPort = 0,
    [int]$HttpPort = 0
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
if ($TcpPort) { $Ports.Tcp = $TcpPort }
if ($UdpPort) { $Ports.Udp = $UdpPort }
if ($HttpPort) { $Ports.Http = $HttpPort }
$dry = [bool]$WhatIfPreference

$serverExe = Join-Path $Repo 'out\win-x64\x4mp-server.exe'
$fakeNodeExe = Join-Path $Repo 'out\fakenode\X4MP.FakeNode.exe'
$guiUrl = "http://127.0.0.1:$($Ports.Http)"
$clientsLog = Join-Path $OutDir 'fakenode-clients.log'
Write-Host "Server  : $serverExe (TCP $($Ports.Tcp) / UDP $($Ports.Udp) on 127.0.0.1, HTTP $($Ports.Http)); mods enforcement: $(if ($Strict) { 'Strict' } else { 'Warn' })"
Write-Host "In-game admin password (type it into the Join dialog when hosting): $NodeAdminPassword"
Write-Host "GUI     : $guiUrl  (log in as 'admin'; password file $(Join-Path $OutDir 'admin-password.txt'), never printed)"
Write-Host "Session : $(if ($SaveName) { "uploaded from $SaveName" } else { 'none yet: run upload-save.ps1 in a second window, or create one in the GUI' })"
Write-Host "Clients : $Clients FakeNode clients once the session is Running (output also in $clientsLog)"
if (-not $PSCmdlet.ShouldProcess($OutDir, 'Publish, start the server, wait for the real authority, start FakeNode clients')) { return }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$null = Ensure-Published -Force:$Rebuild
$server = Start-S3Server $serverExe @{ X4MP__Net__AdminPassword = $NodeAdminPassword; X4MP__Mods__Enforcement = $(if ($Strict) { 'Strict' } else { 'Warn' }) }
try {
    $s = Initialize-AdminSession
    Set-S3NexusEntry
    Set-S3Enforcement -Strict:$Strict
    Write-Host "Server is up. Admin GUI: $guiUrl"
    if ($SaveName) {
        & (Join-Path $PSScriptRoot 'upload-save.ps1') -SaveName $SaveName -UserId $UserId -HttpPort $Ports.Http
    }
    Write-Host ''
    Write-Host 'Now start X4 (x4mp installed, see docs/in-game-session-3.md) and host: Multiplayer > Join, "Host this session as the authority" = Yes.'
    Write-Host "Waiting up to $WaitMinutes min for the session to become Running..."
    $deadline = (Get-Date).AddMinutes($WaitMinutes)
    $running = $false
    $lastState = ''
    while (-not $running) {
        if ($server.HasExited) { throw "The server exited with $($server.ExitCode)" }
        try {
            $cur = Invoke-RestMethod -Uri "$guiUrl/api/v1/sessions/current" -Headers $Admin.Headers -WebSession $s
            if ($cur -and $cur.state -ne $lastState) { $lastState = $cur.state; Write-Host ("  {0}  session state: {1}" -f (Get-Date -Format 'HH:mm:ss'), $cur.state) }
            $running = ($cur -and $cur.state -eq 'Running')
        } catch { }
        if (-not $running) {
            if ((Get-Date) -gt $deadline) { throw "The session did not become Running within $WaitMinutes min." }
            Wait-Process -Id $server.Id -Timeout 2 -ErrorAction SilentlyContinue
        }
    }
    Write-Host 'Session is Running: the real authority stored its checkpoint. Starting the FakeNode clients.'
    & $fakeNodeExe swarm --server "127.0.0.1:$($Ports.Tcp)" --clients $Clients --duration 600 2>&1 | Tee-Object -FilePath $clientsLog
    Write-Host "FakeNode clients exited with $LASTEXITCODE (each client prints 'joined with the save after ...' once it has downloaded and SHA-256-verified the checkpoint). The server keeps running: use the GUI, Ctrl+C to stop."
    Wait-Process -Id $server.Id
}
finally {
    if ($server -and -not $server.HasExited) { Write-Host 'Stopping the server...'; Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
}
