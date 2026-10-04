<#
.SYNOPSIS
  Session 4, sitting 1: local server + a FakeNode AUTHORITY that serves your own save (+ optional WINGMAN bots); the real X4 joins as a client.
.DESCRIPTION
  Like session 3's start-fake-authority.ps1, plus:
    -Wingmen N      N FakeNode bots (Wing01..) join as players, take their avatars next to the fake host ship, and then fly in orbit (400 m)
                    around the player called -Target (default Tester), the name you type into the Join dialog. They echo your chat.
    -GalaxyFile F   the FakeNode universe is built from the galaxy dump of sitting 0 (real sector macros and gate links), so the avatars,
                    the fake host ship and the bots are placed in sectors your X4 really has. Default: out\session4\galaxy-dump.json when it
                    exists (collect-logs.ps1 / extract-galaxy-dump.ps1 write it). Without a dump the bots use a generated galaxy and a real X4
                    cannot place them: the script warns.
  1. Copies <Documents>\Egosoft\X4\<id>\save\<SaveName>.xml.gz (verbatim; the original is never touched) to out\session4\authority-save\.
  2. Publishes the server and FakeNode when missing, starts the server (out\session4\data, TCP 47780, UDP 47781, HTTP 47790; the game port is
     on 127.0.0.1 only: this PC; Net.ModBuildStrict off) and signs in as admin (first-run password change done for you; the password goes to
     out\session4\admin-password.txt and is never printed).
  3. Runs `fakenode authority ...` (no wingmen) or `fakenode swarm --with-authority ... --clients N --wingman <Target>` in this window
     (output also in out\session4\fakenode.log). The server then offers that save to every joining client.
  Ctrl+C or closing the window stops everything. -WhatIf prints the plan only.
  The fake authority reports YOUR enabled DLCs (read from the X4 install and your content.xml; written to out\session4\authority-extensions.json)
  and the bots report the same, so the server does not refuse you for owning DLCs.
  Where you land: the fake authority puts the fake host ship and every avatar in the FIRST sector (by macro name order, sector index 1) that has a
  gate. After joining you are moved there. The sector name appears in the fakenode.log line "host=net_id=... sector=1".
