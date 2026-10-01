<#
.SYNOPSIS
  Removes the throwaway x4mp_spike extension from the X4: Foundations extensions folder.
.PARAMETER X4Dir
  X4 install folder (the one containing X4.exe). Default: auto-detected like install-spike.ps1.
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

$dest = Join-Path (Join-Path $X4Dir 'extensions') 'x4mp_spike'
if (-not (Test-Path $dest)) {
    Write-Host "Nothing to remove: $dest does not exist."
    return
}
if ($PSCmdlet.ShouldProcess($dest, 'Remove x4mp_spike')) {
    Remove-Item -Path $dest -Recurse -Force
    Write-Host "Removed $dest"
    Write-Host 'Also delete the two test saves (they contain spike objects) and remove the -debug launch options if you like.'
}
