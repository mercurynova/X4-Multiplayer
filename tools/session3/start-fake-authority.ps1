<#
.SYNOPSIS
  Session 3, topology 1: local server + a FakeNode AUTHORITY that serves your own save; the real X4 joins as a client.
.DESCRIPTION
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
.PARAMETER JoinPassword  Server join password for criterion 14 (a THROWAWAY test value you also type into the in-game dialog).
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
    [string]$JoinPassword,
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
if ($AuthorityExtensions) { $fakeArgs += @('--authority-extensions', $AuthorityExtensions) }

Write-Host "Save      : $source"
Write-Host "Copy to   : $authSave"
Write-Host "Server    : $serverExe  (TCP $($Ports.Tcp) / UDP $($Ports.Udp) on 127.0.0.1, HTTP $($Ports.Http)); mods enforcement: $(if ($Strict) { 'Strict' } else { 'Warn' }); join password: $(if ($JoinPassword) { 'set' } else { 'none' })"
Write-Host "FakeNode  : $fakeNodeExe $($fakeArgs -join ' ')"
Write-Host "GUI       : $guiUrl   (log in as 'admin'; the password is in $(Join-Path $OutDir 'admin-password.txt'), never printed)"
if (-not $PSCmdlet.ShouldProcess($OutDir, 'Publish, start the server and the FakeNode authority')) { return }

New-Item -ItemType Directory -Force $OutDir, $authDir | Out-Null
Copy-Item $source $authSave -Force
$null = Ensure-Published -Force:$Rebuild

$envVars = @{ X4MP__Mods__Enforcement = $(if ($Strict) { 'Strict' } else { 'Warn' }) }
if ($JoinPassword) { $envVars['X4MP__Net__JoinPassword'] = $JoinPassword }
$server = Start-S3Server $serverExe $envVars
try {
    $null = Initialize-AdminSession
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
