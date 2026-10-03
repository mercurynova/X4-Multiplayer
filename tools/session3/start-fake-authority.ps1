<#
.SYNOPSIS
  Session 3, topology 1: local server + a FakeNode AUTHORITY that serves your own save; the real X4 joins as a client.
.DESCRIPTION
  X4 saves are synced by Steam Cloud: files you delete (x4mp_*.xml.gz leftovers) are restored at the next X4 start, so a save served before turns the
  download test into a cache hit. The script checks for that (see -FreshDownload and -SaveName).
  1. Copies <Documents>\Egosoft\X4\<id>\save\<SaveName>.xml.gz (verbatim; the original is never touched) to out\session3\authority-save\.
  2. Publishes the server and FakeNode when missing, starts the server (out\session3\data, TCP 47780, UDP 47781, HTTP 47790, game port on
     127.0.0.1 only; Net.ModBuildStrict off) and signs in as admin (first-run password change is done for you; the password goes to
     out\session3\admin-password.txt and is never printed).
  3. Runs `fakenode authority --save-file <copy>` in this window (output also in out\session3\fakenode.log). The server then offers that
     save to every joining client: criterion 1.
  Ctrl+C or closing the window stops both. -WhatIf prints the plan only.
.PARAMETER SaveName     Save file name without .xml.gz (your working copy, for example save_004). -List shows the saves.
.PARAMETER List         Only list the saves (newest first) and exit.
.PARAMETER UserId       The numeric folder under Documents\Egosoft\X4 (only needed when there are several).
.PARAMETER Strict       Enforce mod rules strictly (default: Warn, so your own third-party mods do not block the join; a DLC difference always refuses).
.PARAMETER AuthorityExtensions  Make the fake authority report this extension set (vanilla | modded | a JSON file): used for criterion 7, the mod refusal.
                        Without it the fake authority reports YOUR enabled DLCs (every enabled ego_dlc_* extension of the X4 install, read from
                        content.xml and your Documents content.xml enabled flags; written to out\session3\authority-extensions.json), so the server
                        does not refuse you for owning DLCs (a DLC difference refuses even in Warn mode).
.PARAMETER X4Dir        The X4 install folder (found through Steam when omitted); only used to read the DLC list.
.PARAMETER JoinPassword  Server join password for criterion 14 (a THROWAWAY test value you also type into the in-game dialog).
.PARAMETER FreshDownload  Serve a copy whose gzip header timestamp is changed (same decompressed save, different bytes), so its download name
                        x4mp_<12 hex>.xml.gz has never been seen by this PC or Steam Cloud and B1 really downloads. Your saves are not touched; only the
                        copy in out\session3\authority-save differs. Without it, the script warns when that name already exists in your save folder.
.PARAMETER Rebuild      Publish again even if the executables exist.
.PARAMETER TcpPort, UdpPort, HttpPort  Test-only: other ports (the dry run uses 47953-47955).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SaveName,
    [switch]$List,
    [string]$UserId,
    [switch]$Strict,
    [string]$AuthorityExtensions,
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
if ($TcpPort) { $Ports.Tcp = $TcpPort }
if ($UdpPort) { $Ports.Udp = $UdpPort }
if ($HttpPort) { $Ports.Http = $HttpPort }

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
$fakeArgs = @('authority', '--server', "127.0.0.1:$($Ports.Tcp)", '--name', 'FakeAuthority', '--save-file', $authSave)
if ($JoinPassword) { $fakeArgs += @('--password', $JoinPassword) }   # the fake authority must know the join password too
$dlcFile = $null
$dlcList = @()
if ($AuthorityExtensions) { $fakeArgs += @('--authority-extensions', $AuthorityExtensions) }
else {
    $x4 = Resolve-X4Dir $X4Dir -AllowMissing
    $dlcList = @(Get-LocalDlcExtensions $x4 $user)
    if ($dlcList.Count -gt 0) {
        $dlcFile = Join-Path $OutDir 'authority-extensions.json'
        $fakeArgs += @('--authority-extensions', $dlcFile)
    }
    else { Write-Warning 'No enabled DLC found in the X4 install: the fake authority reports none (a real player with DLCs would be refused). Pass -X4Dir or -AuthorityExtensions.' }
}

