<#
.SYNOPSIS
  Removes the X4MP mod from X4: Foundations. Saves are never touched.
.DESCRIPTION
  Deletes extensions\x4mp and, unless -KeepX4Native is given, extensions\x4native. Nothing else in
  the game folder changes, and no save, profile or screenshot is read or written.

  With -RemoveData the mod's own folder in %LOCALAPPDATA%\X4MP is deleted too: that is the log,
  the settings and the server data of a server started from this machine.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File Uninstall.ps1
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File Uninstall.ps1 -KeepX4Native -RemoveData
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$GameDir,
  [switch]$KeepX4Native,
  [switch]$RemoveData,
  [switch]$Force,
  [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'Common.ps1')

Write-Head 'X4MP uninstall'
if (-not $GameDir) {
  $found = @(Get-X4DirsFound)
  if ($found.Count -eq 0) {
    Write-Host 'X4: Foundations was not found, so there is nothing to remove here.'
    Write-Host 'If the game lives elsewhere, run: Uninstall.bat "<game folder>"'
    exit 1
  }
  $GameDir = $found[0]
}
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'X4.exe'))) {
  Write-Host ('No X4.exe in {0} - that is not the game folder.' -f $GameDir)
  exit 1
}
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
Write-Host ('Game: {0}' -f $GameDir)

$running = @(Get-X4Running)
if ($running.Count -gt 0 -and -not $Force) {
  Write-Host ''
  Write-Host 'X4 is running. Close it and run this again: the dll is loaded and cannot be deleted.'
  exit 1
}

$targets = New-Object System.Collections.Generic.List[string]
[void]$targets.Add((Join-Path $GameDir 'extensions\x4mp'))
if (-not $KeepX4Native) { [void]$targets.Add((Join-Path $GameDir 'extensions\x4native')) }
if ($RemoveData) { [void]$targets.Add((Join-Path $env:LOCALAPPDATA 'X4MP')) }

Write-Host ''
foreach ($t in $targets) {
  if (-not (Test-Path -LiteralPath $t)) {
    Write-Host ('  already gone: {0}' -f $t)
    continue
  }
  if ($PSCmdlet.ShouldProcess($t, 'remove')) {
    Remove-Item -LiteralPath $t -Recurse -Force
    Write-Host ('  removed: {0}' -f $t)
  }
}

Write-Host ''
Write-Host 'Done. Your saves, profiles and screenshots were not touched.'
$accounts = @(Get-X4AccountDirs (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Egosoft\X4'))
if ($accounts.Count -gt 0) { Write-Host ('Saves stay in: {0}\save' -f $accounts[0]) }
if (-not $KeepX4Native) {
  Write-Host 'X4Native was removed too. Another mod that needs it would have to be reinstalled.'
} else {
  Write-Host 'X4Native was left in place.'
}
if (-not $RemoveData) {
  Write-Host 'The mod log and settings stay in %LOCALAPPDATA%\X4MP; add -RemoveData to delete them as well.'
}
Write-Host ''
Write-Host 'The extension is still listed in the profile content.xml, which is harmless: the game ignores'
Write-Host 'an entry whose folder is gone. Untick it in Extensions if you prefer the list clean.'
if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') { Write-Host ''; Read-Host 'Press Enter to close' | Out-Null }
exit 0
