<#
.SYNOPSIS
  Session 2: starts the local X4MP server and a FakeNode authority that offers one of your X4 saves as the session save.
.DESCRIPTION
  1. Finds <Documents>\Egosoft\X4\<numeric id>\save\<SaveName>.xml.gz (the id is picked automatically when there is exactly one;
     with several, pass -UserId). X4's save slots are files save_001.xml.gz ... save_010.xml.gz (slot number = number in the
     file name), plus quicksave.xml.gz and autosave_01.xml.gz ... Run with -List to see what is there.
  2. Copies it (verbatim) to out\session2\authority-save\ (your original is never touched).
  3. Publishes the server and FakeNode when missing (dotnet publish, Release), starts the server with
     --data-dir out\session2\data on the default ports (TCP 47780, UDP 47781, HTTP 47790). The game port listens on
     127.0.0.1 only, and Net.ModBuildStrict is off (the probe and FakeNode report different mod build strings).
  4. Runs `fakenode authority --save-file <copy>` in this window and tees its output to out\session2\fakenode.log.
  5. Ctrl+C (or closing the window) stops both. Prints the GUI address and WHERE the admin password file is (never the password).
  With -WhatIf nothing is copied, built or started; the plan is printed.
.PARAMETER SaveName  Save file name without .xml.gz (for example save_003).
.PARAMETER List      Only list the saves in the X4 save folder (newest first) and exit.
.PARAMETER UserId    The numeric folder under Documents\Egosoft\X4 (only needed when there are several).
.PARAMETER Rebuild   Publish the server and FakeNode again even if they exist.
.PARAMETER TcpPort, UdpPort, HttpPort  Test-only: other ports than the defaults (the dry run uses 47953-47955).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SaveName,
    [switch]$List,
    [string]$UserId,
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
if (-not $SaveName) { Show-Saves $saveDir; throw 'Pass -SaveName <name without .xml.gz>, for example -SaveName save_003.' }

$SaveName = $SaveName -replace '\.xml\.gz$', ''
$source = Join-Path $saveDir "$SaveName.xml.gz"
if (-not (Test-Path $source)) {
    $msg = "Save not found: $source"
    if (Test-Path $saveDir) {
        $newest = Get-ChildItem $saveDir -File -Filter '*.xml.gz' | Sort-Object LastWriteTime -Descending | Select-Object -First 8
        $msg += "`nNewest saves there: " + (($newest | ForEach-Object { $_.Name -replace '\.xml\.gz$', '' }) -join ', ') + "`n(run with -List to see all)"
    }
    if ($dry) { Write-Warning "$msg (a real run would stop here)" } else { throw $msg }
}

$serverExe = Join-Path $Repo 'out\win-x64\x4mp-server.exe'
$fakeNodeExe = Join-Path $Repo 'out\fakenode\X4MP.FakeNode.exe'
$data = Join-Path $OutDir 'data'
$authDir = Join-Path $OutDir 'authority-save'
$authSave = Join-Path $authDir "$SaveName.xml.gz"
$fakeLog = Join-Path $OutDir 'fakenode.log'
$guiUrl = "http://127.0.0.1:$($Ports.Http)"
$pwFile = Join-Path $data 'initial-admin-password.txt'

Write-Host "Save      : $source"
Write-Host "Copy to   : $authSave"
Write-Host "Server    : $serverExe  --data-dir $data --port $($Ports.Http)   (TCP $($Ports.Tcp) and UDP $($Ports.Udp) on 127.0.0.1)"
Write-Host "FakeNode  : $fakeNodeExe authority --server 127.0.0.1:$($Ports.Tcp) --save-file <copy>   (output also in $fakeLog)"
Write-Host "GUI       : $guiUrl"
Write-Host "Admin password file (the password is not printed): $pwFile"

if (-not $PSCmdlet.ShouldProcess($OutDir, 'Publish, start the server and the FakeNode authority')) { return }

New-Item -ItemType Directory -Force $OutDir, $authDir | Out-Null
Copy-Item $source $authSave -Force

function Invoke-Native([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}
if ($Rebuild -or -not (Test-Path $serverExe) -or -not (Test-Path $fakeNodeExe)) {
    if (-not (Test-Path (Join-Path $Repo 'tools\flatc\bin\flatc.exe'))) {
        Invoke-Native 'fetch-flatc' { & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $Repo 'tools\flatc\fetch-flatc.ps1') }
    }
    Invoke-Native 'dotnet publish (server)' { dotnet publish (Join-Path $Repo 'server\src\X4MP.Server') -c Release '-p:PublishProfile=win-x64' -p:SkipWebBuild=true -nologo -v:m }
    Invoke-Native 'dotnet build (FakeNode)' { dotnet build (Join-Path $Repo 'tools\X4MP.FakeNode') -c Release -o (Join-Path $Repo 'out\fakenode') -nologo -v:m }
}

# Server settings through the environment of the server process only (the child inherits it).
$env:X4MP__Net__NodeTcpEndpoint = "127.0.0.1:$($Ports.Tcp)"
$env:X4MP__Net__UdpPort = "$($Ports.Udp)"
$env:X4MP__Net__ModBuildStrict = 'false'

$server = Start-Process -FilePath $serverExe -ArgumentList @('--data-dir', "`"$data`"", '--port', "$($Ports.Http)") -WorkingDirectory $OutDir `
    -RedirectStandardOutput (Join-Path $OutDir 'server.out.log') -RedirectStandardError (Join-Path $OutDir 'server.err.log') -PassThru -WindowStyle Hidden
try {
    $deadline = (Get-Date).AddSeconds(40)
    $up = $false
    while (-not $up) {
        if ($server.HasExited) { throw "The server exited with $($server.ExitCode); see $(Join-Path $OutDir 'server.err.log')" }
        try { $up = (Invoke-WebRequest -UseBasicParsing "$guiUrl/healthz" -TimeoutSec 2).StatusCode -eq 200 } catch { $up = $false }
        if (-not $up) {
            if ((Get-Date) -gt $deadline) { throw "The server did not answer $guiUrl/healthz within 40 s" }
            Wait-Process -Id $server.Id -Timeout 1 -ErrorAction SilentlyContinue
        }
    }
    Write-Host ''
    Write-Host "Server is up. Admin GUI: $guiUrl"
    Write-Host "Admin password file: $pwFile  (open it yourself; log in as 'admin')"
    Write-Host 'Starting the FakeNode authority. Leave this window open; Ctrl+C stops everything.'
    Write-Host ''
    # Tee: the console shows the FakeNode output and the same text goes to fakenode.log.
    & $fakeNodeExe authority --server "127.0.0.1:$($Ports.Tcp)" --name 'FakeAuthority' --save-file $authSave 2>&1 | Tee-Object -FilePath $fakeLog
    Write-Host "FakeNode exited with $LASTEXITCODE."
}
finally {
    if ($server -and -not $server.HasExited) {
        Write-Host 'Stopping the server...'
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
}
