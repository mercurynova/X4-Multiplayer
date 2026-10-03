<#
.SYNOPSIS
  Configure, build and test the native mod (x4mp.dll) (task M0-08). Humans and CI run the same script.

.DESCRIPTION
  1. Imports the MSVC x64 developer environment (vcvars64.bat) so cl, cmake and ninja are on PATH.
  2. Makes sure the pinned flatc exists (tools/flatc/bin, via fetch-flatc.ps1).
  3. Finds vcpkg: $env:VCPKG_ROOT if set (CI), else a cached clone in %LOCALAPPDATA%\x4mp\vcpkg that is
     created on first use and pinned to the "builtin-baseline" of vcpkg.json. Dependencies (FlatBuffers
     25.2.10, Catch2, nlohmann-json) are built by vcpkg manifest mode, triplet x64-windows-static.
  4. cmake --preset msvc-x64-<config>, cmake --build, ctest. Any warning in our code fails the build.

.PARAMETER Config
  debug, release or relwithdebinfo (default).
.PARAMETER Clean
  Delete the build directory first.
.PARAMETER NoTest
  Configure and build only.
.PARAMETER Filter
  Passed to ctest -R.
.PARAMETER Spikes
  Also build the throwaway in-game probe (x4mp_probe.dll + its tests, CMake option X4MP_SPIKES). Without the switch
  the option is forced OFF, so a plain build is identical whether or not an earlier -Spikes build used the same directory.

.EXAMPLE
  powershell -NoProfile -File mod/build.ps1
  powershell -NoProfile -File mod/build.ps1 -Config release -Clean
#>
[CmdletBinding()]
param(
  [ValidateSet('debug', 'release', 'relwithdebinfo')][string]$Config = 'relwithdebinfo',
  [switch]$Clean,
  [switch]$NoTest,
  [string]$Filter,
  [switch]$Spikes
)

$ErrorActionPreference = 'Stop'
$cppDir = $PSScriptRoot   # the mod directory (holds CMakeLists.txt / CMakePresets.json / vcpkg.json)
$repoRoot = (Resolve-Path (Join-Path $cppDir '..')).Path
$preset = "msvc-x64-$Config"

function Invoke-Checked([string]$what, [scriptblock]$cmd) {
  & $cmd
  if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

# ---- 1. MSVC developer environment ---------------------------------------------------------
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
  $vcvars = $null
  $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
  if (Test-Path $vswhere) {
    $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($install) { $vcvars = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat' }
  }
  if (-not $vcvars -or -not (Test-Path $vcvars)) {
    $vcvars = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
  }
  if (-not (Test-Path $vcvars)) { throw 'MSVC (Visual Studio 2022 C++ build tools) not found; see docs/dev-setup.md.' }
  Write-Host "Importing MSVC environment from $vcvars"
  $lines = cmd /c "`"$vcvars`" >nul 2>&1 && set"
  foreach ($line in $lines) {
    if ($line -match '^([^=]+)=(.*)$') { Set-Item -Path "env:$($Matches[1])" -Value $Matches[2] }
  }
}
foreach ($tool in 'cl.exe', 'cmake.exe', 'ninja.exe') {
  if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool not found on PATH after loading the MSVC environment." }
}

# ---- 2. flatc -------------------------------------------------------------------------------
$flatc = Join-Path $repoRoot 'tools/flatc/bin/flatc.exe'
if (-not (Test-Path $flatc)) {
  Write-Host 'Fetching pinned flatc'
  Invoke-Checked 'fetch-flatc' { powershell -NoProfile -File (Join-Path $repoRoot 'tools/flatc/fetch-flatc.ps1') }
}

# ---- 3. vcpkg -------------------------------------------------------------------------------
$baseline = (Get-Content (Join-Path $cppDir 'vcpkg.json') -Raw | ConvertFrom-Json).'builtin-baseline'
if (-not $env:VCPKG_ROOT) {
  $env:VCPKG_ROOT = Join-Path $env:LOCALAPPDATA 'x4mp\vcpkg'
  if (-not (Test-Path (Join-Path $env:VCPKG_ROOT '.git'))) {
    # Full history (about 200 MB), not a partial clone: vcpkg runs git with a scrubbed environment, which
    # breaks lazy fetching from a promisor remote.
    Write-Host "Cloning vcpkg into $env:VCPKG_ROOT"
    New-Item -ItemType Directory -Force (Split-Path $env:VCPKG_ROOT) | Out-Null
    Invoke-Checked 'git clone vcpkg' { git clone --no-checkout https://github.com/microsoft/vcpkg.git $env:VCPKG_ROOT }
  }
  $head = (git -C $env:VCPKG_ROOT rev-parse HEAD 2>$null)
  if ($head -ne $baseline) {
    Write-Host "Checking out vcpkg baseline $baseline"
    git -C $env:VCPKG_ROOT cat-file -e "$baseline^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { Invoke-Checked 'git fetch vcpkg' { git -C $env:VCPKG_ROOT fetch origin $baseline } }
    Invoke-Checked 'git checkout vcpkg' { git -C $env:VCPKG_ROOT checkout -q $baseline }
  }
}
if (-not (Test-Path (Join-Path $env:VCPKG_ROOT 'vcpkg.exe'))) {
  Write-Host 'Bootstrapping vcpkg'
  Invoke-Checked 'bootstrap-vcpkg' { & (Join-Path $env:VCPKG_ROOT 'bootstrap-vcpkg.bat') -disableMetrics }
}
$env:VCPKG_DISABLE_METRICS = '1'

# ---- 4. configure, build, test --------------------------------------------------------------
Push-Location $cppDir
try {
  if ($Clean) { Remove-Item -Recurse -Force (Join-Path $cppDir "build/$preset") -ErrorAction SilentlyContinue }
  $spikesFlag = if ($Spikes) { '-DX4MP_SPIKES=ON' } else { '-DX4MP_SPIKES=OFF' }
  Invoke-Checked 'cmake configure' { cmake --preset $preset $spikesFlag }
  Invoke-Checked 'cmake build' { cmake --build --preset $preset }
  if ($Spikes) {
    $probe = Get-ChildItem (Join-Path $cppDir "build/$preset") -Recurse -Filter x4mp_probe.dll | Select-Object -First 1
    if ($probe) { Write-Host "x4mp_probe.dll: $($probe.FullName)" }
  }
  if (-not $NoTest) {
    if ($Filter) { Invoke-Checked 'ctest' { ctest --preset $preset -R $Filter } }
    else { Invoke-Checked 'ctest' { ctest --preset $preset } }
  }
}
finally {
  Pop-Location
}
