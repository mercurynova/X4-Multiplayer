<#
.SYNOPSIS
  Session 3: removes x4mp and x4native from <X4 install>\extensions\, then LISTS (never deletes) saves the session created.
.DESCRIPTION
  Removes only the two folders install.ps1 created. Afterwards it lists x4mp_*.xml.gz files (downloaded session saves,
  authority checkpoints) in the X4 save folder and the X4MP config folder (Documents\Egosoft\X4\x4mp) so you can delete them yourself.
  Supports -WhatIf.
#>
[CmdletBinding(SupportsShouldProcess)]
param([string]$X4Dir, [string]$UserId)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$dry = [bool]$WhatIfPreference
$X4Dir = Resolve-X4Dir $X4Dir -AllowMissing:$dry
foreach ($name in $ExtensionNames) {
    $dest = Join-Path (Join-Path $X4Dir 'extensions') $name
    if (-not (Test-Path $dest)) { Write-Host "Not installed: $dest"; continue }
    if ($PSCmdlet.ShouldProcess($dest, "Remove $name")) { Remove-Item -Path $dest -Recurse -Force; Write-Host "Removed $dest" }
}
try {
    $user = Resolve-X4UserDir $UserId -AllowMissing:$dry
    $saveDir = Join-Path $user 'save'
    Write-Host ''
    Write-Host "Saves in $saveDir that you may want to delete yourself (this script never deletes saves):"
    if (Test-Path $saveDir) {
        $tests = @(Get-ChildItem $saveDir -File -Filter 'x4mp_*.xml.gz')
        if ($tests.Count) { $tests | ForEach-Object { Write-Host ('    {0}  {1:N1} MB  {2}' -f $_.Name, ($_.Length / 1MB), $_.LastWriteTime) } } else { Write-Host '  (no x4mp_*.xml.gz files)' }
    }
    else { Write-Host '  (save folder not found)' }
}
catch { Write-Warning "Could not list saves: $($_.Exception.Message)" }
Write-Host ''
Write-Host "X4MP's own files (settings, logs, authority bookkeeping) are in $(Get-X4MPConfigDir); delete the folder when you are done."
Write-Host 'Also: remove -logfile x4mp_s3.log from the Steam launch options; restore your autosave interval.'
