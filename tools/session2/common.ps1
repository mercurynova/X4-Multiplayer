# Shared helpers for the session-2 kit (dot-sourced by the other scripts). No hard-coded user paths:
# the X4 install is found through Steam, Documents through [Environment]::GetFolderPath('MyDocuments').

$script:Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:OutDir = Join-Path $script:Repo 'out\session2'
$script:Ports = @{ Tcp = 47780; Udp = 47781; Http = 47790 }
$script:ExtensionNames = @('x4native', 'x4mp_probe', 'x4mp_spike')

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
                    $candidates.Add((Join-Path ($Matches[1] -replace '\\\\', '\') 'steamapps\common\X4 Foundations'))
                }
            }
        }
    }
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'X4.exe')) { return $c }
    }
    return $null
}

# Returns the X4 install folder; when it cannot be found: throws, or with -AllowMissing returns a placeholder (dry runs).
function Resolve-X4Dir([string]$X4Dir, [switch]$AllowMissing) {
    if (-not $X4Dir) { $X4Dir = Find-X4Dir }
    if ($X4Dir -and (Test-Path (Join-Path $X4Dir 'X4.exe'))) { return $X4Dir }
    if ($AllowMissing) {
        Write-Warning 'X4.exe not found (dry run continues with a placeholder). Pass -X4Dir <folder containing X4.exe> for a real run.'
        return '<X4 install>'
    }
    throw 'X4.exe not found. Pass the install folder with -X4Dir (the folder that contains X4.exe).'
}

function Get-X4DocsRoot {
    return (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Egosoft\X4')
}

# Documents\Egosoft\X4\<numeric id>; -UserId picks one when several exist. -AllowMissing: dry runs.
function Resolve-X4UserDir([string]$UserId, [switch]$AllowMissing) {
    $root = Get-X4DocsRoot
    $ids = @()
    if (Test-Path $root) { $ids = @(Get-ChildItem $root -Directory | Where-Object { $_.Name -match '^\d+$' }) }
    if ($UserId) {
        $dir = Join-Path $root $UserId
        if (Test-Path $dir) { return $dir }
        if ($AllowMissing) { Write-Warning "No folder $dir (dry run)."; return $dir }
        throw "X4 user folder not found: $dir"
    }
    if ($ids.Count -eq 1) { return $ids[0].FullName }
    if ($ids.Count -eq 0) {
        if ($AllowMissing) { Write-Warning "No numeric X4 user folder under $root (dry run)."; return (Join-Path $root '<numeric id>') }
        throw "No numeric X4 user folder under $root. Start X4 once, or pass -UserId."
    }
    if ($AllowMissing) { Write-Warning "Several X4 user folders ($($ids.Name -join ', ')); a real run needs -UserId."; return (Join-Path $root '<numeric id>') }
    throw "Several X4 user folders under $root : $($ids.Name -join ', '). Pass -UserId <one of them>."
}

function Get-ProbeConfigPath { return (Join-Path (Get-X4DocsRoot) 'x4mp\x4mp_probe.json') }

# Reads the probe config as an ordered hashtable (empty when absent).
function Read-ProbeConfig {
    $path = Get-ProbeConfigPath
    $cfg = [ordered]@{}
    if (Test-Path $path) {
        $obj = Get-Content $path -Raw | ConvertFrom-Json
        foreach ($p in $obj.PSObject.Properties) { $cfg[$p.Name] = $p.Value }
    }
    return $cfg
}

function Write-ProbeConfig($cfg) {
    $path = Get-ProbeConfigPath
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    ($cfg | ConvertTo-Json -Depth 4) | Set-Content -Path $path -Encoding UTF8
}
