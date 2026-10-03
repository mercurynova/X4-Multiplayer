<#
.SYNOPSIS
  Session 3 (criterion 9): uploads one of your saves to the running local server through the admin REST API, then creates and starts a
  session from it, so a real X4 that joins as the AUTHORITY loads that save.
.DESCRIPTION
  Same calls the GUI Sessions page makes: POST /api/v1/saves/uploads, PUT the bytes (one chunk, Content-Range), POST .../complete,
  then POST /api/v1/sessions {saveId = the save's SHA-256} and POST /api/v1/sessions/<id>/start. The original save file is only read.
  Needs the server from start-fake-clients.ps1 (or any server on the ports below) and its admin password in
  out\session3\admin-password.txt (written by the start scripts), or pass -AdminPassword. Never prints the password.
  Supports -WhatIf (prints the plan, changes nothing).
.PARAMETER SaveName   Save file name without .xml.gz (a file in the X4 save folder), or pass -Path.
.PARAMETER Path       A save file anywhere.
.PARAMETER SessionName  Name of the session to create (default "Session 3").
.PARAMETER NoSession  Upload only (the save shows up in the GUI Sessions page > Saves library).
.PARAMETER AdminPassword  Admin password when out\session3\admin-password.txt does not exist.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SaveName,
    [string]$Path,
    [string]$UserId,
    [string]$SessionName = 'Session 3',
    [switch]$NoSession,
    [string]$AdminPassword,
    [int]$HttpPort = 0
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
if ($HttpPort) { $Ports.Http = $HttpPort }
$dry = [bool]$WhatIfPreference

if (-not $Path) {
    if (-not $SaveName) { throw 'Pass -SaveName <name without .xml.gz> (a file in the X4 save folder) or -Path <file>.' }
    $user = Resolve-X4UserDir $UserId -AllowMissing:$dry
    $Path = Join-Path (Join-Path $user 'save') (($SaveName -replace '\.xml\.gz$', '') + '.xml.gz')
}
if (-not (Test-Path $Path)) {
    $msg = "Save not found: $Path"
    if ($dry) { Write-Warning "$msg (a real run would stop here)"; $size = 0; $sha = '<sha256>' } else { throw $msg }
}
else {
    $size = ([IO.FileInfo]$Path).Length
    $hasher = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { $sha = ([BitConverter]::ToString($hasher.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() } finally { $stream.Dispose(); $hasher.Dispose() }
}
$url = "http://127.0.0.1:$($Ports.Http)"
Write-Host "Save   : $Path ($([math]::Round($size / 1MB, 1)) MB, sha256 $sha)"
Write-Host "Server : $url"
Write-Host "Plan   : upload, then $(if ($NoSession) { 'stop' } else { "create session '$SessionName' from it and start it" })"
if (-not $PSCmdlet.ShouldProcess($url, 'Upload the save' + $(if (-not $NoSession) { ' and start a session' }))) { return }

$h = @{ 'X-X4MP' = '1' }
if ($AdminPassword) {
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/auth/login" -Headers $h -WebSession $s -ContentType 'application/json' -Body (@{ username = 'admin'; password = $AdminPassword } | ConvertTo-Json)
}
else { $s = Initialize-AdminSession }

$bytes = [IO.File]::ReadAllBytes($Path)
$name = (Split-Path $Path -Leaf)
$begin = Invoke-RestMethod -Method Post -Uri "$url/api/v1/saves/uploads" -Headers $h -WebSession $s -ContentType 'application/json' -Body (@{ fileName = $name; size = $bytes.Length; sha256 = $sha } | ConvertTo-Json)
$hdr = @{ 'X-X4MP' = '1'; 'Content-Range' = "bytes 0-$($bytes.Length - 1)/$($bytes.Length)" }
# The server answers with the bytes it already has: the whole save when it is already in the library (re-runs), so only send the rest.
if ($begin.receivedBytes -lt $bytes.Length) {
    $from = [long]$begin.receivedBytes
    $hdr['Content-Range'] = "bytes $from-$($bytes.Length - 1)/$($bytes.Length)"
    $rest = if ($from -eq 0) { $bytes } else { $bytes[$from..($bytes.Length - 1)] }
    $null = Invoke-RestMethod -Method Put -Uri "$url/api/v1/saves/uploads/$($begin.uploadId)" -Headers $hdr -WebSession $s -ContentType 'application/octet-stream' -Body $rest
}
else { Write-Host 'The server already has this save; skipping the upload.' }
$null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/saves/uploads/$($begin.uploadId)/complete" -Headers $h -WebSession $s -ContentType 'application/json' -Body '{}'
Write-Host "Uploaded. The save is now in the GUI Sessions page > Saves library (sha256 $sha)."
if ($NoSession) { return }

$created = Invoke-RestMethod -Method Post -Uri "$url/api/v1/sessions" -Headers $h -WebSession $s -ContentType 'application/json' -Body (@{ name = $SessionName; saveId = $sha } | ConvertTo-Json)
$null = Invoke-RestMethod -Method Post -Uri "$url/api/v1/sessions/$($created.id)/start" -Headers $h -WebSession $s -ContentType 'application/json' -Body '{}'
Write-Host "Session '$SessionName' (id $($created.id)) created from the upload and started: it waits for an authority."
Write-Host 'Now in X4: Multiplayer > Join, click the "Host this session as the authority" button until it says Yes, and enter the admin password that start-fake-clients.ps1 printed.'
