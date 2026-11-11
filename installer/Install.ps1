<#
.SYNOPSIS
  Installs the X4MP mod (and the X4Native it needs) into X4: Foundations.
.DESCRIPTION
  Copies extensions\x4mp and extensions\x4native from this package into the game folder found on
  this machine. Nothing else in the game folder is touched, and no save is ever read or written.

  When the game runs from Steam the folder is found automatically. Pass -GameDir when it is not
  (a different drive, a copied install, a beta branch under another library).

  The script refuses to run while X4 is open: the game holds the dll and a copy over a loaded
  module leaves a half-installed extension behind.

  Run it from the .bat file next to it, or: powershell -NoProfile -ExecutionPolicy Bypass -File Install.ps1
.PARAMETER GameDir
  The folder that holds X4.exe. Found automatically when omitted.
.PARAMETER AccountRoot
  The folder that holds the per-profile folders, usually Documents\Egosoft\X4.
.PARAMETER Source
  The package root, when the payload is not next to this script.
.PARAMETER Force
  Install even when the game is detected as running.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File Install.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$GameDir,
  [string]$AccountRoot,
  [string]$Source,
  [switch]$Force,
  [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'Common.ps1')

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $AccountRoot) { $AccountRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Egosoft\X4' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

Write-Head 'X4MP installer'
Write-Host ('Package: {0}' -f $Here)

# 1. Where to install.
if (-not $GameDir) {
  $found = @(Get-X4DirsFound)
  if ($found.Count -eq 0) {
    Write-Host ''
    Write-Host 'X4: Foundations was not found.'
    Write-Host 'Start the game once from Steam, then run this installer again.'
    Write-Host 'If the game is installed somewhere unusual, run: Install.bat "D:\Games\X4 Foundations"'
    exit 1
  }
  $GameDir = $found[0]
  if ($found.Count -gt 1) {
    Write-Host ''
    Write-Host 'More than one X4 install was found:'
    $found | ForEach-Object { Write-Host ('  {0}' -f $_) }
    Write-Host ('Installing into the first one: {0}' -f $GameDir)
    Write-Host 'If you play the other one, run: Install.bat "<that folder>"'
  }
}
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'X4.exe'))) {
  Write-Host ''
  Write-Host ('No X4.exe in {0} - that is not the game folder.' -f $GameDir)
  exit 1
}
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
Write-Host ('Game:    {0}' -f $GameDir)

# The mod is built against one game build. Installing over a different one copies files that will not
# load, so say it now instead of leaving it to Check Install after the fact.
$versionDat = Join-Path $GameDir 'version.dat'
if (Test-Path -LiteralPath $versionDat) {
  $gameVersion = (Get-Content -LiteralPath $versionDat -Raw).Trim()
  if ($gameVersion -ne $script:X4Version) {
    Write-Host ''
    Write-Host ('The game is version {0}, the mod was built for {1}.' -f $gameVersion, $script:X4Version)
    if (-not $Force) {
      Write-Host 'Update or roll back the game, or install the mod build for your version.'
      Write-Host 'Add -Force to install anyway.'
      exit 1
    }
    Write-Host 'Installing anyway because -Force was given.'
  }
}

$payload = Resolve-Payload -Here $Here -Source $Source
if (-not $payload) {
  Write-Host ''
  Write-Host 'The package is incomplete: no extension folder was found next to this script.'
  Write-Host 'Unzip the whole package again, keeping the folder structure.'
  exit 1
}
Write-Host ('Payload: {0} ({1})' -f $payload.X4mp, $payload.Kind)
if (-not $payload.X4Native) {
  Write-Host ''
  Write-Host 'The package is incomplete: it carries no X4Native, which the mod needs to run.'
  exit 1
}
if ($payload.Kind -eq 'checkout' -and -not $payload.Dll) {
  Write-Host ''
  Write-Host 'This is a source checkout and x4mp.dll has not been built yet.'
  Write-Host 'Build it first: cmake --build mod\build\msvc-x64-relwithdebinfo'
  exit 1
}

# 2. Not while the game is open.
$running = @(Get-X4Running)
if ($running.Count -gt 0 -and -not $Force) {
  Write-Host ''
  Write-Host 'X4 is running. Close it and run the installer again.'
  Write-Host ('Running: {0}' -f (($running | ForEach-Object { '{0} ({1})' -f $_.ProcessName, $_.Id }) -join ', '))
  exit 1
}

# 3. Copy. Only the two extension folders are replaced.
$ext = Join-Path $GameDir 'extensions'
if (-not (Test-Path -LiteralPath $ext)) { New-Item -ItemType Directory -Path $ext | Out-Null }

