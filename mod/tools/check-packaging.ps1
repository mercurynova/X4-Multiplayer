<#
.SYNOPSIS
  Packaging guard: the vendored X4Native must be complete and carry version_db data for the supported build.
.DESCRIPTION
  Fails (exit 1) when any of these is wrong:
    - a required runtime file (x4native_64.dll, x4native_core.dll, content.xml, ui.xml, md/ui/t files, LICENSE,
      native/version_db/{internal_functions,func_history,type_changes}.json) is missing
    - version_db/internal_functions.json is not valid JSON or any function lacks the build key (default
      "900-611726": the key format is "<game version without dot>-<build number>") or has an empty rva
    - a file's SHA-256 differs from the VERSION file's record
    - mod/extension/x4mp/x4native.json does not point at native/x4mp.dll, or content.xml lacks the x4native dependency
  Run by CTest as guard.packaging.
#>
[CmdletBinding()]
param(
  [string]$ModDir,
  [string]$Build = '900-611726',
  [string]$Tag = 'v9.0.0-611726',
  # Override for tests: a copy of the vendored directory.
  [string]$X4NativeDir
)
$ErrorActionPreference = 'Stop'
if (-not $ModDir) { $ModDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..' }
$ModDir = (Resolve-Path $ModDir).Path
if (-not $X4NativeDir) { $X4NativeDir = Join-Path $ModDir "third_party\x4native\$Tag" }
$failures = New-Object System.Collections.Generic.List[string]
function Fail([string]$m) { $failures.Add($m) }

if (-not (Test-Path $X4NativeDir)) { Write-Host "FAIL: vendored X4Native not found: $X4NativeDir"; exit 1 }
$X4NativeDir = (Resolve-Path $X4NativeDir).Path
$rt = Join-Path $X4NativeDir 'x4native'

$required = @(
  'LICENSE', 'VERSION', 'sdk\x4native_extension.h', 'sdk\x4n_core.h',
  'x4native\content.xml', 'x4native\ui.xml', 'x4native\md\x4native_main.xml', 'x4native\t\0001-L044.xml',
  'x4native\ui\x4native.lua', 'x4native\ui\x4n_game_data.lua', 'x4native\ui\x4n_settings_menu.lua',
  'x4native\native\x4native_64.dll', 'x4native\native\x4native_core.dll',
  'x4native\native\version_db\internal_functions.json', 'x4native\native\version_db\func_history.json',
  'x4native\native\version_db\type_changes.json')
foreach ($r in $required) {
  if (-not (Test-Path (Join-Path $X4NativeDir $r))) { Fail "missing vendored file: $r" }
}

# version_db must know the supported build for every internal function.
$idb = Join-Path $rt 'native\version_db\internal_functions.json'
if (Test-Path $idb) {
  try {
    $json = Get-Content $idb -Raw | ConvertFrom-Json
    $fnCount = 0
    foreach ($fn in $json.functions.PSObject.Properties) {
      $fnCount++
      $entry = $fn.Value.PSObject.Properties[$Build]
      if (-not $entry) { Fail "internal_functions.json: '$($fn.Name)' has no entry for build $Build" }
      elseif (-not $entry.Value.rva) { Fail "internal_functions.json: '$($fn.Name)' build $Build has no rva" }
    }
    if ($fnCount -eq 0) { Fail 'internal_functions.json: no functions listed' }
    else { Write-Host "internal_functions.json: $fnCount functions, build key $Build present on all" }
  }
  catch { Fail "internal_functions.json is not valid JSON: $($_.Exception.Message)" }
}

# Hashes recorded in VERSION must match the files on disk (no silent edits of vendored code).
$versionFile = Join-Path $X4NativeDir 'VERSION'
if (Test-Path $versionFile) {
  $recorded = @{}
  foreach ($line in Get-Content $versionFile) {
    if ($line -match '^([0-9a-f]{64})\s+(\d+)\s+(\S.*)$') { $recorded[$Matches[3].Trim()] = @{ Hash = $Matches[1]; Size = [int64]$Matches[2] } }
  }
  if ($recorded.Count -eq 0) { Fail 'VERSION records no file hashes' }
  foreach ($rel in $recorded.Keys) {
    $p = Join-Path $X4NativeDir ($rel.Replace('/', '\'))
    if (-not (Test-Path $p)) { Fail "VERSION lists a file that is missing: $rel"; continue }
    if ((Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant() -ne $recorded[$rel].Hash) { Fail "SHA-256 mismatch: $rel" }
  }
  foreach ($f in Get-ChildItem $X4NativeDir -Recurse -File) {
    $rel = $f.FullName.Substring($X4NativeDir.Length + 1).Replace('\', '/')
    if ($rel -ne 'VERSION' -and -not $recorded.ContainsKey($rel)) { Fail "file not recorded in VERSION: $rel" }
  }
}

# Our own extension package.
$ext = Join-Path $ModDir 'extension\x4mp'
$nj = Join-Path $ext 'x4native.json'
if (-not (Test-Path $nj)) { Fail 'extension/x4mp/x4native.json missing' }
else {
  $lib = (Get-Content $nj -Raw | ConvertFrom-Json).library
  if ($lib -ne 'native/x4mp.dll') { Fail "x4native.json library is '$lib', expected native/x4mp.dll" }
}
$cx = Join-Path $ext 'content.xml'
if (-not (Test-Path $cx)) { Fail 'extension/x4mp/content.xml missing' }
else {
  [xml]$c = Get-Content $cx -Raw
  if ($c.content.id -ne 'x4mp') { Fail 'content.xml id must be x4mp' }
  $dep = @($c.content.dependency) | Where-Object { $_.id -eq 'x4native' -and $_.optional -eq 'false' }
  if (-not $dep) { Fail 'content.xml lacks a non-optional dependency on x4native' }
}

if ($failures.Count -gt 0) {
  Write-Host 'FAIL: packaging check'
  $failures | ForEach-Object { Write-Host "  $_" }
  exit 1
}
Write-Host "OK: vendored X4Native $Tag complete, version_db covers $Build, hashes match"
exit 0