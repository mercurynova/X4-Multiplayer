# Common.ps1 - helpers shared by Install.ps1, CheckInstall.ps1, CollectLogs.ps1 and Uninstall.ps1.
# Plain Windows PowerShell 5.1 has to run this file, and 5.1 reads a .ps1 without a BOM as ANSI, so the
# whole installer stays ASCII: a plain '-' instead of a dash, '*' for a bullet, no curly quotes.

$ErrorActionPreference = 'Stop'

$script:X4MPInstallerVersion = '0.1.0'
# The game build this package was made for: version.dat holds the version, version_db keys are
# "<version without the dot>-<build number>".
$script:X4Version = '900'
$script:X4BuildKey = '900-611726'
$script:X4NativeTag = 'v9.0.0-611726'

function Write-Head([string]$Title) {
  Write-Host ''
  Write-Host $Title
  Write-Host ('-' * $Title.Length)
}

function New-Problem([string]$Severity, [string]$Text, [string]$Fix) {
  [pscustomobject]@{ Severity = $Severity; Text = $Text; Fix = $Fix }
}

# A problem list is the only way a check reports anything: a verdict is derived from it, never
# written by hand, so the exit code and the printed text cannot disagree.
function Show-Problems($Problems) {
  $rank = @{ blocker = 0; warning = 1; note = 2 }
  $blockers = @($Problems | Where-Object { $_.Severity -eq 'blocker' }).Count
  $warnings = @($Problems | Where-Object { $_.Severity -eq 'warning' }).Count
  $notes = @($Problems | Where-Object { $_.Severity -eq 'note' }).Count
  if ($Problems.Count -gt 0) {
    Write-Host ''
    Write-Host 'Problems'
    Write-Host '--------'
    $i = 0
    foreach ($p in @($Problems | Sort-Object { $rank[$_.Severity] })) {
      $i++
      Write-Host ('  {0}. [{1}] {2}' -f $i, $p.Severity, $p.Text)
      if ($p.Fix) { Write-Host ('     Fix: {0}' -f $p.Fix) }
    }
  }
  Write-Host ''
  if ($blockers -gt 0) {
    Write-Host ('NOT READY: {0} blocker(s), {1} warning(s), {2} note(s)' -f $blockers, $warnings, $notes)
  } elseif ($warnings -gt 0) {
    Write-Host ('READY, with {0} warning(s)' -f $warnings)
  } else {
    Write-Host 'READY'
  }
  Write-Host 'Send this window if you need help.'
  return ($blockers -eq 0)
}

function Get-Sha256Hex([string]$Path) {
  # not Get-FileHash: a 5.1 child of pwsh 7 cannot autoload Microsoft.PowerShell.Utility.
  $s = [IO.File]::OpenRead($Path)
  try {
    $h = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($h.ComputeHash($s)) -replace '-', '').ToLowerInvariant() }
    finally { $h.Dispose() }
  } finally { $s.Dispose() }
}

# Mark-of-the-Web: the file carries an NTFS alternate data stream, which X4Native refuses to load
# from in some Windows configurations and which SmartScreen uses to block the dll silently.
function Test-MotW([string]$Path) {
  try {
    $streams = Get-Item -LiteralPath $Path -Stream * -ErrorAction Stop
    foreach ($s in $streams) { if ($s.Stream -eq 'Zone.Identifier') { return $true } }
  } catch { }
  return $false
}