$copied = New-Object System.Collections.Generic.List[string]
function Same-Folder([string]$A, [string]$B) {
  $a2 = [IO.Path]::GetFullPath($A).TrimEnd('\')
  $b2 = [IO.Path]::GetFullPath($B).TrimEnd('\')
  return [string]::Equals($a2, $b2, [StringComparison]::OrdinalIgnoreCase)
}
function Copy-Tree([string]$From, [string]$To) {
  # A player who unzips into the game folder (README-server.md) already has the extension where the
  # copy would put it. Removing the destination first would delete the only copy of the payload, and
  # the install would end with nothing to verify, so the files are left alone and only reported.
  if (Same-Folder $From $To) {
    Write-Host ('Already in place: {0}' -f $To)
    foreach ($item in Get-ChildItem -LiteralPath $From -Recurse -File -Force) { $copied.Add($item.FullName) }
    return
  }
  if (Test-Path -LiteralPath $To) { Remove-Item -LiteralPath $To -Recurse -Force }
  New-Item -ItemType Directory -Path $To | Out-Null
  foreach ($item in Get-ChildItem -LiteralPath $From -Recurse -Force) {
    $rel = $item.FullName.Substring($From.Length + 1)
    $dst = Join-Path $To $rel
    if ($item.PSIsContainer) {
      if (-not (Test-Path -LiteralPath $dst)) { New-Item -ItemType Directory -Path $dst | Out-Null }
    } else {
      $dir = Split-Path -Parent $dst
      if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
      Copy-Item -LiteralPath $item.FullName -Destination $dst -Force
      $copied.Add($dst)
    }
  }
  Write-Host ('Copied extensions\{0}' -f (Split-Path -Leaf $To))
}

Write-Host ''
# An upgrade replaces the whole extension folder, so keep the old one until the new one verifies.
$backupRoot = Join-Path $env:LOCALAPPDATA ('X4MP\install-backup\' + $stamp)
$backups = New-Object System.Collections.Generic.List[string]
function Backup-Tree([string]$Path, [string]$Name, [string]$From) {
  # Nothing is wiped when the payload already lives in the game folder, so there is nothing to keep.
  if (-not (Test-Path -LiteralPath $Path)) { return }
  if (Same-Folder $From $Path) { return }
  if (-not (Test-Path -LiteralPath $backupRoot)) { New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null }
  $dst = Join-Path $backupRoot $Name
  Copy-Item -LiteralPath $Path -Destination $dst -Recurse -Force
  $backups.Add($dst)
  Write-Host ('Backed up extensions\{0}' -f $Name)
}
function Undo-Install() {
  # Put back exactly what was there before this run: the new folder goes, the old one returns.
  foreach ($name in 'x4mp', 'x4native') {
    $dst = Join-Path $ext $name
    if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Recurse -Force -ErrorAction SilentlyContinue }
    $bak = Join-Path $backupRoot $name
    if (Test-Path -LiteralPath $bak) {
      Copy-Item -LiteralPath $bak -Destination $dst -Recurse -Force
      Write-Host ('Restored extensions\{0}' -f $name)
    }
  }
}
if ($PSCmdlet.ShouldProcess($ext, 'install x4mp and x4native')) {
  Backup-Tree (Join-Path $ext 'x4mp') 'x4mp' $payload.X4mp
  Backup-Tree (Join-Path $ext 'x4native') 'x4native' $payload.X4Native
  Copy-Tree $payload.X4mp (Join-Path $ext 'x4mp')
  Copy-Tree $payload.X4Native (Join-Path $ext 'x4native')
  foreach ($extra in $payload.X4NativeExtras) {
    Copy-Item -LiteralPath $extra -Destination (Join-Path $ext ('x4native\' + (Split-Path -Leaf $extra))) -Force
    $copied.Add((Join-Path $ext ('x4native\' + (Split-Path -Leaf $extra))))
  }
  if ($payload.Dll) {
    $native = Join-Path $ext 'x4mp\native'
    if (-not (Test-Path -LiteralPath $native)) { New-Item -ItemType Directory -Path $native | Out-Null }
    Copy-Item -LiteralPath $payload.Dll -Destination (Join-Path $native 'x4mp.dll') -Force
    $copied.Add((Join-Path $native 'x4mp.dll'))
    Write-Host ('Copied native\x4mp.dll ({0})' -f $payload.Dll)
  }
}

# 4. Unblock. A file that came from the internet carries a mark that SmartScreen and X4Native both
#    act on, and an unblocked copy is the difference between a mod that loads and one that does not.
$blocked = 0
foreach ($f in $copied) {
  if (Test-MotW $f) { Unblock-File -LiteralPath $f -ErrorAction SilentlyContinue; $blocked++ }
}
Write-Host ('Unblocked {0} file(s) that Windows had marked as coming from the internet' -f $blocked)

# 5. Verify what landed.
$problems = New-Object System.Collections.Generic.List[object]
$installed = Join-Path $ext 'x4mp'
if (-not (Test-Path -LiteralPath (Join-Path $installed 'content.xml'))) {
  [void]$problems.Add((New-Problem 'blocker' 'extensions\x4mp\content.xml is missing after the copy' 'Run the installer again; if it keeps failing, the archive is damaged.'))
}
if (-not (Test-Pe64 (Join-Path $installed 'native\x4mp.dll'))) {
  [void]$problems.Add((New-Problem 'blocker' 'extensions\x4mp\native\x4mp.dll is missing or is not a 64-bit dll' 'Install the release package, not a source folder; a checkout needs a build first.'))
}
foreach ($rel in 'ui', 'md', 'libraries', 't\0001-l044.xml', 'x4native.json') {
  if (-not (Test-Path -LiteralPath (Join-Path $installed $rel))) {
    [void]$problems.Add((New-Problem 'blocker' ('extensions\x4mp\' + $rel + ' is missing after the copy') 'Unzip the package again and rerun the installer.'))
  }
}
if (-not (Test-Pe64 (Join-Path $ext 'x4native\native\x4native_64.dll'))) {
  [void]$problems.Add((New-Problem 'blocker' 'extensions\x4native\native\x4native_64.dll is missing or is not a 64-bit dll' 'The package is damaged; download it again.'))
}

function Enable-ExtensionInProfile([string]$ContentXml) {
  # The game writes content.xml when its Extensions menu scans the install; this only flips the flag that
  # Check Install blocks on. A missing entry is reported, never invented - the ids have to match the game's.
  $state = @{}
  if (-not (Test-Path -LiteralPath $ContentXml)) { return $state }
  try { [xml]$doc = Get-Content -LiteralPath $ContentXml -Raw } catch { $state['error'] = 'unreadable'; return $state }
  $changed = $false
  foreach ($id in 'x4mp', 'x4native') {
    $node = $null
    foreach ($e in @($doc.Content.extension)) { if ($e.id -eq $id) { $node = $e; break } }
    if (-not $node) { $state[$id] = 'missing'; continue }
    if ($node.enabled -eq 'true') { $state[$id] = 'already enabled'; continue }
    $node.SetAttribute('enabled', 'true')
    $changed = $true
    $state[$id] = 'enabled'
  }
  if ($changed) {
    Copy-Item -LiteralPath $ContentXml -Destination ($ContentXml + '.bak-' + $stamp) -Force
    $doc.Save($ContentXml)
  }
  return $state
}

# 6. Next steps, in the order the player has to do them.
$accounts = @(Get-X4AccountDirs $AccountRoot)
$extensionState = @{}
if ($accounts.Count -gt 0) {
  $extensionState = Enable-ExtensionInProfile (Join-Path $accounts[0] 'content.xml')
}
Write-Head 'Installed'
if ($problems.Count -gt 0) {
  [void](Show-Problems $problems)
  if ($backups.Count -gt 0) {
    Write-Host ''
    Write-Host 'The new copy did not verify, so the previous install is being put back:'
    Undo-Install
  }
  exit 1
}
Write-Host 'Extensions are in place and readable.'
Write-Host ''
Write-Host 'Now do these two things:'
Write-Host '  1. In X4: Settings > Game Settings, turn "Protected UI" off (the game restarts).'
Write-Host '  2. Start a game and open the X4MP window from the main menu.'
Write-Host ''
if ($accounts.Count -eq 0) {
  Write-Host 'No X4 profile folder was found yet: start the game once, then run Install again so the'
  Write-Host 'extensions get enabled for you, or enable "X4 Multiplayer" and "X4Native" by hand.'
} else {
  Write-Host ('Profiles found: {0} (newest: {1})' -f $accounts.Count, $accounts[0])
  foreach ($id in 'x4mp', 'x4native') {
    $s = $extensionState[$id]
    if ($s -eq 'enabled') { Write-Host ('  {0}: enabled in the profile content.xml' -f $id) }
    elseif ($s -eq 'already enabled') { Write-Host ('  {0}: already enabled' -f $id) }
    elseif ($s -eq 'missing') { Write-Host ('  {0}: not listed in the profile yet - open Extensions once and enable it' -f $id) }
    else { Write-Host ('  {0}: could not be enabled automatically - enable it in the Extensions menu' -f $id) }
  }
}
if ($backups.Count -gt 0) {
  Write-Host ''
  Write-Host ('The previous extension folders are kept at {0}' -f $backupRoot)
}
Write-Host ''
Write-Host 'If something does not work, run "Check Install.bat" and "Collect Logs.bat".'
if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') { Write-Host ''; Read-Host 'Press Enter to close' | Out-Null }
exit 0
