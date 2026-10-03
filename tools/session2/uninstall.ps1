<#
.SYNOPSIS
  Session 2: removes x4native, x4mp_probe and x4mp_spike from <X4 install>\extensions\, then LISTS (never deletes) test saves.
.DESCRIPTION
  Only the three folders install.ps1 created are removed. Afterwards the script lists x4mp_*.xml.gz files and the most recent
  saves in the X4 save folder so you can delete the test saves yourself. Supports -WhatIf.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$X4Dir,
    [string]$UserId
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$dry = [bool]$WhatIfPreference
$X4Dir = Resolve-X4Dir $X4Dir -AllowMissing:$dry
$extRoot = Join-Path $X4Dir 'extensions'
foreach ($name in $ExtensionNames) {
    $dest = Join-Path $extRoot $name
    if (-not (Test-Path $dest)) { Write-Host "Not installed: $dest"; continue }
    if ($PSCmdlet.ShouldProcess($dest, "Remove $name")) {
        Remove-Item -Path $dest -Recurse -Force
        Write-Host "Removed $dest"
    }
}

# List only; deleting saves is the user's call.
try {
    $user = Resolve-X4UserDir $UserId -AllowMissing:$dry
    $saveDir = Join-Path $user 'save'
    Write-Host ''
    Write-Host "Saves in $saveDir that you may want to delete yourself (this script never deletes saves):"
    if (Test-Path $saveDir) {
        $tests = @(Get-ChildItem $saveDir -File -Filter 'x4mp_*.xml.gz')
        $recent = @(Get-ChildItem $saveDir -File -Filter '*.xml.gz' | Where-Object { $_.Name -notlike 'x4mp_*' } | Sort-Object LastWriteTime -Descending | Select-Object -First 10)
        if ($tests.Count) { Write-Host '  Downloaded / test saves (x4mp_*.xml.gz):'; $tests | ForEach-Object { Write-Host ('    {0}  {1:N1} MB  {2}' -f $_.Name, ($_.Length / 1MB), $_.LastWriteTime) } }
        else { Write-Host '  (no x4mp_*.xml.gz files)' }
        if ($recent.Count) { Write-Host '  Most recent other saves (your test slots T/A/B/F are among them):'; $recent | ForEach-Object { Write-Host ('    {0}  {1:N1} MB  {2}' -f $_.Name, ($_.Length / 1MB), $_.LastWriteTime) } }
    }
    else { Write-Host '  (save folder not found)' }
}
catch { Write-Warning "Could not list saves: $($_.Exception.Message)" }
Write-Host ''
Write-Host 'Also: remove -logfile x4mp_s2.log from the Steam launch options; restore your autosave interval.'
