<#
.SYNOPSIS
  Session 4, sitting 0: swaps the product x4mp OUT and the throwaway test kit (x4native + x4mp_probe + x4mp_spike) IN. -Restore swaps back.
.DESCRIPTION
  The product mod and the spike kit must never run together (the product's x4mp.dll hooks the same game functions the probe measures).
  Install (default):
    1. refuses while X4 is running (-Force overrides);
    2. MOVES <X4>\extensions\x4mp\ (the product) to out\session4\parked\x4mp\ (outside the X4 install; a renamed folder would still load,
       because X4 identifies an extension by the id in its content.xml) and switches its entry in the user content.xml to enabled="false"
       when such an entry exists (a backup content.xml.x4mp-bak is made first);
    3. copies x4native (vendored X4Native), x4mp_probe (extension files + the built x4mp_probe.dll -> native\) and x4mp_spike (Lua/MD, with
       the session-4 blocks) into <X4>\extensions\, unblocks the files, and writes out\session4\install-spike-state.json (what was parked).
  -Restore:
    removes x4mp_probe and x4mp_spike from <X4>\extensions\, moves the parked product x4mp back, sets its content.xml entry to enabled="true",
    and removes x4native only when this script installed it (there was no x4native before and no product to restore).
  Saves are never touched. Supports -WhatIf (nothing is copied, moved, removed or edited).
  Build first: powershell -ExecutionPolicy Bypass -File mod\build.ps1 -Spikes   (x4mp_probe.dll)
.PARAMETER X4Dir    Folder containing X4.exe (default: auto-detected via Steam).
.PARAMETER UserId   The numeric folder under Documents\Egosoft\X4 (only needed when there are several).
.PARAMETER Restore  Swap back: remove the spike kit, bring the product mod back and enable it.
.PARAMETER Force    Do not stop when X4 is running (not recommended: X4 keeps the files open).
.PARAMETER Config   Which native build to prefer: relwithdebinfo (default), release or debug.
.PARAMETER ProbeDll Explicit path of x4mp_probe.dll (default: searched under mod\build\).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$X4Dir,
    [string]$UserId,
    [switch]$Restore,
    [switch]$Force,
    [ValidateSet('debug', 'release', 'relwithdebinfo')][string]$Config = 'relwithdebinfo',
    [string]$ProbeDll
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$dry = [bool]$WhatIfPreference
$X4Dir = Resolve-X4Dir $X4Dir -AllowMissing:$dry
$user = Resolve-X4UserDir $UserId -AllowMissing:$dry
$extRoot = Join-Path $X4Dir 'extensions'
$mod = Join-Path $Repo 'mod'
$tag = 'v9.0.0-611726'
$product = Join-Path $extRoot $ProductExtension
$parked = Join-Path $ParkedDir $ProductExtension

if (-not $Force -and (Test-X4Running)) {
    if ($dry) { Write-Warning 'X4 is running (a real run would stop here; quit X4 first).' }
    else { throw 'X4 is running. Quit X4 first (it keeps the extension files open), or pass -Force.' }
}

function Read-State { if (Test-Path $StatePath) { return (Get-Content $StatePath -Raw | ConvertFrom-Json) } return $null }

function Remove-Folder([string]$path, [string]$what) {
    if (-not (Test-Path $path)) { Write-Host "  not present: $path"; return }
    if ($PSCmdlet.ShouldProcess($path, "Remove $what")) { Remove-Item -Path $path -Recurse -Force; Write-Host "  removed $path" }
}

# ------------------------------------------------------------------------------------------------------------------ restore
if ($Restore) {
    Write-Host "X4 folder : $X4Dir"
    Write-Host 'Swapping back: spike kit out, product mod in.'
    $state = Read-State
    foreach ($name in 'x4mp_probe', 'x4mp_spike') { Remove-Folder (Join-Path $extRoot $name) $name }
    $hadProduct = Test-Path $parked
    if ($hadProduct) {
        if (Test-Path $product) { throw "Cannot restore: $product exists although the product is parked in $parked. Resolve this by hand (keep one of the two)." }
        if ($PSCmdlet.ShouldProcess($product, "Move the parked product mod back from $parked")) {
            Move-Item -Path $parked -Destination $product
            Get-ChildItem $product -Recurse -File | ForEach-Object { Unblock-File -Path $_.FullName }
            Write-Host "  restored $product"
        }
        $r = Set-ExtensionEnabled $user $ProductExtension $true -Preview:$dry
        Write-Host "  content.xml: $ProductExtension enabled -> $r"
    }
    else {
        Write-Host "  no parked product mod ($parked): nothing to bring back."
    }
    # x4native: the product needs it; remove only what this script added when there was no product at all
    $x4nativeWasPresent = if ($state) { [bool]$state.x4nativeWasPresent } else { $true }
    if (-not $hadProduct -and -not $x4nativeWasPresent) { Remove-Folder (Join-Path $extRoot 'x4native') 'x4native (installed by install-spike.ps1)' }
    else { Write-Host '  x4native stays in place (the product mod needs it).' }
    foreach ($id in 'x4mp_probe', 'x4mp_spike') { $r = Set-ExtensionEnabled $user $id $false -Preview:$dry; if ($r -eq 'changed') { Write-Host "  content.xml: $id disabled" } }
    if ($state -and -not $dry -and (Test-Path $StatePath)) { Remove-Item $StatePath -Force }
    Write-Host ''
    Write-Host 'Done. In X4: Settings > Extensions: x4mp and x4native enabled (Protected UI mode may stay off).'
    Write-Host 'Remove the launch option -logfile x4mp_s4.log only when you are completely finished with session 4.'
    return
}

# ------------------------------------------------------------------------------------------------------------------ install
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
if (-not (Test-Path (Join-Path $nativeSrc 'x4native'))) { $problems.Add("X4Native not found under $(Join-Path $mod 'third_party\x4native')") }
if (-not (Test-Path (Join-Path $probeSrc 'content.xml'))) { $problems.Add("probe extension files not found at $probeSrc") }
if (-not $ProbeDll -or -not (Test-Path $ProbeDll)) { $problems.Add('x4mp_probe.dll not found: run mod\build.ps1 -Spikes first (or pass -ProbeDll)') }
if (-not (Test-Path (Join-Path $spikeSrc 'ui\x4mp_spike_s13.lua'))) { $problems.Add("spike extension (with the session-4 blocks) not found at $spikeSrc") }
if ($problems.Count -gt 0) {
    if ($dry) { $problems | ForEach-Object { Write-Warning "$_ (a real run would stop here)" } }
    else { throw ("Cannot install:`n  " + ($problems -join "`n  ")) }
}
if ((Test-Path $parked) -and (Test-Path $product)) {
    throw "Both $product and the parked copy $parked exist. Keep one of them (delete the other by hand), then run again."
}

Write-Host "X4 folder : $X4Dir"
Write-Host "Install to: $extRoot  ($($SpikeExtensionNames -join ', '))"
if ($ProbeDll) { Write-Host "Probe DLL : $ProbeDll" }
$productPresent = Test-Path $product
Write-Host $(if ($productPresent) { "Product mod: $product will be PARKED in $parked (and switched off in content.xml)" } else { 'Product mod: not installed (nothing to park)' })

function Install-Folder([string]$source, [string]$dest) {
    if (Test-Path $dest) { Remove-Item -Path $dest -Recurse -Force }
    Copy-Item -Path $source -Destination $dest -Recurse -Force
}

$x4nativeWasPresent = Test-Path (Join-Path $extRoot 'x4native')
if ($productPresent -and $PSCmdlet.ShouldProcess($product, "Park the product mod in $parked")) {
    New-Item -ItemType Directory -Force $ParkedDir | Out-Null
    if (Test-Path $parked) { Remove-Item $parked -Recurse -Force }
    Move-Item -Path $product -Destination $parked
    Write-Host "  parked $product"
}
if ($productPresent) {
    $r = Set-ExtensionEnabled $user $ProductExtension $false -Preview:$dry
    Write-Host "  content.xml: $ProductExtension disabled -> $r"
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
    foreach ($name in $SpikeExtensionNames) {
        Get-ChildItem (Join-Path $extRoot $name) -Recurse -File | ForEach-Object { Unblock-File -Path $_.FullName; $n++ }
    }
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    @{ installedAt = (Get-Date).ToString('s'); productParked = $productPresent; x4nativeWasPresent = $x4nativeWasPresent } |
        ConvertTo-Json | Set-Content -Path $StatePath -Encoding UTF8
    Write-Host "Installed and unblocked $n files. State: $StatePath"
    foreach ($id in 'x4mp_probe', 'x4mp_spike', 'x4native') {
        $r = Set-ExtensionEnabled $user $id $true -Preview:$dry
        if ($r -eq 'changed') { Write-Host "  content.xml: $id enabled" }
    }
    Write-Host ''
    Write-Host 'Next: write-probe-config.ps1 -ScratchSlot save_007 (once), then start X4.'
    Write-Host 'In X4: Settings > Extensions: Protected UI mode OFF; x4native, x4mp_probe and x4mp_spike enabled; x4mp (the product) must NOT be listed as enabled.'
}
