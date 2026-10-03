<#
.SYNOPSIS
  Session 3: zips the logs into out\session3\logs[-<label>]-<timestamp>.zip. Uploads nothing.
.DESCRIPTION
  Collects (what exists): the X4 -logfile (x4mp_s3.log by default), the x4native\ folder of the X4 user folder, the X4MP config folder
  Documents\Egosoft\X4\x4mp (x4mp.json, logs\x4mp.log, authority-saves.json; never launch.json), the server log files
  (out\session3\data\logs\), the server console logs and the FakeNode output. Never includes admin passwords, the database, uidata.xml
  or any save. Supports -WhatIf. X4 may overwrite its -logfile each time it starts: run this every time you quit X4, before starting it
  again. -Label puts a word into the zip name (for example -Label client-run).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$UserId,
    [string]$GameLogName = 'x4mp_s3.log',
    [string]$Label = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$dry = [bool]$WhatIfPreference
$user = Resolve-X4UserDir $UserId -AllowMissing:$dry
$items = New-Object System.Collections.Generic.List[object]
function Add-Item([string]$path, [string]$entry) {
    if (Test-Path $path) { $items.Add(@{ Path = $path; Entry = $entry }) } else { Write-Host "  (missing, skipped) $path" }
}
Add-Item (Join-Path $user $GameLogName) $GameLogName
Add-Item (Join-Path $user 'x4native') 'x4native'
Add-Item (Get-X4MPConfigDir) 'x4mp-config'
Add-Item (Join-Path $OutDir 'data\logs') 'server-logs'
foreach ($n in 'fakenode.log', 'fakenode-clients.log', 'server.out.log', 'server.err.log') { Add-Item (Join-Path $OutDir $n) $n }

$labelPart = if ($Label) { '-' + ($Label -replace '[^A-Za-z0-9_.-]', '_') } else { '' }
$zip = Join-Path $OutDir ('logs{0}-{1}.zip' -f $labelPart, (Get-Date -Format 'yyyyMMdd-HHmmss'))
Write-Host "Zip: $zip"
foreach ($i in $items) { Write-Host "  + $($i.Path)" }
if (-not $PSCmdlet.ShouldProcess($zip, 'Create log archive')) { return }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$stage = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-logs-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $stage | Out-Null
function Copy-Shared([string]$from, [string]$to) {
    New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
    $src = [IO.File]::Open($from, 'Open', 'Read', 'ReadWrite')
    try { $dst = [IO.File]::Create($to); try { $src.CopyTo($dst) } finally { $dst.Dispose() } } finally { $src.Dispose() }
}
try {
    foreach ($i in $items) {
        if ((Get-Item $i.Path).PSIsContainer) {
            foreach ($f in Get-ChildItem $i.Path -Recurse -File) {
                if ($f.Extension -in '.gz', '.dll', '.exe', '.db', '.sqlite') { continue }
                if ($f.Name -like 'launch.json*') { continue }   # may hold a session password if X4 never consumed it
                Copy-Shared $f.FullName (Join-Path (Join-Path $stage $i.Entry) $f.FullName.Substring($i.Path.Length).TrimStart('\'))
            }
        }
        else { Copy-Shared $i.Path (Join-Path $stage $i.Entry) }
    }
    Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -like 'initial-admin-password*' -or $_.Name -like 'admin-password*' } | Remove-Item -Force
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
}
finally { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host ('Created {0} ({1:N1} MB). Nothing was uploaded; tell Claude the file name.' -f $zip, ((Get-Item $zip).Length / 1MB))
