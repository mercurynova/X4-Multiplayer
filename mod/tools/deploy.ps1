<#
.SYNOPSIS
  Copies the X4MP extension, the built x4mp.dll and the vendored X4Native into <X4 dir>\extensions\.
.DESCRIPTION
  Installs (replacing older copies of exactly these two folders, nothing else in the X4 folder is touched):
    <X4>\extensions\x4mp\       = mod\extension\x4mp\ + native\x4mp.dll (+ x4mp.pdb with -WithPdb)
    <X4>\extensions\x4native\   = the vendored X4Native runtime (third_party\x4native\<tag>\x4native\),
                                  plus LICENSE next to it
  Refuses to run when the build output is missing or the packaging guard fails (so version_db is always shipped).
  Use -WhatIf to see what would be copied. Settings > Extensions: Protected UI Mode must be OFF in X4.
.PARAMETER X4Dir
  Folder containing X4.exe. Default: auto-detected (Steam default path, then Steam library folders).
.PARAMETER Config
  Which build to deploy: relwithdebinfo (default), release or debug.
.PARAMETER WithPdb
  Also copy x4mp.pdb next to the dll (crash analysis).
.PARAMETER SkipX4Native
  Do not (re)install the vendored X4Native; only update x4mp.
.EXAMPLE
  .\deploy.ps1 -WhatIf
  .\deploy.ps1 -X4Dir "D:\SteamLibrary\steamapps\common\X4 Foundations"
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$X4Dir,
  [ValidateSet('debug', 'release', 'relwithdebinfo')][string]$Config = 'relwithdebinfo',
  [switch]$WithPdb,
  [switch]$SkipX4Native
)
$ErrorActionPreference = 'Stop'
$modDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tag = 'v9.0.0-611726'

function Find-X4Dir {
  $candidates = New-Object System.Collections.Generic.List[string]
  $candidates.Add('C:\Program Files (x86)\Steam\steamapps\common\X4 Foundations')
  $steamRoots = New-Object System.Collections.Generic.List[string]
  foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
    try {
      $p = Get-ItemProperty -Path $key -ErrorAction Stop
      foreach ($name in 'SteamPath', 'InstallPath') { if ($p.$name) { $steamRoots.Add(($p.$name -replace '/', '\')) } }
    } catch { }
  }
  foreach ($root in $steamRoots) {
    $candidates.Add((Join-Path $root 'steamapps\common\X4 Foundations'))
    $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
      foreach ($line in Get-Content $vdf) {
        if ($line -match '"path"\s+"([^"]+)"') {
          $candidates.Add((Join-Path ($Matches[1] -replace '\\\\', '\') 'steamapps\common\X4 Foundations'))
        }
      }
    }
  }
  foreach ($c in $candidates) { if (Test-Path (Join-Path $c 'X4.exe')) { return $c } }
  return $null
}

if (-not $X4Dir) { $X4Dir = Find-X4Dir }
if (-not $X4Dir -or -not (Test-Path (Join-Path $X4Dir 'X4.exe'))) {
  throw 'X4.exe not found. Pass the install folder with -X4Dir (the folder that contains X4.exe).'
}

$dll = Join-Path $modDir "build\msvc-x64-$Config\x4mp.dll"
if (-not (Test-Path $dll)) { throw "x4mp.dll not found at $dll. Run mod\build.ps1 -Config $Config first." }

# Never ship an incomplete X4Native (a missing version_db silently disables all MD hooks).
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'check-packaging.ps1') -ModDir $modDir
if ($LASTEXITCODE -ne 0) { throw 'Packaging check failed; refusing to deploy.' }

$extRoot = Join-Path $X4Dir 'extensions'
$destMp = Join-Path $extRoot 'x4mp'
$destNative = Join-Path $extRoot 'x4native'
$srcMp = Join-Path $modDir 'extension\x4mp'
$srcNative = Join-Path $modDir "third_party\x4native\$tag"

Write-Host "X4 folder : $X4Dir"
Write-Host "x4mp      : $destMp  (dll from $dll)"
if (-not $SkipX4Native) { Write-Host "x4native  : $destNative  ($tag)" }

function Install-Folder([string]$source, [string]$dest) {
  if (Test-Path $dest) { Remove-Item -Path $dest -Recurse -Force }
  Copy-Item -Path $source -Destination $dest -Recurse -Force
}

if ($PSCmdlet.ShouldProcess($extRoot, 'Install x4mp (and X4Native)')) {
  if (-not (Test-Path $extRoot)) { New-Item -ItemType Directory -Path $extRoot | Out-Null }

  Install-Folder $srcMp $destMp
  Get-ChildItem $destMp -Recurse -Filter '.gitkeep' -File | Remove-Item -Force
  New-Item -ItemType Directory -Force (Join-Path $destMp 'native') | Out-Null
  Copy-Item $dll (Join-Path $destMp 'native\x4mp.dll') -Force
  if ($WithPdb) {
    $pdb = [IO.Path]::ChangeExtension($dll, '.pdb')
    if (Test-Path $pdb) { Copy-Item $pdb (Join-Path $destMp 'native\x4mp.pdb') -Force }
  }

  if (-not $SkipX4Native) {
    Install-Folder (Join-Path $srcNative 'x4native') $destNative
    Copy-Item (Join-Path $srcNative 'LICENSE') (Join-Path $destNative 'LICENSE') -Force
  }

  foreach ($d in $destMp, $destNative) {
    if (Test-Path $d) { Get-ChildItem $d -Recurse -File | ForEach-Object { Unblock-File -Path $_.FullName } }
  }
  Write-Host 'Deployed. In X4: Settings > Extensions: Protected UI Mode OFF; enable X4Native and X4 Multiplayer.'
  Write-Host 'Logs: %USERPROFILE%\Documents\Egosoft\X4\<id>\x4native\ (x4native.log, x4mp.log).'
}
