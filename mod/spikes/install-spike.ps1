<#
.SYNOPSIS
  Installs the throwaway x4mp_spike extension into the X4: Foundations extensions folder.
.DESCRIPTION
  Copies mod\spikes\x4mp_spike\ to <X4>\extensions\x4mp_spike\ (replacing an older copy) and runs
  Unblock-File on every copied file. Never touches anything else in the X4 folder.
.PARAMETER X4Dir
  X4 install folder (the one containing X4.exe). Default: auto-detected (Steam default path, then the Steam
  library folders from the registry / libraryfolders.vdf).
.EXAMPLE
  .\install-spike.ps1
  .\install-spike.ps1 -X4Dir "D:\SteamLibrary\steamapps\common\X4 Foundations"
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$X4Dir
)

$ErrorActionPreference = 'Stop'

function Find-X4Dir {
    $candidates = New-Object System.Collections.Generic.List[string]
    $candidates.Add('C:\Program Files (x86)\Steam\steamapps\common\X4 Foundations')
    $steamRoots = New-Object System.Collections.Generic.List[string]
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        try {
            $p = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($name in 'SteamPath', 'InstallPath') {
                if ($p.$name) { $steamRoots.Add(($p.$name -replace '/', '\')) }
            }
        } catch { }
    }
    foreach ($root in $steamRoots) {
        $candidates.Add((Join-Path $root 'steamapps\common\X4 Foundations'))
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"([^"]+)"') {
                    $lib = $Matches[1] -replace '\\\\', '\'
                    $candidates.Add((Join-Path $lib 'steamapps\common\X4 Foundations'))
                }
            }
        }
    }
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'X4.exe')) { return $c }
    }
    return $null
}

if (-not $X4Dir) { $X4Dir = Find-X4Dir }
if (-not $X4Dir -or -not (Test-Path (Join-Path $X4Dir 'X4.exe'))) {
    throw "X4.exe not found. Pass the install folder with -X4Dir (the folder that contains X4.exe)."
}

$source = Join-Path $PSScriptRoot 'x4mp_spike'
if (-not (Test-Path (Join-Path $source 'content.xml'))) {
    throw "Extension source not found at $source"
}

$extRoot = Join-Path $X4Dir 'extensions'
$dest = Join-Path $extRoot 'x4mp_spike'
Write-Host "X4 folder : $X4Dir"
Write-Host "Install to: $dest"

if ($PSCmdlet.ShouldProcess($dest, 'Install x4mp_spike')) {
    if (-not (Test-Path $extRoot)) { New-Item -ItemType Directory -Path $extRoot | Out-Null }
    if (Test-Path $dest) {
        Write-Host 'Removing the previous copy first.'
        Remove-Item -Path $dest -Recurse -Force
    }
    Copy-Item -Path $source -Destination $dest -Recurse -Force
    $files = Get-ChildItem -Path $dest -Recurse -File
    foreach ($f in $files) { Unblock-File -Path $f.FullName }
    Write-Host ("Copied and unblocked {0} files." -f $files.Count)
    Write-Host ''
    Write-Host 'Next steps:'
    Write-Host '  1. Steam > X4 > Properties > Launch options:  -debug all -logfile x4mp_spike.log'
    Write-Host '  2. In X4: Settings > Extensions: Protected UI mode OFF; make sure "X4MP Spike (test)" is enabled.'
    Write-Host '  3. Load the TEST SAVE (made without this extension) and follow docs\in-game-session-2.md (session 1: docs\spikes\session-1.md).'
}