.PARAMETER SaveName     Save file name without .xml.gz (your working copy, for example save_004). -List shows the saves.
.PARAMETER List         Only list the saves (newest first) and exit.
.PARAMETER Wingmen      Number of wingman bots (default 0 = authority only). Sitting 1 uses 2, the performance check 7.
.PARAMETER Target       The player name the wingmen fly around (default Tester).
.PARAMETER WingmanMode  orbit (default) or formation.
.PARAMETER HostStand    With wingmen: the sector-local spot x,y,z in metres where the fake host ship (and so your avatar, 300-600 m from it) appears, in the first sector
                        of the galaxy dump. Default 25000,0,-20000 (25 km out: empty space, away from the sector's stations). Change it if you land in something.
                        Without wingmen you appear next to where YOUR save's ship really is (the first request, yours, decides).
.PARAMETER GalaxyFile   The galaxy dump JSON (default out\session4\galaxy-dump.json).
.PARAMETER UserId       The numeric folder under Documents\Egosoft\X4 (only needed when there are several).
.PARAMETER Strict       Enforce mod rules strictly (default: Warn; a DLC difference always refuses).
.PARAMETER X4Dir        The X4 install folder (found through Steam when omitted); only used to read the DLC list.
.PARAMETER JoinPassword A THROWAWAY server join password (you also type it into the in-game dialog). Never a real password.
.PARAMETER FreshDownload Serve a copy whose gzip header timestamp is changed (same decompressed save, different bytes), so its download name has never
                        been seen by this PC or Steam Cloud and the client really downloads. Your saves are not touched.
.PARAMETER Rebuild      Publish again even if the executables exist.
.PARAMETER TcpPort, UdpPort, HttpPort  Test-only: other ports (the dry run uses 47977-47979).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SaveName,
    [switch]$List,
    [int]$Wingmen = 0,
    [string]$Target = '',
    [ValidateSet('orbit', 'formation')][string]$WingmanMode = 'orbit',
    [ValidatePattern('^-?\d+(\.\d+)?,-?\d+(\.\d+)?,-?\d+(\.\d+)?$')][string]$HostStand = '25000,0,-20000',
    [string]$GalaxyFile,
    [string]$UserId,
    [switch]$Strict,
    [string]$X4Dir,
    [string]$JoinPassword,
    [switch]$FreshDownload,
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
if (-not $Target) { $Target = $DefaultTarget }
if ($Wingmen -lt 0 -or $Wingmen -gt 7) { throw '-Wingmen must be 0..7 (the performance check of criterion 11 uses 7).' }

$dry = [bool]$WhatIfPreference
$user = Resolve-X4UserDir $UserId -AllowMissing:($dry -or $List)
$saveDir = Join-Path $user 'save'

function Show-Saves([string]$dir) {
    if (-not (Test-Path $dir)) { Write-Host "Save folder not found: $dir"; return }
    Write-Host "Saves in $dir (newest first; pass the name without .xml.gz as -SaveName):"
    Get-ChildItem $dir -File -Filter '*.xml.gz' | Sort-Object LastWriteTime -Descending | ForEach-Object {
        Write-Host ('  {0,-24} {1,8:N1} MB   {2}' -f ($_.Name -replace '\.xml\.gz$', ''), ($_.Length / 1MB), $_.LastWriteTime)
    }
}
if ($List) { Show-Saves $saveDir; return }
if (-not $SaveName) { Show-Saves $saveDir; throw 'Pass -SaveName <name without .xml.gz>, for example -SaveName save_004.' }
$SaveName = $SaveName -replace '\.xml\.gz$', ''
$source = Join-Path $saveDir "$SaveName.xml.gz"
if (-not (Test-Path $source)) {
    $msg = "Save not found: $source (run with -List)"
    if ($dry) { Write-Warning "$msg (a real run would stop here)" } else { throw $msg }
}

$serverExe = Join-Path $Repo 'out\win-x64\x4mp-server.exe'
$fakeNodeExe = Join-Path $Repo 'out\fakenode\X4MP.FakeNode.exe'
$authDir = Join-Path $OutDir 'authority-save'
$authSave = Join-Path $authDir "$SaveName.xml.gz"
$fakeLog = Join-Path $OutDir 'fakenode.log'
$guiUrl = "http://127.0.0.1:$($Ports.Http)"
$galaxy = Resolve-GalaxyFile $GalaxyFile -Dry:$dry
$dlc = New-DlcExtensionsFile $X4Dir $UserId 'authority-extensions.json' -Dry:$dry

$fakeArgs = @(if ($Wingmen -gt 0) { 'swarm'; '--with-authority' } else { 'authority' })   # @(): a one-element result must stay an array for the += below
$fakeArgs += @('--server', "127.0.0.1:$($Ports.Tcp)", '--save-file', $authSave, '--duration', '21600', '--seed', $WingmanSeed)
if ($Wingmen -eq 0) { $fakeArgs += @('--name', 'FakeAuthority') }
if ($Wingmen -gt 0) { $fakeArgs += Get-WingmanArgs $Wingmen $Target $WingmanMode; $fakeArgs += @('--host-stand-pos', $HostStand) }
if ($galaxy) { $fakeArgs += @('--galaxy-file', $galaxy) }
if ($dlc.File) { $fakeArgs += @('--authority-extensions', $dlc.File); if ($Wingmen -gt 0) { $fakeArgs += @('--extensions', $dlc.File) } }
if ($JoinPassword) { $fakeArgs += @('--password', $JoinPassword) }   # the fake nodes must know the join password too

# Steam Cloud / cache-hit check: what name will the mod give the downloaded copy, and is it already in the save folder?
$cloud = Test-SteamCloudSaveFolder $saveDir
if ((Test-Path $source) -and -not $FreshDownload) {
    $expectName = Get-SaveDownloadName $source
    if (Test-Path (Join-Path $saveDir $expectName)) {
        Write-Warning ("$expectName already exists in your save folder" + $(if ($cloud) { ' (and Steam Cloud restores deleted files there at every X4 start)' } else { '' }) +
            ': the join would be a CACHE HIT, not a download. Re-run with -FreshDownload.')
    }
    elseif ($cloud) { Write-Warning "Steam Cloud manages your save folder: x4mp_*.xml.gz files you delete come back at X4 start. Use -FreshDownload next time you want a real download." }
}
elseif ($FreshDownload -and $cloud) { Write-Host 'Steam Cloud detected: -FreshDownload serves a never-seen save name, so the join downloads for real.' }

Write-Host "Save      : $source"
Write-Host "Copy to   : $authSave$(if ($FreshDownload) { '  (FreshDownload: gzip header timestamp changed, same save content)' })"
Write-Host "Server    : $serverExe  (TCP $($Ports.Tcp) / UDP $($Ports.Udp) on 127.0.0.1, HTTP $($Ports.Http)); mods enforcement: $(if ($Strict) { 'Strict' } else { 'Warn' }); join password: $(if ($JoinPassword) { 'set' } else { 'none' })"
Write-Host "Authority : FakeNode, extensions: $(Format-DlcList $dlc.List)"
Write-Host "Galaxy    : $(if ($galaxy) { $galaxy } else { 'GENERATED (no galaxy dump: a real X4 cannot place the bots)' })"
Write-Host "Wingmen   : $(if ($Wingmen -gt 0) { "$Wingmen bots ($WingmanPrefix" + '01..), ' + $WingmanMode + " around the player '$Target' (type exactly that name into the Join dialog)" } else { 'none' })"
Write-Host "FakeNode  : $fakeNodeExe $($fakeArgs -join ' ')"
Write-Host "GUI       : $guiUrl   (log in as 'admin'; the password is in $(Join-Path $OutDir 'admin-password.txt'), never printed)"
if (-not $PSCmdlet.ShouldProcess($OutDir, 'Publish, start the server and the FakeNode authority' + $(if ($Wingmen -gt 0) { ' with wingmen' }))) { return }

New-Item -ItemType Directory -Force $OutDir, $authDir | Out-Null
if ($FreshDownload) {
    New-FreshDownloadCopy $source $authSave
    $fresh = Get-SaveDownloadName $authSave
    if (Test-Path (Join-Path $saveDir $fresh)) { throw "Unlikely: $fresh exists in the save folder; run again." }
    Write-Host "Fresh download name: $fresh (not in your save folder, so the client downloads it)"
}
else { Copy-Item $source $authSave -Force }
$null = Ensure-Published -Force:$Rebuild

$envVars = @{ X4MP__Mods__Enforcement = $(if ($Strict) { 'Strict' } else { 'Warn' }); X4MP__Net__MaxPlayers = '16' }
if ($JoinPassword) { $envVars['X4MP__Net__JoinPassword'] = $JoinPassword }
$server = Start-S3Server $serverExe $envVars
try {
    $null = Initialize-AdminSession
    Set-S3NexusEntry
    Set-S3Enforcement -Strict:$Strict
    Write-Host ''
    Write-Host "Server is up. Admin GUI: $guiUrl"
    Write-Host "Admin password file: $($Admin.PasswordFile)  (open it yourself; log in as 'admin')"
    Write-Host 'Starting the FakeNode authority. Leave this window open; Ctrl+C stops everything.'
    Write-Host "Wait for 'checkpoint stored' in the output below before you start X4."
    Write-Host ''
    & $fakeNodeExe @fakeArgs 2>&1 | Tee-Object -FilePath $fakeLog
    Write-Host "FakeNode exited with $LASTEXITCODE."
}
finally {
    if ($server -and -not $server.HasExited) { Write-Host 'Stopping the server...'; Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
}
