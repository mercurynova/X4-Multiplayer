<#
.SYNOPSIS
  Session 4, sitting 2: local server with NO fake authority; the real X4 hosts as the authority and FakeNode WINGMAN bots join as the other players.
.DESCRIPTION
  1. Publishes when needed, starts the server (out\session4\data, TCP 47780, UDP 47781, HTTP 47790, game port on 127.0.0.1 only) with an
     in-game admin password (the "host key" you type into the Join dialog), signs in as GUI admin (password file out\session4\admin-password.txt).
  2. With -SaveName: uploads that save and creates + starts the session from it (what session 3's upload-save.ps1 does).
  3. Waits until the session is Running (the real X4 authority joined, loaded the save, its SessionStart checkpoint is stored).
  4. Waits until YOUR (the host's) ship exists for the session: the host must have sat down in the pilot seat (check 2.1: the bots must not
     start before, because the real authority can only place their avatars next to the host's ship). Then it runs
     `fakenode swarm --clients N --wingman <Target> --avatars --chat-echo --galaxy-file <dump>`: the bots (Wing01..) join, download the checkpoint,
     ask for avatars (the real authority spawns them next to your ship as team ships), and orbit your ship. No --verify: there is no ground truth for a real game.
  5. Stays up until Ctrl+C so you can press "Save now" in the GUI (Sessions page) and read the Players page.
  Everything is tee'd to out\session4\fakenode-clients.log. -WhatIf prints the plan only.
  Restart with a different -Wingmen (3 -> 7 for the performance check 2.4): Ctrl+C, run again; the session/save upload is done again for you.
  The bots report YOUR enabled DLCs (read from the X4 install and your content.xml; out\session4\client-extensions.json), or the server would refuse them.
.PARAMETER SaveName   Optional: your save (name without .xml.gz) to upload and start a session from.
.PARAMETER Wingmen    Number of wingman bots (default 3, at most 7).
.PARAMETER Target     The player name the bots fly around = the name you type into the Join dialog when hosting (default Tester).
.PARAMETER WingmanMode  orbit (default) or formation.
.PARAMETER GalaxyFile The galaxy dump JSON (default out\session4\galaxy-dump.json, written after sitting 0). Needed: the bots must use sector macros your X4 has.
.PARAMETER NodeAdminPassword  The in-game admin password for hosting (default: a fixed THROWAWAY test value, printed; the server is loopback-only).
.PARAMETER Strict     Enforce mod rules strictly (default Warn).
.PARAMETER WaitMinutes  How long to wait for the session to become Running and for your ship (default 40).
.PARAMETER X4Dir, UserId  Where to read the DLC list from (found automatically when omitted).
.PARAMETER TcpPort, UdpPort, HttpPort  Test-only: other ports (the dry run uses 47977-47979).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SaveName,
    [int]$Wingmen = 3,
    [string]$Target = '',
    [ValidateSet('orbit', 'formation')][string]$WingmanMode = 'orbit',
    [string]$GalaxyFile,
    [string]$UserId,
    [string]$NodeAdminPassword = 'x4mp-host-test',
    [switch]$Strict,
    [int]$WaitMinutes = 40,
    [switch]$Rebuild,
    [string]$X4Dir,
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
if (-not $Target) { $Target = $DefaultTarget }
if ($Wingmen -lt 1 -or $Wingmen -gt 7) { throw '-Wingmen must be 1..7.' }
$dry = [bool]$WhatIfPreference

$serverExe = Join-Path $Repo 'out\win-x64\x4mp-server.exe'
$fakeNodeExe = Join-Path $Repo 'out\fakenode\X4MP.FakeNode.exe'
$guiUrl = "http://127.0.0.1:$($Ports.Http)"
$clientsLog = Join-Path $OutDir 'fakenode-clients.log'
$galaxy = Resolve-GalaxyFile $GalaxyFile -Dry:$dry
$dlc = New-DlcExtensionsFile $X4Dir $UserId 'client-extensions.json' -Dry:$dry
$swarmArgs = @('swarm', '--server', "127.0.0.1:$($Ports.Tcp)", '--duration', '21600', '--seed', $WingmanSeed) + (Get-WingmanArgs $Wingmen $Target $WingmanMode 90)
if ($galaxy) { $swarmArgs += @('--galaxy-file', $galaxy) }
if ($dlc.File) { $swarmArgs += @('--extensions', $dlc.File) }

Write-Host "Server  : $serverExe (TCP $($Ports.Tcp) / UDP $($Ports.Udp) on 127.0.0.1, HTTP $($Ports.Http)); mods enforcement: $(if ($Strict) { 'Strict' } else { 'Warn' })"
Write-Host "In-game admin password (type it into the Join dialog when hosting): $NodeAdminPassword"
Write-Host "GUI     : $guiUrl  (log in as 'admin'; password file $(Join-Path $OutDir 'admin-password.txt'), never printed)"
Write-Host "Session : $(if ($SaveName) { "uploaded from $SaveName" } else { 'none yet: run tools\session4\upload-save.ps1 in a second window, or create one in the GUI' })"
Write-Host "Wingmen : $Wingmen bots ($WingmanPrefix$('01..')), $WingmanMode around '$Target' (use exactly that name in the Join dialog), started when your ship exists; extensions: $(Format-DlcList $dlc.List)"
Write-Host "Galaxy  : $(if ($galaxy) { $galaxy } else { 'GENERATED (no galaxy dump: the real authority cannot place the bots in sectors it does not have)' })"
Write-Host "FakeNode: $fakeNodeExe $($swarmArgs -join ' ')"
if (-not $PSCmdlet.ShouldProcess($OutDir, 'Publish, start the server, wait for the real authority and for your ship, start the wingman bots')) { return }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$null = Ensure-Published -Force:$Rebuild
$server = Start-S3Server $serverExe @{ X4MP__Net__AdminPassword = $NodeAdminPassword; X4MP__Mods__Enforcement = $(if ($Strict) { 'Strict' } else { 'Warn' }); X4MP__Net__MaxPlayers = '16' }
try {
    $s = Initialize-AdminSession
    Set-S3NexusEntry
    Set-S3Enforcement -Strict:$Strict
    Write-Host "Server is up. Admin GUI: $guiUrl"
    if ($SaveName) {
        & (Join-Path $PSScriptRoot 'upload-save.ps1') -SaveName $SaveName -UserId $UserId -HttpPort $Ports.Http
    }
    Write-Host ''
    Write-Host "Now start X4 (x4mp installed) and host: Multiplayer > Join, name '$Target', 'Host this session as the authority' = Yes, the admin password above."
    Write-Host "Waiting up to $WaitMinutes min for the session to become Running and for your ship (sit in the pilot seat after the load)..."
    $deadline = (Get-Date).AddMinutes($WaitMinutes)
    $ready = $false
    $lastNote = ''
    while (-not $ready) {
        if ($server.HasExited) { throw "The server exited with $($server.ExitCode)" }
        $note = 'waiting for the session to start'
        try {
            $cur = Invoke-RestMethod -Uri "$guiUrl/api/v1/sessions/current" -Headers $Admin.Headers -WebSession $s
            if ($cur) {
                $note = "session state: $($cur.state)"
                if ($cur.state -eq 'Running') {
                    $note = "session Running; waiting for your ship (sit down in the pilot seat as '$Target')"
                    $dash = Invoke-RestMethod -Uri "$guiUrl/api/v1/dashboard" -Headers $Admin.Headers -WebSession $s
                    $me = @($dash.players | Where-Object { $_.name -eq $Target -and $_.shipNetId -gt 0 })
                    if ($me.Count -gt 0) { $ready = $true }
                }
            }
        } catch { }
        if ($note -ne $lastNote) { $lastNote = $note; Write-Host ("  {0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $note) }
        if (-not $ready) {
            if ((Get-Date) -gt $deadline) { throw "Not ready within $WaitMinutes min ($lastNote)." }
            Wait-Process -Id $server.Id -Timeout 2 -ErrorAction SilentlyContinue
        }
    }
    Write-Host "Your ship exists for the session. Starting $Wingmen wingman bot(s); they ask for avatars (up to 90 s each) and then orbit '$Target'."
    & $fakeNodeExe @swarmArgs 2>&1 | Tee-Object -FilePath $clientsLog
    Write-Host "FakeNode exited with $LASTEXITCODE. The server keeps running: use the GUI, Ctrl+C to stop."
    Wait-Process -Id $server.Id
}
finally {
    if ($server -and -not $server.HasExited) { Write-Host 'Stopping the server...'; Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
}
