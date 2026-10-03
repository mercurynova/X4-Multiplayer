# Shared helpers for the session-4 kit (dot-sourced by the other scripts). Builds on the session-2/3 helpers (X4 install and Documents
# discovery through Steam and the shell, no hard-coded user paths) and overrides what differs: output folder, extension names.

. (Join-Path $PSScriptRoot '..\session3\common.ps1')

# X4MP_S4_OUT_DIR: test-only override of out\session4 (dry runs keep their files in a temp folder).
$script:OutDir = if ($env:X4MP_S4_OUT_DIR) { $env:X4MP_S4_OUT_DIR } else { Join-Path $script:Repo 'out\session4' }
$script:SpikeExtensionNames = @('x4native', 'x4mp_probe', 'x4mp_spike')   # what install-spike.ps1 puts into <X4>\extensions\
$script:ProductExtension = 'x4mp'                                         # the real mod, swapped out while the spike kit is installed
$script:GameLogDefault = 'x4mp_s4.log'                                    # the -logfile of the Steam launch options for session 4
$script:ParkedDir = Join-Path $script:OutDir 'parked'                     # where install-spike.ps1 keeps the product x4mp (outside the X4 install)
$script:StatePath = Join-Path $script:OutDir 'install-spike-state.json'

# Block names run-block.ps1 knows. Native = the x4mp_probe DLL (M3-001); Lua = the x4mp_spike extension (M3-002); anything else is passed
# through with a warning (the session-2 blocks still exist in x4mp_spike).
$script:NativeBlocks = @('ghost_spawn', 'ghost_motion', 'ghost_xsector', 'sample', 'seat', 'takeover', 'takeover_docked', 'persist_spawn', 'persist_check', 'seta', 'pause_move', 'cleanup', 's13_check', 's13_stop', 's13_status')
$script:LuaBlocks = @('dress', 'velocity', 'seta_off', 'seta_watch', 'teams_product', 'teams_report', 'teams_end', 'md_table', 'galaxy_dump', 'chat', 'list', 'ping')
# The blocks that change the game (spawn, move the player, remove a ship) and therefore only run on the scratch save.
$script:ScratchOnlyBlocks = @('takeover', 'takeover_docked', 'persist_spawn')

function Test-X4Running { return [bool](Get-Process -Name 'X4' -ErrorAction SilentlyContinue) }

# Sets enabled="true|false" on an EXISTING <extension id="..."> entry of the X4 user content.xml (X4 keeps the Extensions on/off switches
# there). Never creates entries. Makes content.xml.x4mp-bak once before the first change. Returns 'changed', 'unchanged' or 'absent'.
function Set-ExtensionEnabled([string]$UserDir, [string]$Id, [bool]$Enabled, [switch]$Preview) {
    $file = Join-Path $UserDir 'content.xml'
    if (-not (Test-Path $file)) { return 'absent' }
    $xml = New-Object System.Xml.XmlDocument
    $xml.PreserveWhitespace = $true
    $xml.Load($file)
    $node = $xml.SelectSingleNode("//extension[@id='$Id']")
    if (-not $node) { return 'absent' }
    $want = if ($Enabled) { 'true' } else { 'false' }
    if ([string]$node.GetAttribute('enabled') -eq $want) { return 'unchanged' }
    if (-not $Preview) {
        $bak = $file + '.x4mp-bak'
        if (-not (Test-Path $bak)) { Copy-Item $file $bak }
        $node.SetAttribute('enabled', $want)
        $xml.Save($file)
    }
    return 'changed'
}

# Rebuilds galaxy-dump.json from the chunks the spike block galaxy_dump writes into the game log:
#   [X4MP-SPIKE] S13.11 DATA part=<i>/<n> json=<chunk>        ('~' stands for a newline)
# The LAST complete set in the log wins (the block may have been run several times). Returns the JSON text, or $null when there is none.
function Convert-GalaxyDumpFromLog([string]$LogPath) {
    if (-not (Test-Path $LogPath)) { return $null }
    $parts = @{}
    $total = 0
    $best = $null
    $fs = [IO.File]::Open($LogPath, 'Open', 'Read', 'ReadWrite')
    $sr = New-Object IO.StreamReader($fs)
    try {
        while ($null -ne ($line = $sr.ReadLine())) {
            if ($line -notmatch 'S13\.11 DATA part=(\d+)/(\d+) json=(.*)$') { continue }
            $i = [int]$Matches[1]; $n = [int]$Matches[2]; $data = $Matches[3]
            if ($i -eq 1) { $parts = @{}; $total = $n }
            if ($n -ne $total) { continue }
            $parts[$i] = $data
            if ($parts.Count -eq $total -and ($parts.Keys | Measure-Object -Maximum).Maximum -eq $total) {
                $best = (1..$total | ForEach-Object { $parts[$_] }) -join ''
            }
        }
    }
    finally { $sr.Dispose() }
    if ($null -eq $best) { return $null }
    return ($best -replace '~', "`n")
}

# Validates a galaxy dump JSON text; returns @{ Ok; Sectors; Message }.
function Test-GalaxyDumpJson([string]$Json, [int]$MinSectors = 140) {
    try { $obj = $Json | ConvertFrom-Json } catch { return @{ Ok = $false; Sectors = 0; Message = "not valid JSON: $($_.Exception.Message)" } }
    $n = @($obj.sectors).Count
    $ok = $n -ge $MinSectors
    return @{ Ok = $ok; Sectors = $n; Message = "$n sectors" + $(if ($ok) { " (>= $MinSectors)" } else { " (FEWER than $MinSectors)" }) }
}
