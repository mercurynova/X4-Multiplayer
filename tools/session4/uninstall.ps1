<#
.SYNOPSIS
  Session 4: removes x4mp and x4native from <X4 install>\extensions\, then LISTS (never deletes) saves the session created.
.DESCRIPTION
  Removes only the two folders install.ps1 created (and, with -RemoveKit, also the sitting-0 kit folders x4mp_probe / x4mp_spike when they are
  still there). Afterwards it lists x4mp_*.xml.gz files (downloaded session saves, authority checkpoints) in the X4 save folder and tells you where
  the X4MP config folder is (%LocalAppData%\X4MP) so you can delete them yourself. Never touches saves. Supports -WhatIf.
.PARAMETER X4Dir      Folder containing X4.exe (default: auto-detected via Steam).
.PARAMETER UserId     The numeric folder under Documents\Egosoft\X4 (only needed when there are several).
.PARAMETER RemoveKit  Also remove x4mp_probe and x4mp_spike (sitting-0 kit) if present.
.PARAMETER Force      Continue while X4 is running (not recommended).
#>
[CmdletBinding(SupportsShouldProcess)]
param([string]$X4Dir, [string]$UserId, [switch]$RemoveKit, [switch]$Force)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$dry = [bool]$WhatIfPreference
$X4Dir = Resolve-X4Dir $X4Dir -AllowMissing:$dry
if (-not $Force -and (Test-X4Running)) {
    if ($dry) { Write-Warning 'X4 is running (a real run would stop here; quit X4 first).' }
    else { throw 'X4 is running. Quit X4 first (it keeps the extension files open), or pass -Force.' }
}
$names = @($ExtensionNames)
if ($RemoveKit) { $names += $ForbiddenExtensions }
foreach ($name in $names) {
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
Write-Host "X4MP's own files (settings, logs, authority bookkeeping, avatar records) are in $(Get-X4MPConfigDir); delete the folder when you are done."
Write-Host 'Also: remove -logfile x4mp_s4.log from the Steam launch options; restore your autosave interval.'
