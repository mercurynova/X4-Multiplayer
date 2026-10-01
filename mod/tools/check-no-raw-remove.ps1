<#
.SYNOPSIS
  Guard: the game's component-removal calls may only appear in game/safe_remove.* (mod-design 6.1).
.DESCRIPTION
  Fails (exit 1) if "RemoveComponent" or "SelfDestructComponent" appears in any C/C++ source under -Root
  (default mod/native) except game/safe_remove.h and game/safe_remove.cpp. mod/spikes is outside -Root and is
  deliberately not scanned. Run by CTest as guard.no_raw_remove.
#>
[CmdletBinding()]
param(
  [string]$Root
)
$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\native' }
if (-not (Test-Path $Root)) { Write-Error "Root not found: $Root"; exit 2 }
$Root = (Resolve-Path $Root).Path
$allowed = @('game\safe_remove.h', 'game\safe_remove.cpp')
$extensions = '.h', '.hpp', '.hh', '.hxx', '.inl', '.inc', '.c', '.cc', '.cpp', '.cxx', '.ixx'
$pattern = 'RemoveComponent|SelfDestructComponent'

$violations = @()
Get-ChildItem -Path $Root -Recurse -File | Where-Object { $extensions -contains $_.Extension.ToLowerInvariant() } | ForEach-Object {
  $rel = $_.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('/', '\')
  if ($allowed -contains $rel.ToLowerInvariant()) { return }
  Select-String -Path $_.FullName -Pattern $pattern | ForEach-Object {
    $violations += "{0}:{1}: {2}" -f $rel, $_.LineNumber, $_.Line.Trim()
  }
}
if ($violations.Count -gt 0) {
  Write-Host 'FAIL: raw component removal outside game/safe_remove.*; call x4mp::game::safe_remove() instead:'
  $violations | ForEach-Object { Write-Host "  $_" }
  exit 1
}
Write-Host 'OK: no RemoveComponent / SelfDestructComponent outside game/safe_remove.*'
exit 0