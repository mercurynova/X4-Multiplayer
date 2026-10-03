<#
.SYNOPSIS
  Session 2: zips the logs into out\session2\logs-<timestamp>.zip. Uploads nothing.
.DESCRIPTION
  Collects (what exists): the X4 -logfile (x4mp_s2.log by default), the x4native\ folder of the X4 user folder, the server log
  files (out\session2\data\logs\), the server console logs and the FakeNode output (out\session2\fakenode.log). Never includes
  initial-admin-password.txt, the database or any save. Supports -WhatIf.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$UserId,
    [string]$GameLogName = 'x4mp_s2.log'
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
Add-Item (Join-Path $OutDir 'data\logs') 'server-logs'
Add-Item (Join-Path $OutDir 'fakenode.log') 'fakenode.log'
Add-Item (Join-Path $OutDir 'server.out.log') 'server.out.log'
Add-Item (Join-Path $OutDir 'server.err.log') 'server.err.log'

$zip = Join-Path $OutDir ('logs-{0}.zip' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
Write-Host "Zip: $zip"
foreach ($i in $items) { Write-Host "  + $($i.Path)" }
if ($PSCmdlet.ShouldProcess($zip, 'Create log archive')) {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-logs-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $stage | Out-Null
    try {
        foreach ($i in $items) {
            # Copy first (not zip in place): the game or server may still hold the file open.
            if ((Get-Item $i.Path).PSIsContainer) {
                New-Item -ItemType Directory -Force (Join-Path $stage $i.Entry) | Out-Null
                foreach ($f in Get-ChildItem $i.Path -Recurse -File) {
                    $rel = $f.FullName.Substring($i.Path.Length).TrimStart('\')
                    $target = Join-Path (Join-Path $stage $i.Entry) $rel
                    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
                    $src = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
                    try { $dst = [IO.File]::Create($target); try { $src.CopyTo($dst) } finally { $dst.Dispose() } } finally { $src.Dispose() }
                }
            }
            else {
                $src = [IO.File]::Open($i.Path, 'Open', 'Read', 'ReadWrite')
                try { $dst = [IO.File]::Create((Join-Path $stage $i.Entry)); try { $src.CopyTo($dst) } finally { $dst.Dispose() } } finally { $src.Dispose() }
            }
        }
        Get-ChildItem $stage -Recurse -File -Filter 'initial-admin-password*' | Remove-Item -Force
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
    }
    finally { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host ('Created {0} ({1:N1} MB). Nothing was uploaded; tell Claude the file name.' -f $zip, ((Get-Item $zip).Length / 1MB))
}
