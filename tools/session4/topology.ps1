# Helpers shared by the session-4 topology scripts (start-fake-authority.ps1, start-fake-clients.ps1, start-server-lan.ps1).
# Dot-source AFTER common.ps1. Nothing here changes the system: the firewall helpers only READ the rules and PRINT commands.

# Name the wingman bots give themselves: Wing01, Wing02, ... (FakeNode: --name-prefix Wing). They fly near the player called -Target.
$script:WingmanPrefix = 'Wing'
$script:DefaultTarget = 'Tester'
$script:WingmanSeed = '42'            # every FakeNode process of one run needs the same --seed (and --galaxy-file)

# The galaxy dump the FakeNode builds its universe from. Explicit path wins, else out\session4\galaxy-dump.json (written by collect-logs.ps1 /
# extract-galaxy-dump.ps1 after sitting 0). Returns the path or $null (then the bots use a generated galaxy and the script says so).
function Resolve-GalaxyFile([string]$Explicit, [switch]$Dry) {
    $path = if ($Explicit) { $Explicit } else { Join-Path $OutDir 'galaxy-dump.json' }
    if (Test-Path -LiteralPath $path) {
        $check = Test-GalaxyDumpJson (Get-Content -LiteralPath $path -Raw)
        if (-not $check.Ok) { Write-Warning "Galaxy dump $path : $($check.Message). The bots will still use it, but a real X4 galaxy has 140 or more sectors." }
        return (Resolve-Path -LiteralPath $path).Path
    }
    if ($Explicit) {
        $msg = "Galaxy dump not found: $Explicit"
        if ($Dry) { Write-Warning "$msg (a real run would stop here)"; return $null }
        throw $msg
    }
    Write-Warning "No galaxy dump at $path (sitting 0 / extract-galaxy-dump.ps1 makes it): the bots use a GENERATED galaxy. Their sector macros then do not exist in your X4, so a real client cannot place them. Run sitting 0 first, or pass -GalaxyFile."
    return $null
}

# Writes the extension report (your enabled DLCs) the FakeNode processes send, so the server does not refuse a real player that owns DLCs
# (a DLC difference refuses even in Warn mode; session 3 lesson). Returns @{ File; List } (File = $null when no DLC is found).
function New-DlcExtensionsFile([string]$X4Dir, [string]$UserId, [string]$FileName, [switch]$Dry) {
    $x4 = Resolve-X4Dir $X4Dir -AllowMissing
    $list = @(Get-LocalDlcExtensions $x4 (Resolve-X4UserDir $UserId -AllowMissing))
    if ($list.Count -eq 0) {
        Write-Warning 'No enabled DLC found in the X4 install: the bots report none (a real player that owns DLCs would be refused). Pass -X4Dir (and -UserId when there are several X4 user folders).'
        return @{ File = $null; List = @() }
    }
    $file = Join-Path $OutDir $FileName
    if (-not $Dry) {
        New-Item -ItemType Directory -Force $OutDir | Out-Null
        ConvertTo-Json -InputObject @($list) -Depth 4 | Set-Content -Path $file -Encoding ascii
    }
    return @{ File = $file; List = $list }
}

function Format-DlcList($list) {
    if (-not $list -or @($list).Count -eq 0) { return 'none' }
    return 'your DLCs: ' + ((@($list) | ForEach-Object { $_.id + '@' + $_.version }) -join ', ')
}

# The FakeNode arguments of the wingman bots: -Count bots Wing01.. that fly 400 m around the player called -Target (their ghost, so the
# bots need the server to replicate that player: it does as soon as the player is in game and has a ship).
function Get-WingmanArgs([int]$Count, [string]$Target, [string]$Mode, [int]$AvatarTimeout = 0) {
    $a = @('--clients', "$Count", '--name-prefix', $script:WingmanPrefix, '--wingman', $Target, '--wingman-mode', $Mode, '--avatars', '--chat-echo')
    if ($AvatarTimeout -gt 0) { $a += @('--avatar-timeout', "$AvatarTimeout") }   # a real X4 authority needs longer than the bots' default 10 s
    return $a
}

# ---- LAN and firewall (read-only) ------------------------------------------------------------------------------------------------