function Get-X4DirsFound {
  # Every X4 install that can be found: the usual Steam folder, the Steam registry keys, and every
  # library listed in libraryfolders.vdf. Duplicates are dropped case-insensitively.
  $result = New-Object System.Collections.Generic.List[string]
  $seen = @{}
  $add = {
    param([string]$p)
    if (-not $p) { return }
    if (-not (Test-Path -LiteralPath (Join-Path $p 'X4.exe'))) { return }
    try { $full = (Resolve-Path -LiteralPath $p).Path } catch { return }
    $full = $full.TrimEnd('\')
    $key = $full.ToLowerInvariant()
    if ($seen.ContainsKey($key)) { return }
    $seen[$key] = $true
    [void]$result.Add($full)
  }
  & $add 'C:\Program Files (x86)\Steam\steamapps\common\X4 Foundations'
  $roots = New-Object System.Collections.Generic.List[string]
  foreach ($pair in @(
      @{ Path = 'HKCU:\Software\Valve\Steam'; Name = 'SteamPath' },
      @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Name = 'InstallPath' },
      @{ Path = 'HKLM:\SOFTWARE\Valve\Steam'; Name = 'InstallPath' })) {
    if (Test-Path $pair.Path) {
      try {
        $v = (Get-ItemProperty -Path $pair.Path -Name $pair.Name -ErrorAction Stop).$($pair.Name)
        if ($v) { [void]$roots.Add($v) }
      } catch { }
    }
  }
  foreach ($root in $roots) {
    & $add (Join-Path $root 'steamapps\common\X4 Foundations')
    $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
    if (-not (Test-Path -LiteralPath $vdf)) { continue }
    foreach ($line in Get-Content -LiteralPath $vdf -ErrorAction SilentlyContinue) {
      if ($line -match '^\s*"path"\s+"([^"]+)"') {
        $lib = $Matches[1].Replace('\\', '\')
        & $add (Join-Path $lib 'steamapps\common\X4 Foundations')
      }
    }
  }
  return $result.ToArray()
}

function Get-X4AccountDirs([string]$AccountRoot) {
  # One folder per X4 profile, named after a numeric id, newest last played first.
  if (-not (Test-Path -LiteralPath $AccountRoot)) { return @() }
  return @(Get-ChildItem -LiteralPath $AccountRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d+$' } |
    Sort-Object LastWriteTime -Descending |
    ForEach-Object { $_.FullName })
}

function Test-Pe64([string]$Path) {
  # A real 64-bit PE, not a placeholder or a truncated download.
  if (-not (Test-Path -LiteralPath $Path)) { return $false }
  $s = $null
  try {
    $s = [IO.File]::OpenRead($Path)
    $hdr = New-Object byte[] 64
    if ($s.Read($hdr, 0, 64) -ne 64) { return $false }
    if ($hdr[0] -ne 0x4D -or $hdr[1] -ne 0x5A) { return $false }  # MZ
    $lfanew = [BitConverter]::ToInt32($hdr, 0x3C)
    [void]$s.Seek($lfanew, 'Begin')
    $sig = New-Object byte[] 6
    if ($s.Read($sig, 0, 6) -ne 6) { return $false }
    if ($sig[0] -ne 0x50 -or $sig[1] -ne 0x45) { return $false }  # PE
    $machine = [BitConverter]::ToUInt16($sig, 4)
    return ($machine -eq 0x8664)  # x64
  } catch {
    return $false
  } finally {
    if ($s) { $s.Dispose() }
  }
}

function Get-ExtensionState([string]$ContentXml, [string]$Id) {
  # 'enabled', 'disabled', or $null when the file does not list the extension at all.
  if (-not (Test-Path -LiteralPath $ContentXml)) { return $null }
  try {
    [xml]$doc = Get-Content -LiteralPath $ContentXml -Raw
  } catch {
    return 'unreadable'
  }
  foreach ($e in @($doc.Content.extension)) {
    if ($e.id -eq $Id) {
      if ($e.enabled -eq 'false') { return 'disabled' }
      return 'enabled'
    }
  }
  return $null
}

function Get-X4Running {
  return @(Get-Process -Name X4, X4b -ErrorAction SilentlyContinue)
}

# Where the payload is. The release zip and a checkout look different on disk, and both are run by
# developers, so both are resolved here instead of in each script.
function Resolve-Payload([string]$Here, [string]$Source) {
  $repo = Split-Path -Parent $Here
  $roots = New-Object System.Collections.Generic.List[string]
  if ($Source) { [void]$roots.Add((Resolve-Path -LiteralPath $Source).Path) }
  [void]$roots.Add($repo)

  $x4mp = $null
  $x4native = $null
  $extras = New-Object System.Collections.Generic.List[string]
  foreach ($root in $roots) {
    if (-not $x4mp) {
      foreach ($rel in 'extensions\x4mp', 'mod\extension\x4mp', 'x4mp') {
        $p = Join-Path $root $rel
        if (Test-Path -LiteralPath (Join-Path $p 'content.xml')) { $x4mp = (Resolve-Path -LiteralPath $p).Path; break }
      }
    }
    if (-not $x4native) {
      foreach ($rel in 'extensions\x4native', "mod\third_party\x4native\$($script:X4NativeTag)\x4native") {
        $p = Join-Path $root $rel
        if (Test-Path -LiteralPath (Join-Path $p 'content.xml')) {
          $x4native = (Resolve-Path -LiteralPath $p).Path
          foreach ($extra in 'LICENSE', 'VERSION') {
            $e = Join-Path (Split-Path -Parent $x4native) $extra
            if (Test-Path -LiteralPath $e) { [void]$extras.Add($e) }
          }
          break
        }
      }
    }
  }
  if (-not $x4mp) { return $null }

  # A checkout keeps the dll in build\, the zip carries it inside the extension.
  $dll = $null
  if (-not (Test-Path -LiteralPath (Join-Path $x4mp 'native\x4mp.dll'))) {
    $candidates = @()
    foreach ($rel in 'mod\build\msvc-x64-release\x4mp.dll') {
      $p = Join-Path $repo $rel
      if (Test-Path -LiteralPath $p) { $candidates += (Get-Item -LiteralPath $p) }
    }
    $buildDir = Join-Path $repo 'mod\build'
    if ((Test-Path -LiteralPath $buildDir) -and $candidates.Count -eq 0) {
      $candidates = @(Get-ChildItem -LiteralPath $buildDir -Recurse -Filter 'x4mp.dll' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    }
    if ($candidates.Count -gt 0) { $dll = $candidates[0].FullName }
  }

  return [pscustomobject]@{
    X4mp = $x4mp
    X4Native = $x4native
    X4NativeExtras = $extras.ToArray()
    Dll = $dll
    Kind = $(if ($x4mp -like '*\extensions\x4mp') { 'package' } else { 'checkout' })
  }
}