# Steam Cloud / cache-hit check (close-out B item 8): what name will the mod give the downloaded copy, and is it already in the save folder?
$cloud = Test-SteamCloudSaveFolder $saveDir
if ((Test-Path $source) -and -not $FreshDownload) {
    $expectName = Get-SaveDownloadName $source
    if (Test-Path (Join-Path $saveDir $expectName)) {
        Write-Warning ("$expectName already exists in your save folder" + $(if ($cloud) { ' (and Steam Cloud restores deleted files there at every X4 start)' } else { '' }) +
            ': the B1 download would be a CACHE HIT, not a download. Re-run with -FreshDownload (serves a copy with a new name) or with a -SaveName that was never served before.')
    }
    elseif ($cloud) {
        Write-Warning "Steam Cloud manages your save folder: x4mp_*.xml.gz files you delete come back at X4 start. $expectName is not there now, so B1 will download; after this run it stays (and syncs), so use -FreshDownload next time."
    }
}
elseif ($FreshDownload -and $cloud) { Write-Host 'Steam Cloud detected: -FreshDownload serves a never-seen save name, so B1 downloads for real.' }

Write-Host "Save      : $source"
Write-Host "Copy to   : $authSave$(if ($FreshDownload) { '  (FreshDownload: gzip header timestamp changed, same save content)' })"
Write-Host "Server    : $serverExe  (TCP $($Ports.Tcp) / UDP $($Ports.Udp) on 127.0.0.1, HTTP $($Ports.Http)); mods enforcement: $(if ($Strict) { 'Strict' } else { 'Warn' }); join password: $(if ($JoinPassword) { 'set' } else { 'none' })"
Write-Host "Authority extensions: $(if ($AuthorityExtensions) { $AuthorityExtensions } elseif ($dlcList.Count) { 'your DLCs: ' + (($dlcList | ForEach-Object { $_.id + '@' + $_.version }) -join ', ') } else { 'none' })"
Write-Host "FakeNode  : $fakeNodeExe $($fakeArgs -join ' ')"
Write-Host "GUI       : $guiUrl   (log in as 'admin'; the password is in $(Join-Path $OutDir 'admin-password.txt'), never printed)"
if (-not $PSCmdlet.ShouldProcess($OutDir, 'Publish, start the server and the FakeNode authority')) { return }

New-Item -ItemType Directory -Force $OutDir, $authDir | Out-Null
if ($FreshDownload) {
    New-FreshDownloadCopy $source $authSave
    $fresh = Get-SaveDownloadName $authSave
    if (Test-Path (Join-Path $saveDir $fresh)) { throw "Unlikely: $fresh exists in the save folder; run again." }
    Write-Host "Fresh download name: $fresh (not in your save folder, so the client downloads it)"
}
else { Copy-Item $source $authSave -Force }
if ($dlcFile) { ConvertTo-Json -InputObject @($dlcList) -Depth 4 | Set-Content -Path $dlcFile -Encoding ascii }
$null = Ensure-Published -Force:$Rebuild

$envVars = @{ X4MP__Mods__Enforcement = $(if ($Strict) { 'Strict' } else { 'Warn' }) }
if ($JoinPassword) { $envVars['X4MP__Net__JoinPassword'] = $JoinPassword }
$server = Start-S3Server $serverExe $envVars
try {
    $null = Initialize-AdminSession
    Set-S3NexusEntry -Present:($AuthorityExtensions -eq 'modded')
    Set-S3Enforcement -Strict:$Strict
    Write-Host ''
    Write-Host "Server is up. Admin GUI: $guiUrl"
    Write-Host "Admin password file: $($Admin.PasswordFile)  (open it yourself; log in as 'admin')"
    Write-Host 'Starting the FakeNode authority. Leave this window open; Ctrl+C stops everything.'
    Write-Host ''
    & $fakeNodeExe @fakeArgs 2>&1 | Tee-Object -FilePath $fakeLog
    Write-Host "FakeNode exited with $LASTEXITCODE."
}
finally {
    if ($server -and -not $server.HasExited) { Write-Host 'Stopping the server...'; Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
}
