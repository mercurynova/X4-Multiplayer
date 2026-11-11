<#
.SYNOPSIS
  Checks that the X4MP mod can actually run on this machine and prints one plain-English verdict.
.DESCRIPTION
  This is the health check T-11 asks for: game found, game build, files present, version_db data,
  files not blocked by Windows, VC++ runtime, extensions enabled, Protected UI off, log folder
  writable, server bundle present, ports free.

  Every problem is printed in the order that matters, each with the fix, so the window can be sent
  as is: the exit code is 1 when the mod cannot work and 0 otherwise.
.PARAMETER GameDir
  The folder that holds X4.exe. Found automatically when omitted.
.PARAMETER AccountRoot
  The folder that holds the per-profile folders, usually Documents\Egosoft\X4.
.PARAMETER JsonPath
  Also write the problems to this file as JSON, for a bug report or a test.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File CheckInstall.ps1
#>
[CmdletBinding()]
param(
  [string]$GameDir,
  [string]$AccountRoot,
  [string]$JsonPath,
  [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'Common.ps1')

if (-not $AccountRoot) { $AccountRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Egosoft\X4' }
$problems = New-Object System.Collections.Generic.List[object]

function Fail([string]$Text, [string]$Fix) { [void]$problems.Add((New-Problem 'blocker' $Text $Fix)) }
function Warn([string]$Text, [string]$Fix) { [void]$problems.Add((New-Problem 'warning' $Text $Fix)) }
function Note([string]$Text, [string]$Fix = '') { [void]$problems.Add((New-Problem 'note' $Text $Fix)) }

Write-Head 'X4MP Check Install'
Write-Host ('Installer {0}, for X4 {1} build {2}' -f $script:X4MPInstallerVersion, $script:X4Version, $script:X4BuildKey)

# 1. The game.
Write-Head 'Game'
if (-not $GameDir) {
  $found = @(Get-X4DirsFound)
  if ($found.Count -eq 0) {
    Fail 'X4: Foundations was not found on this machine' 'Start the game once from Steam, then run this check again. If the game lives elsewhere, run: Check Install.bat "<game folder>"'
  } else {
    if ($found.Count -gt 1) {
      Warn ('More than one X4 install was found, checking the first: {0}' -f $found[0]) 'If you play another one, pass its folder: Check Install.bat "<game folder>"'
      $found | ForEach-Object { Write-Host ('  found: {0}' -f $_) }
    }
    $GameDir = $found[0]
  }
}
if ($GameDir) {
  if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'X4.exe'))) {
    Fail ('There is no X4.exe in {0}' -f $GameDir) 'Point the check at the folder that holds X4.exe.'
    $GameDir = $null
  } else {
    $GameDir = (Resolve-Path -LiteralPath $GameDir).Path
    Write-Host ('  folder: {0}' -f $GameDir)
    $vd = Join-Path $GameDir 'version.dat'
    if (-not (Test-Path -LiteralPath $vd)) {
      Warn 'version.dat is missing: the game build cannot be confirmed' 'Verify the game files in Steam.'
    } else {
      $v = (Get-Content -LiteralPath $vd -Raw).Trim()
      Write-Host ('  version: {0}' -f $v)
      if ($v -ne $script:X4Version) {
        Fail ('The game is version {0}, the mod was built for {1}' -f $v, $script:X4Version) 'Update or roll back the game so that it matches the mod, or install the mod build for your version.'
      }
    }
  }
}
$running = @(Get-X4Running)
if ($running.Count -gt 0) {
  Warn ('X4 is running ({0})' -f (($running | ForEach-Object { '{0} ({1})' -f $_.ProcessName, $_.Id }) -join ', ')) 'Close the game, then run the check again: files cannot be verified while they are loaded.'
}

