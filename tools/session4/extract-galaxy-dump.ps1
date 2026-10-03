<#
.SYNOPSIS
  Session 4, S13.11: rebuilds galaxy-dump.json from the chunks the spike block galaxy_dump wrote into the X4 game log.
.DESCRIPTION
  The spike writes Documents\Egosoft\X4\x4mp\galaxy-dump.json itself when X4's Lua can write files; either way it also logs the JSON
  in chunks ("[X4MP-SPIKE] S13.11 DATA part=i/n json=..."). This script finds the last complete set in the log, checks that it is valid
  JSON with >= 140 sectors and writes it to out\session4\galaxy-dump.json (the file the FakeNode takes with --galaxy-file in sittings
  1-2). collect-logs.ps1 does the same automatically; use this script when you only want the dump. Supports -WhatIf.
.PARAMETER LogPath  The game log (default: <X4 user folder>\x4mp_s4.log).
.PARAMETER Out      Output file (default out\session4\galaxy-dump.json).
.PARAMETER UserId   The numeric folder under Documents\Egosoft\X4 (only needed when there are several).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$LogPath,
    [string]$Out,
    [string]$UserId
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$dry = [bool]$WhatIfPreference
if (-not $LogPath) { $LogPath = Join-Path (Resolve-X4UserDir $UserId -AllowMissing:$dry) $GameLogDefault }
if (-not $Out) { $Out = Join-Path $OutDir 'galaxy-dump.json' }
Write-Host "Game log: $LogPath"
$json = Convert-GalaxyDumpFromLog $LogPath
if (-not $json) {
    if ($dry) { Write-Warning 'No galaxy DATA lines found (a real run would stop here).'; return }
    throw "No complete S13.11 DATA set in $LogPath. Run the spike block galaxy_dump (run-block.ps1 galaxy_dump) and quit X4 first."
}
$check = Test-GalaxyDumpJson $json
Write-Host "Rebuilt: $($check.Message)"
if (-not $check.Ok) { Write-Warning 'The dump has fewer sectors than expected (140): is every DLC enabled? Send the log anyway.' }
if ($PSCmdlet.ShouldProcess($Out, 'Write galaxy-dump.json')) {
    New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
    [IO.File]::WriteAllText($Out, $json)
    Write-Host "Written $Out"
}
