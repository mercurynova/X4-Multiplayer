<#
.SYNOPSIS
  Session 2: installs x4native, x4mp_probe and x4mp_spike into <X4 install>\extensions\ (nothing else is touched).
.DESCRIPTION
  Sources (built beforehand with `mod\build.ps1 -Spikes`):
    x4native    mod\third_party\x4native\<tag>\x4native  (+ LICENSE)
    x4mp_probe  mod\spikes\x4mp_probe  (extension files) + the built x4mp_probe.dll -> native\
    x4mp_spike  mod\spikes\x4mp_spike
  Refuses to run when <X4>\extensions\x4mp\ exists (the real mod and the test kit must not run together).
  Replaces older copies of exactly these three folders and unblocks the copied files. Supports -WhatIf.
.PARAMETER X4Dir   Folder containing X4.exe (default: auto-detected via Steam).
.PARAMETER Config  Which native build to prefer: relwithdebinfo (default), release or debug.
.PARAMETER ProbeDll  Explicit path of x4mp_probe.dll (default: searched under mod\build\).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$X4Dir,
    [ValidateSet('debug', 'release', 'relwithdebinfo')][string]$Config = 'relwithdebinfo',
    [string]$ProbeDll
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$dry = [bool]$WhatIfPreference
$X4Dir = Resolve-X4Dir $X4Dir -AllowMissing:$dry
$extRoot = Join-Path $X4Dir 'extensions'
$mod = Join-Path $Repo 'mod'
$tag = 'v9.0.0-611726'

if (Test-Path (Join-Path $extRoot 'x4mp')) {
    throw "Refusing to install: $(Join-Path $extRoot 'x4mp') exists. The real x4mp extension and the session-2 test kit must not run together; remove that folder first."
}

$nativeSrc = Join-Path $mod "third_party\x4native\$tag"
$probeSrc = Join-Path $mod 'spikes\x4mp_probe'
$spikeSrc = Join-Path $mod 'spikes\x4mp_spike'
if (-not $ProbeDll) {
    $found = @()
    $buildRoot = Join-Path $mod 'build'
    if (Test-Path $buildRoot) {
        $found = @(Get-ChildItem $buildRoot -Recurse -Filter 'x4mp_probe.dll' -File | Sort-Object { if ($_.FullName -match $Config) { 0 } else { 1 } }, LastWriteTime -Descending)
    }
    if ($found.Count -gt 0) { $ProbeDll = $found[0].FullName }
}

$problems = New-Object System.Collections.Generic.List[string]
if (-not (Test-Path (Join-Path $nativeSrc 'x4native'))) { $problems.Add("X4Native not found at $nativeSrc") }
if (-not (Test-Path $probeSrc)) { $problems.Add("probe extension files not found at $probeSrc") }
if (-not $ProbeDll -or -not (Test-Path $ProbeDll)) { $problems.Add('x4mp_probe.dll not found: run mod\build.ps1 -Spikes first (or pass -ProbeDll)') }
if (-not (Test-Path (Join-Path $spikeSrc 'content.xml'))) { $problems.Add("spike extension not found at $spikeSrc") }
if ($problems.Count -gt 0) {
    if ($dry) { $problems | ForEach-Object { Write-Warning "$_ (a real run would stop here)" } }
    else { throw ("Cannot install:`n  " + ($problems -join "`n  ")) }
}

Write-Host "X4 folder : $X4Dir"
Write-Host "Install to: $extRoot  ($($ExtensionNames -join ', '))"
if ($ProbeDll) { Write-Host "Probe DLL : $ProbeDll" }

function Install-Folder([string]$source, [string]$dest) {
    if (Test-Path $dest) { Remove-Item -Path $dest -Recurse -Force }
    Copy-Item -Path $source -Destination $dest -Recurse -Force
}

if ($PSCmdlet.ShouldProcess($extRoot, 'Install x4native, x4mp_probe, x4mp_spike')) {
    New-Item -ItemType Directory -Force $extRoot | Out-Null
    Install-Folder (Join-Path $nativeSrc 'x4native') (Join-Path $extRoot 'x4native')
    if (Test-Path (Join-Path $nativeSrc 'LICENSE')) { Copy-Item (Join-Path $nativeSrc 'LICENSE') (Join-Path $extRoot 'x4native\LICENSE') -Force }
    Install-Folder $probeSrc (Join-Path $extRoot 'x4mp_probe')
    New-Item -ItemType Directory -Force (Join-Path $extRoot 'x4mp_probe\native') | Out-Null
    Copy-Item $ProbeDll (Join-Path $extRoot 'x4mp_probe\native\x4mp_probe.dll') -Force
    Install-Folder $spikeSrc (Join-Path $extRoot 'x4mp_spike')
    $n = 0
    foreach ($name in $ExtensionNames) {
        Get-ChildItem (Join-Path $extRoot $name) -Recurse -File | ForEach-Object { Unblock-File -Path $_.FullName; $n++ }
    }
    Write-Host "Installed and unblocked $n files."
    Write-Host 'In X4: Settings > Extensions: Protected UI mode OFF; x4native, x4mp_probe and x4mp_spike must be enabled.'
}