# 2. The mod's own files.
Write-Head 'Mod files'
$ext = if ($GameDir) { Join-Path $GameDir 'extensions' } else { $null }
$mod = if ($ext) { Join-Path $ext 'x4mp' } else { $null }
if (-not $mod -or -not (Test-Path -LiteralPath $mod)) {
  Fail 'extensions\x4mp is not installed' 'Run Install.bat from the package.'
} else {
  $required = 'content.xml', 'ui.xml', 'ui', 'md', 'libraries', 't\0001-l044.xml', 'x4native.json'
  $missing = @()
  foreach ($rel in $required) { if (-not (Test-Path -LiteralPath (Join-Path $mod $rel))) { $missing += $rel } }
  if ($missing.Count -gt 0) {
    Fail ('extensions\x4mp is incomplete, missing: {0}' -f ($missing -join ', ')) 'Run Install.bat again; the copy did not finish.'
  } else {
    Write-Host '  extensions\x4mp: complete'
  }
  if (-not (Test-Pe64 (Join-Path $mod 'native\x4mp.dll'))) {
    Fail 'extensions\x4mp\native\x4mp.dll is missing or is not a 64-bit dll' 'Install the release package. A source checkout needs a build first.'
  } else {
    Write-Host ('  native\x4mp.dll: {0:n0} bytes' -f (Get-Item -LiteralPath (Join-Path $mod 'native\x4mp.dll')).Length)
  }
  try {
    $nj = Get-Content -LiteralPath (Join-Path $mod 'x4native.json') -Raw | ConvertFrom-Json
    if ($nj.library -ne 'native/x4mp.dll') {
      Fail ('x4native.json points at {0}, expected native/x4mp.dll' -f $nj.library) 'The extension folder is mixed from two versions; run Install.bat again.'
    }
  } catch {
    Fail 'x4native.json is not valid JSON' 'Run Install.bat again.'
  }

  # 3. X4Native, which the mod loads through.
  $nat = Join-Path $ext 'x4native'
  if (-not (Test-Path -LiteralPath $nat)) {
    Fail 'extensions\x4native is not installed, and the mod needs it' 'Run Install.bat from the package.'
  } else {
    foreach ($rel in 'content.xml', 'native\x4native_64.dll', 'native\x4native_core.dll', 'native\version_db\internal_functions.json') {
      if (-not (Test-Path -LiteralPath (Join-Path $nat $rel))) {
        Fail ('extensions\x4native\' + $rel + ' is missing') 'Run Install.bat again; the package may be damaged.'
      }
    }
    $db = Join-Path $nat 'native\version_db\internal_functions.json'
    if (Test-Path -LiteralPath $db) {
      $unmatched = @()
      try {
        $json = Get-Content -LiteralPath $db -Raw | ConvertFrom-Json
        foreach ($fn in $json.functions.PSObject.Properties) {
          $entry = $fn.Value.PSObject.Properties[$script:X4BuildKey]
          if (-not $entry -or -not $entry.Value.rva) { $unmatched += $fn.Name }
        }
      } catch {
        Fail 'version_db\internal_functions.json is not valid JSON' 'Run Install.bat again.'
      }
      if ($unmatched.Count -gt 0) {
        Fail ('version_db has no data for game build {0}: {1} function(s), for example {2}' -f $script:X4BuildKey, $unmatched.Count, $unmatched[0]) 'This X4Native was made for another game build. Install the version built for 900-611726.'
      } elseif ($json) {
        Write-Host ('  version_db: {0} functions for build {1}' -f $json.functions.PSObject.Properties.Count, $script:X4BuildKey)
      }
    }
    $versionFile = Join-Path $nat 'VERSION'
    if (Test-Path -LiteralPath $versionFile) {
      $bad = @()
      $checked = 0
      $unshipped = 0
      foreach ($line in Get-Content -LiteralPath $versionFile) {
        if ($line -match '^([0-9a-f]{64})\s+(\d+)\s+(\S.*)$') {
          # VERSION describes the whole vendored release, relative to its root: "x4native/..." is the
          # runtime that gets installed, and "sdk/..." is upstream headers for modders that are not
          # shipped at all. Anything outside the runtime is not this installation's business.
          $rel = $Matches[3].Trim().Replace('/', '\')
          if ($rel -like 'x4native\*') {
            $rel = $rel.Substring('x4native\'.Length)
          } else {
            $unshipped++
            continue
          }
          $p = Join-Path $nat $rel
          if (-not (Test-Path -LiteralPath $p)) { $bad += "$rel (missing)"; continue }
          $checked++
          if ((Get-Sha256Hex $p) -ne $Matches[1]) { $bad += "$rel (edited)" }
        }
      }
      if ($bad.Count -gt 0) {
        Fail ('X4Native files do not match its VERSION record: {0}' -f (($bad | Select-Object -First 3) -join ', ')) 'Run Install.bat again; do not edit files inside extensions\x4native.'
      } elseif ($checked -gt 0) {
        Write-Host ('  X4Native: {0} files match their recorded hashes' -f $checked)
        if ($unshipped -gt 0) { Write-Host ('  X4Native: {0} upstream file(s) outside the runtime are not shipped, as expected' -f $unshipped) }
      }
    }
  }

  # 4. Files Windows is holding back. A dll that carries the internet mark may not load at all.
  $marked = @()
  foreach ($root in @($mod, $nat)) {
    if (-not $root -or -not (Test-Path -LiteralPath $root)) { continue }
    foreach ($f in Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue) {
      if (Test-MotW $f.FullName) { $marked += $f.FullName }
    }
  }
  if ($marked.Count -gt 0) {
    $dlls = @($marked | Where-Object { $_ -like '*.dll' })
    if ($dlls.Count -gt 0) {
      Fail ('{0} file(s) are blocked by Windows, including a dll: {1}' -f $marked.Count, (Split-Path -Leaf $dlls[0])) 'Run Install.bat again (it unblocks what it copies), or right-click each file > Properties > Unblock.'
    } else {
      Warn ('{0} file(s) carry the internet mark' -f $marked.Count) 'Harmless for plain files; run Install.bat again if you want them cleared.'
    }
  }
}

# 5. What the game loads at start.
Write-Head 'Game settings'
$accounts = @(Get-X4AccountDirs $AccountRoot)
if ($accounts.Count -eq 0) {
  Warn ('No X4 profile folder in {0}' -f $AccountRoot) 'Start the game once: the profile folder is created on first launch.'
} else {
  $account = $accounts[0]
  Write-Host ('  profile: {0}' -f $account)
  $cfg = Join-Path $account 'config.xml'
  if (-not (Test-Path -LiteralPath $cfg)) {
    Warn 'config.xml is missing in the newest profile' 'Start the game once so it writes its settings.'
  } else {
    try {
      [xml]$c = Get-Content -LiteralPath $cfg -Raw
      $safe = $c.SelectSingleNode('//uisafemode')
      if ($safe -and $safe.InnerText -eq 'true') {
        Fail 'Protected UI is on, and no mod can start while it is' 'In X4 open Settings > Game Settings and turn "Protected UI" off, then confirm the restart.'
      } else {
        Write-Host '  Protected UI: off'
      }
    } catch {
      Warn 'config.xml could not be read as XML' 'Start the game once so it writes its settings again.'
    }
  }
  $accContent = Join-Path $account 'content.xml'
  if (-not (Test-Path -LiteralPath $accContent)) {
    Warn 'content.xml is missing in the newest profile: no extension has been enabled yet' 'Start the game, open Extensions, enable "X4 Multiplayer" and "X4Native", then start again.'
  } else {
    foreach ($id in 'x4mp', 'x4native') {
      $state = Get-ExtensionState $accContent $id
      if ($state -ne 'enabled') {
        Fail ('The extension "{0}" is not enabled for this profile ({1})' -f $id, $(if ($state) { $state } else { 'not listed' })) 'In the X4 start menu open Extensions, tick "' + $id + '", then start the game.'
      } else {
        Write-Host ('  extension {0}: enabled' -f $id)
      }
    }
  }
}

# 6. The Windows pieces the dll needs.
Write-Head 'Windows'
# The dll is x64, so the x64 runtime is the one that matters. A 32-bit PowerShell sees System32 through WOW64
# redirection (where vcruntime140_1.dll does not exist) and would report a false blocker: Sysnative is the 64-bit
# view for a 32-bit process.
$sys = if ([Environment]::Is64BitProcess) { Join-Path $env:SystemRoot 'System32' } else { Join-Path $env:SystemRoot 'Sysnative' }
$missingCpp = @()
foreach ($dll in 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') {
  if (-not (Test-Path -LiteralPath (Join-Path $sys $dll))) { $missingCpp += $dll }
}
if ($missingCpp.Count -gt 0) {
  Fail ('The Visual C++ runtime is missing: {0}' -f ($missingCpp -join ', ')) 'Install "Microsoft Visual C++ 2015-2022 Redistributable (x64)" from Microsoft, then start X4 again.'
} else {
  Write-Host '  Visual C++ runtime: present'
}

# 7. The server the game starts from its own menu.
$serverExe = if ($mod) { Join-Path $mod 'server\x4mp-server.exe' } else { $null }
if ($serverExe -and (Test-Path -LiteralPath $serverExe)) {
  Write-Host '  server bundle: present'
  if (-not (Test-Path -LiteralPath (Join-Path $mod 'server\appsettings.json'))) {
    Warn 'The server bundle has no appsettings.json' 'Run Install.bat again: the bundle from the package carries one.'
  }
} else {
  Warn 'The server bundle is not installed, so this game cannot start a server' 'This is fine if you only ever join someone else. Otherwise run Install.bat from the package. The launcher can also host with the server bundle that ships beside it.'
}

$logDir = Join-Path $env:LOCALAPPDATA 'X4MP\logs'
$logOk = $true
try {
  if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
  $probe = Join-Path $logDir '.write-probe'
  [IO.File]::WriteAllText($probe, 'x')
  Remove-Item -LiteralPath $probe -Force
} catch {
  $logOk = $false
}
if ($logOk) { Write-Host ('  log folder: {0}' -f $logDir) } else { Warn ('The log folder cannot be written: {0}' -f $logDir) 'Check the permissions on %LOCALAPPDATA%\X4MP, or run Collect Logs.bat after fixing them.' }

# 8. Ports and the firewall rule other PCs need. A busy port is normal: the launcher or the game may already be hosting.
foreach ($port in 47780, 47790) {
  $busy = $null
  try { $busy = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction Stop } catch { }
  if ($busy) { Note ('TCP port {0} is already in use, so a server is probably running here' -f $port) }
}
$udpListening = $null
try { $udpListening = Get-NetUDPEndpoint -LocalPort 47781 -ErrorAction Stop } catch { }
if ($udpListening) {
  Note 'UDP port 47781 is already in use, so a server is probably running here'
}

# The server listens on all interfaces (TCP 47780/47790, UDP 47781); Windows blocks that inbound traffic by default.
# A host who only plays alone does not need the rule, so this is a note with the exact command, not a blocker.
$rule = $null
try { $rule = @(Get-NetFirewallRule -Direction Inbound -Action Allow -Enabled True -ErrorAction Stop |
    Where-Object { $_.DisplayName -like 'X4MP*' }) } catch { }
if (-not $rule -or $rule.Count -eq 0) {
  Note 'No Windows Firewall rule for X4MP was found, so players on other PCs cannot join this host' 'As administrator, run: New-NetFirewallRule -DisplayName "X4MP" -Direction Inbound -Protocol TCP -LocalPort 47780,47790 -Profile Private -Action Allow; New-NetFirewallRule -DisplayName "X4MP realtime" -Direction Inbound -Protocol UDP -LocalPort 47781 -Profile Private -Action Allow'
}

# 9. The launcher beside the game, the save folder, the disk, the settings files and the sidecar exe.
# None of these can stop the mod from loading, so they are notes and warnings, not blockers.
$launcher = Join-Path $GameDir 'x4mp-launcher.exe'
if (Test-Path -LiteralPath $launcher) {
  Write-Host ('  launcher: {0}' -f $launcher)
} else {
  Note 'The X4MP launcher is not beside the game, so hosting and joining start from the game menu instead' 'The launcher is optional. It checks for updates and writes launch.json. Unzip the whole package into the game folder to get it.'
}

if ($accounts.Count -gt 0) {
  $saveDir = Join-Path $accounts[0] 'save'
  $saveOk = $true
  try {
    if (-not (Test-Path -LiteralPath $saveDir)) { New-Item -ItemType Directory -Path $saveDir -Force | Out-Null }
    $probe = Join-Path $saveDir '.write-probe'
    [IO.File]::WriteAllText($probe, 'x')
    Remove-Item -LiteralPath $probe -Force
  } catch {
    $saveOk = $false
  }
  if ($saveOk) {
    Write-Host ('  save folder: {0}' -f $saveDir)
  } else {
    Warn ('The save folder cannot be written: {0}' -f $saveDir) 'The game cannot save or load there, and neither can the mod. Check the folder permissions, or move Documents\Egosoft\X4 to a writable disk.'
  }
}

$driveFree = $null
try {
  $driveName = (Split-Path -Qualifier $GameDir).TrimEnd(':')
  $driveFree = (Get-PSDrive -Name $driveName -ErrorAction Stop).Free
} catch { }
if ($null -ne $driveFree) {
  $freeGb = [math]::Round($driveFree / 1GB, 1)
  if ($driveFree -lt 5GB) {
    Warn ('Only {0} GB is free on the game drive' -f $freeGb) 'A session save is 25-40 MB per checkpoint and the install is about 0.5 GB. Free some space before hosting a long session.'
  } else {
    Write-Host ('  game drive free: {0} GB' -f $freeGb)
  }
}

$configs = @((Join-Path $env:LOCALAPPDATA 'X4MP\x4mp.json'))
if ($mod) { $configs += (Join-Path $mod 'x4mp.json') }
foreach ($cfg in $configs) {
  if (-not (Test-Path -LiteralPath $cfg)) {
    Write-Host ('  config: {0} (not created yet, the defaults are used)' -f $cfg)
    continue
  }
  try {
    $null = Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json
    Write-Host ('  config: {0}' -f $cfg)
  } catch {
    Warn ('The settings file is not valid JSON: {0}' -f $cfg) 'Delete it and start the game again: the mod writes a fresh one with the defaults.'
  }
}

if ($serverExe -and (Test-Path -LiteralPath $serverExe)) {
  if (Test-Pe64 $serverExe) {
    Write-Host '  server exe: 64-bit'
  } else {
    Warn ('The server executable is not 64-bit: {0}' -f $serverExe) 'Run Install.bat again from the package: the bundle carries the 64-bit build.'
  }
}

# 10. Verdict.
if ($JsonPath) {
  $out = [pscustomobject]@{
    version = $script:X4MPInstallerVersion
    game = $GameDir
    profile = $(if ($accounts.Count -gt 0) { $accounts[0] } else { $null })
    problems = @($problems | ForEach-Object { [pscustomobject]@{ severity = $_.Severity; text = $_.Text; fix = $_.Fix } })
  }
  [IO.File]::WriteAllText($JsonPath, ($out | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
}
$ready = Show-Problems $problems
if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') { Write-Host ''; Read-Host 'Press Enter to close' | Out-Null }
if ($ready) { exit 0 } else { exit 1 }
