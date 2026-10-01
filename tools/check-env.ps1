<#
.SYNOPSIS
  Checks that this machine has what X4MP development/testing needs. Read-only.
  See docs/dev-setup.md for what each item is for and how to install it.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\check-env.ps1
  powershell -ExecutionPolicy Bypass -File tools\check-env.ps1 -X4Dir "D:\Games\X4 Foundations"
#>
param(
    [string]$X4Dir = "C:\Program Files (x86)\Steam\steamapps\common\X4 Foundations"
)

$script:fail = 0
function Report([string]$area, [string]$item, [bool]$ok, [string]$detail, [string]$fix) {
    $mark = if ($ok) { "OK  " } else { "MISS" }
    $color = if ($ok) { "Green" } else { "Yellow" }
    Write-Host ("[{0}] {1,-8} {2,-34} {3}" -f $mark, $area, $item, $detail) -ForegroundColor $color
    if (-not $ok) { $script:fail++; if ($fix) { Write-Host "         fix: $fix" -ForegroundColor DarkGray } }
}
function Cmd([string]$name) { Get-Command $name -ErrorAction SilentlyContinue }

$root = Split-Path -Parent $PSScriptRoot

# --- Core ---
foreach ($t in @(@("git", "winget install Git.Git"), @("gh", "winget install GitHub.cli"), @("python", "winget install Python.Python.3.12"))) {
    $c = Cmd $t[0]
    Report "core" $t[0] ([bool]$c) $(if ($c) { $c.Source } else { "" }) $t[1]
}

# --- Server / web ---
$sdks = @()
if (Cmd dotnet) { $sdks = @(dotnet --list-sdks 2>$null) }
$wantMajor = "10"
$gj = Join-Path $root "global.json"
if (Test-Path $gj) { try { $wantMajor = ((Get-Content $gj -Raw | ConvertFrom-Json).sdk.version -split '\.')[0] } catch {} }
$hasSdk = [bool]($sdks | Where-Object { $_ -match "^$wantMajor\." })
Report "server" ".NET SDK $wantMajor.x" $hasSdk ($(if ($sdks) { ($sdks -join '; ') } else { "no SDKs found" })) "winget install Microsoft.DotNet.SDK.$wantMajor"
$node = Cmd node
$nodeVer = if ($node) { (node --version) } else { "" }
Report "web" "Node.js >= 22" ($node -and ([int]($nodeVer.TrimStart('v').Split('.')[0]) -ge 22)) $nodeVer "winget install OpenJS.NodeJS.LTS"

# --- Native ---
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = $null
if (Test-Path $vswhere) { $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null }
Report "native" "MSVC v143 (VS 2022 C++)" ([bool]$vsPath) $(if ($vsPath) { $vsPath } else { "" }) 'winget install Microsoft.VisualStudio.2022.BuildTools --override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"'
$cmake = Cmd cmake
if (-not $cmake -and $vsPath) { $cmake = Get-Item "$vsPath\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" -ErrorAction SilentlyContinue }
Report "native" "CMake >= 3.20" ([bool]$cmake) $(if ($cmake) { "found (use Developer PowerShell for VS)" } else { "" }) "comes with the VS C++ workload"
$vcpkg = $env:VCPKG_ROOT
if (-not $vcpkg -and $vsPath -and (Test-Path "$vsPath\VC\vcpkg\vcpkg.exe")) { $vcpkg = "$vsPath\VC\vcpkg" }
Report "native" "vcpkg" ([bool]$vcpkg) $(if ($vcpkg) { $vcpkg } else { "" }) "comes with VS 17.6+, or clone microsoft/vcpkg and set VCPKG_ROOT"
$flatc = Join-Path $root "tools\flatc"
Report "native" "flatc (pinned, repo-fetched)" (Test-Path (Join-Path $flatc "flatc.exe")) "" "run the tools/flatc fetch script (added in task M0-02)"

# --- Game ---
$x4exe = Join-Path $X4Dir "X4.exe"
$hasX4 = Test-Path $x4exe
Report "game" "X4 install" $hasX4 $X4Dir "pass -X4Dir <path> if installed elsewhere"
if ($hasX4) {
    $ver = (Get-Content (Join-Path $X4Dir "version.dat") -ErrorAction SilentlyContinue | Select-Object -First 1)
    Report "game" "X4 version 900 (9.00)" ($ver -eq "900") "version.dat=$ver" "the mod is pinned to 9.00 build 611726; disable Steam auto-update"
    $dlcs = (Get-ChildItem (Join-Path $X4Dir "extensions") -Directory -Filter "ego_dlc_*" -ErrorAction SilentlyContinue).Name -join ", "
    Report "game" "DLCs (must match across players)" $true $dlcs ""
}
$vcr = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" -ErrorAction SilentlyContinue
Report "game" "VC++ 2015-2022 x64 redist" ([bool]($vcr -and $vcr.Installed -eq 1)) $(if ($vcr) { $vcr.Version } else { "" }) "winget install Microsoft.VCRedist.2015+.x64"
$userX4 = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "Egosoft\X4"
$userExt = Join-Path $userX4 "extensions"
$thirdParty = @(Get-ChildItem $userExt -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin @("x4mp", "x4native") })
Report "game" "no third-party user mods" ($thirdParty.Count -eq 0) $(if ($thirdParty) { ($thirdParty.Name -join ", ") } else { $userExt }) "disable them in-game or move them out while testing"
$saveDirs = @(Get-ChildItem $userX4 -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName "save") })
Report "game" "user data / saves folder" ($saveDirs.Count -gt 0) $(if ($saveDirs) { ($saveDirs | ForEach-Object { Join-Path $_.FullName "save" }) -join "; " } else { "" }) "launch X4 once"
Write-Host "         note: Protected UI mode must be OFF (Settings > Extensions); this script can't check it." -ForegroundColor DarkGray

# --- Local-only folders ---
Report "repo" "reference/ (old mod, read-only)" (Test-Path (Join-Path $root "reference\README.md")) "" "git clone --depth 1 <previous-multiplayer-mod-repo> reference"
Report "repo" "x4-unpacked/ (game API reference)" (Test-Path (Join-Path $root "x4-unpacked\ui")) "" "see docs/dev-setup.md section 6"

Write-Host ""
if ($script:fail -eq 0) { Write-Host "All checks passed." -ForegroundColor Green }
else { Write-Host "$($script:fail) item(s) missing. Not every role needs every item; see docs/dev-setup.md section 1." -ForegroundColor Yellow }