# IPv4 addresses of this PC that another PC on the LAN can use (no loopback, no 169.254.x.x link-local, no virtual-adapter noise when it can tell).
function Get-LanAddresses {
    $WhatIfPreference = $false   # read only: under -WhatIf the NetTCPIP module import would otherwise print "New Alias" what-if lines
    $result = New-Object System.Collections.Generic.List[object]
    try {
        foreach ($a in Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop) {
            if ($a.IPAddress -like '127.*' -or $a.IPAddress -like '169.254.*') { continue }
            $result.Add([pscustomobject]@{ Address = [string]$a.IPAddress; Interface = [string]$a.InterfaceAlias })
        }
    }
    catch {
        foreach ($h in [Net.Dns]::GetHostAddresses([Net.Dns]::GetHostName())) {
            if ($h.AddressFamily -ne 'InterNetwork') { continue }
            $s = $h.ToString()
            if ($s -like '127.*' -or $s -like '169.254.*') { continue }
            $result.Add([pscustomobject]@{ Address = $s; Interface = '?' })
        }
    }
    return $result.ToArray()
}

# The ports the server listens on and what each is for.
function Get-ServerPortList {
    return @(
        [pscustomobject]@{ Protocol = 'TCP'; Port = $Ports.Tcp; Name = 'X4MP game TCP'; Purpose = 'control + bulk (join, saves, chat)' },
        [pscustomobject]@{ Protocol = 'UDP'; Port = $Ports.Udp; Name = 'X4MP game UDP'; Purpose = 'realtime lane (ghost positions)' },
        [pscustomobject]@{ Protocol = 'TCP'; Port = $Ports.Http; Name = 'X4MP admin HTTP'; Purpose = 'admin GUI + save fallback' })
}

# Reads the Windows Defender Firewall (no changes): for every port, is there an ENABLED inbound ALLOW rule that covers it (any profile)?
# Returns objects with Covered = $true|$false|$null (null = could not tell: not Windows, no cmdlets, or no rights to read).
function Get-FirewallStatus {
    $WhatIfPreference = $false   # read only (see Get-LanAddresses)
    $ports = Get-ServerPortList
    $out = New-Object System.Collections.Generic.List[object]
    $rules = $null
    try {
        $rules = @(Get-NetFirewallRule -Direction Inbound -Action Allow -Enabled True -ErrorAction Stop)
        $filters = @{}
        # (Get-NetFirewallPortFilter without a rule is "access denied" for a normal user; through the pipeline it works and takes ~10 s)
        foreach ($f in ($rules | Get-NetFirewallPortFilter -ErrorAction Stop)) { $filters[$f.InstanceID] = $f }
    }
    catch { $rules = $null }
    foreach ($p in $ports) {
        $covered = $null
        if ($null -ne $rules) {
            $covered = $false
            foreach ($r in $rules) {
                $f = $filters[$r.InstanceID]
                if (-not $f) { continue }
                if ([string]$f.Protocol -ne $p.Protocol -and [string]$f.Protocol -ne 'Any') { continue }
                $lp = @($f.LocalPort | ForEach-Object { [string]$_ })
                if ($lp -contains [string]$p.Port -or $lp -contains 'Any') { $covered = $true; break }
                foreach ($range in ($lp | Where-Object { $_ -match '^\d+-\d+$' })) {
                    $lo, $hi = $range -split '-'
                    if ([int]$lo -le $p.Port -and $p.Port -le [int]$hi) { $covered = $true }
                }
                if ($covered) { break }
            }
        }
        $out.Add([pscustomobject]@{ Protocol = $p.Protocol; Port = $p.Port; Name = $p.Name; Purpose = $p.Purpose; Covered = $covered })
    }
    return $out.ToArray()
}

# The exact commands the USER runs in an elevated PowerShell to open (or later close) the ports. This script never runs them.
function Get-FirewallCommands($Only = $null) {
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($p in Get-ServerPortList) {
        if ($null -ne $Only -and @($Only | Where-Object { $_.Protocol -eq $p.Protocol -and $_.Port -eq $p.Port }).Count -eq 0) { continue }
        $lines.Add("New-NetFirewallRule -DisplayName '$($p.Name)' -Direction Inbound -Action Allow -Protocol $($p.Protocol) -LocalPort $($p.Port) -RemoteAddress LocalSubnet")
    }
    return $lines.ToArray()
}

# For sitting 3, step 3.5 (UDP fallback): the commands that switch the UDP rule off and on again (run by the user, in an elevated PowerShell).
function Get-UdpToggleCommands {
    $n = (Get-ServerPortList | Where-Object { $_.Protocol -eq 'UDP' } | Select-Object -First 1).Name
    return @{ Off = "Disable-NetFirewallRule -DisplayName '$n'"; On = "Enable-NetFirewallRule -DisplayName '$n'" }
}
