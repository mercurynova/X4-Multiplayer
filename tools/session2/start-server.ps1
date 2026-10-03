<#
.SYNOPSIS
  Session 2: starts the local X4MP server and a FakeNode authority that offers one of your X4 saves as the session save.
.DESCRIPTION
  1. Finds <Documents>\Egosoft\X4\<numeric id>\save\<SaveName>.xml.gz (the id is picked automatically when there is exactly one;
     with several, pass -UserId).
  2. Copies it (verbatim) to out\session2\authority-save\ (your original is never touched).
  3. Publishes the server and FakeNode when missing (dotnet publish, Release), starts the server with
     --data-dir out\session2\data on the default ports (TCP 47780, UDP 47781, HTTP 47790).
  4. Runs `fakenode authority --save-file <copy>` in this window and tees its output to out\session2\fakenode.log.
  5. Ctrl+C (or closing the window) stops both. Prints the GUI address and WHERE the admin password file is (never the password).
  With -WhatIf nothing is copied, built or started; the plan is printed.
.PARAMETER SaveName  Save file name without .xml.gz (for example save_012).
.PARAMETER UserId    The numeric folder under Documents\Egosoft\X4 (only needed when there are several).
.PARAMETER Rebuild   Publish the server and FakeNode again even if they exist.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$SaveName,
    [string]$UserId,
    [switch]$Rebuild
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$dry = [bool]$WhatIfPreference
$SaveName = $SaveName -replace '\.xml\.gz$', ''
$user = Resolve-X4UserDir $UserId -AllowMissing:$dry
$source = Join-Path (Join-Path $user 'save') "$SaveName.xml.gz"
if (-not (Test-Path $source)) {
    $msg = "Save not found: $source"
    $dir = Split-Path $source
    if (Test-Path $dir) {
        $newest = Get-ChildItem $dir -File -Filter '*.xml.gz' | Sort-Object LastWriteTime -Descending | Select-Object -First 8
        $msg += "`nNewest saves there: " + (($newest | ForEach-Object { $_.BaseName -replace '\.xml$', '' }) -join ', ')
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
Write-Host "Server    : $serverExe  --data-dir $data --port $($Ports.Http)   (TCP $($Ports.Tcp), UDP $($Ports.Udp))"
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
        Invoke-Native 'fetch-flatc' { & powershell -NoProfile -File (Join-Path $Repo 'tools\flatc\fetch-flatc.ps1') }
    }
    Invoke-Native 'dotnet publish (server)' { dotnet publish (Join-Path $Repo 'server\src\X4MP.Server') -c Release '-p:PublishProfile=win-x64' -p:SkipWebBuild=true -nologo -v:m }
    Invoke-Native 'dotnet build (FakeNode)' { dotnet build (Join-Path $Repo 'tools\X4MP.FakeNode') -c Release -o (Join-Path $Repo 'out\fakenode') -nologo -v:m }
}

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
