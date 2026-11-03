<#
.SYNOPSIS
  Verifies a release zip the way a player gets it: unzip, install into a fake X4 tree, health check, uninstall.
.DESCRIPTION
  The release workflow builds the artifacts; this script answers the question the workflow cannot:
  does the thing in dist\ actually install and pass its own check?

  It never touches the real game, the real profile or the real mod data. A throwaway tree is built in
  %TEMP%, the zip is unpacked next to it, and the installer scripts are run from the unpacked payload,
  not from the checkout, so a missing file in the archive shows up here.

  Checks, in order:
    1. the zip and the SHA256SUMS entry next to it agree;
    2. the payload has every file the mod needs (content.xml, the dll, the UI, md, libraries, x4native);
    3. the dll is a 64-bit build that carries the HTTP save fallback ("X4MP-Download" in its bytes);
    4. Install.ps1 from the unpacked package installs into a fake game folder;
    5. CheckInstall.ps1 says READY with no blocker;
    6. Uninstall.ps1 removes the two extensions and leaves the save and the profile alone.
.PARAMETER Zip
  The mod zip. Defaults to the newest dist\x4mp-mod-*.zip.
.PARAMETER KeepTemp
  Leave the fake tree on disk and print its path, for debugging a failure.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-release.ps1
#>
[CmdletBinding()]
param(
  [string]$Zip,
  [switch]$KeepTemp
)
$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent $PSScriptRoot
$Dist = Join-Path $Repo 'dist'
$PowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $PowerShell)) { $PowerShell = 'powershell' }

$script:checks = 0
$script:failed = 0
$script:tail = ''

function Check([bool]$Ok, [string]$What) {
  $script:checks++
  if ($Ok) {
    Write-Host ('  ok   {0}' -f $What)
  } else {
    $script:failed++
    Write-Host ('  FAIL {0}' -f $What)
    if ($script:tail) { Write-Host $script:tail }
  }
}

function Run([string]$ScriptPath, [string[]]$ScriptArgs) {
  $prev = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try {
    $out = (& $PowerShell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @ScriptArgs 2>&1 | Out-String)
    $code = $LASTEXITCODE
  } finally {
    $ErrorActionPreference = $prev
  }
  $lines = @($out -split "`r?`n" | Where-Object { $_.Trim() })
  $script:tail = if ($lines.Count -gt 12) { ($lines | Select-Object -Last 12) -join "`n" } else { $lines -join "`n" }
  return [pscustomobject]@{ Code = $code; Out = $out }
}

function Expect([string]$Label, $Result, [int]$Code, [string]$Needle) {
  Check ($Result.Code -eq $Code) ('{0}: exit code {1}, expected {2}' -f $Label, $Result.Code, $Code)
  Check ($Result.Out.Contains($Needle)) ('{0}: output contains "{1}"' -f $Label, $Needle)
}

function Write-Text([string]$Path, [string]$Text) {
  [IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

function Set-ProtectedUi([string]$Path, [bool]$On) {
  $v = if ($On) { 'true' } else { 'false' }
  Write-Text $Path ('<?xml version="1.0" encoding="utf-8"?>' + "`n" + '<root>' + "`n" + ('  <uisafemode>{0}</uisafemode>' -f $v) + "`n" + '</root>' + "`n")
}

function Set-ExtensionEnabled([string]$Path) {
  $text = '<?xml version="1.0" encoding="utf-8"?>' + "`n" + '<content>' + "`n" +
    '  <extension id="x4mp" enabled="true" />' + "`n" +
    '  <extension id="x4native" enabled="true" />' + "`n" +
    '</content>' + "`n"
  Write-Text $Path $text
}

# A 64-bit PE check without loading the file: MZ, a PE header at e_lfanew and machine 0x8664.
function Test-Pe64([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return $false }
  $b = [IO.File]::ReadAllBytes($Path)
  if ($b.Length -lt 0x100) { return $false }
  if ($b[0] -ne 0x4D -or $b[1] -ne 0x5A) { return $false }
  $pe = [BitConverter]::ToInt32($b, 0x3C)
  if ($pe -le 0 -or ($pe + 6) -ge $b.Length) { return $false }
  if ($b[$pe] -ne 0x50 -or $b[$pe + 1] -ne 0x45) { return $false }
  return ([BitConverter]::ToUInt16($b, $pe + 4) -eq 0x8664)
}

# Bytes of a file as a Latin-1 string, so a plain ASCII needle can be searched in a binary.
function Read-BinaryText([string]$Path) {
  $bytes = [IO.File]::ReadAllBytes($Path)
  return [Text.Encoding]::GetEncoding(28591).GetString($bytes)
}

if (-not $Zip) {
  $candidate = Get-ChildItem -LiteralPath $Dist -Filter 'x4mp-mod-*.zip' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
  if (-not $candidate) {
    Write-Host ('No dist\x4mp-mod-*.zip to verify. Build one with tools\release-local.ps1.')
    exit 1
  }
  $Zip = $candidate.FullName
}
if (-not (Test-Path -LiteralPath $Zip)) {
  Write-Host ('Missing zip: {0}' -f $Zip)
  exit 1
}
$Zip = (Resolve-Path -LiteralPath $Zip).Path
$zipName = Split-Path -Leaf $Zip

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-release-verify-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$pkg = Join-Path $tmp 'package'
$game = Join-Path $tmp 'game'
$profile = Join-Path $tmp 'profile'
$account = Join-Path $profile '123456789'
$ext = Join-Path $game 'extensions'
$jsonPath = Join-Path $tmp 'check.json'
$stray = Join-Path $ext 'some-other-mod.txt'
$save = Join-Path $account 'save\quicksave.xml.gz'
$profileContent = Join-Path $account 'content.xml'
$profileConfig = Join-Path $account 'config.xml'

try {
  Write-Host 'X4MP release verification'
  Write-Host ('  zip:  {0}' -f $Zip)
  Write-Host ('  temp: {0}' -f $tmp)
  Write-Host ''

  # ---- 1. Unpack.
  Write-Host 'unpack'
  New-Item -ItemType Directory -Force $pkg | Out-Null
  Expand-Archive -LiteralPath $Zip -DestinationPath $pkg -Force
  $files = @(Get-ChildItem -LiteralPath $pkg -Recurse -File -Force)
  $bytes = ($files | Measure-Object -Property Length -Sum).Sum
  Write-Host ('  {0} files, {1:N1} MB unpacked' -f $files.Count, ($bytes / 1MB))
  Check ($files.Count -gt 100) ('payload has {0} files, more than 100' -f $files.Count)

  $installScript = Join-Path $pkg 'installer\Install.ps1'
  $checkScript = Join-Path $pkg 'installer\CheckInstall.ps1'
  $uninstallScript = Join-Path $pkg 'installer\Uninstall.ps1'
  $payloadDll = Join-Path $pkg 'extensions\x4mp\native\x4mp.dll'

  # ---- 2. The archive carries the files a player needs.
  Write-Host ''
  Write-Host 'payload'
  foreach ($rel in 'extensions\x4mp\content.xml', 'extensions\x4mp\native\x4mp.dll',
                  'extensions\x4mp\x4native.json', 'extensions\x4mp\t\0001-l044.xml',
                  'extensions\x4mp\ui', 'extensions\x4mp\md', 'extensions\x4mp\libraries',
                  'extensions\x4mp\server\x4mp-server.exe',
                  'extensions\x4native\native\x4native_64.dll', 'extensions\x4native\VERSION',
                  'x4mp-launcher.exe', 'Install.bat', 'Check Install.bat', 'Collect Logs.bat', 'Uninstall.bat',
                  'installer\Install.ps1', 'installer\CheckInstall.ps1', 'installer\CollectLogs.ps1',
                  'installer\Uninstall.ps1', 'installer\Common.ps1') {
    Check (Test-Path -LiteralPath (Join-Path $pkg $rel)) ('payload: {0}' -f $rel)
  }

  $launcher = Join-Path $pkg 'x4mp-launcher.exe'
  Check ((Test-Path -LiteralPath $launcher) -and ((Get-Item -LiteralPath $launcher).Length -gt 1MB)) 'payload: the launcher is a real executable (> 1 MB)'

  $serverExe = Join-Path $pkg 'extensions\x4mp\server\x4mp-server.exe'
  Check ((Test-Path -LiteralPath $serverExe) -and ((Get-Item -LiteralPath $serverExe).Length -gt 100KB)) 'payload: the bundled server executable is real (> 100 KB)'
  $serverFiles = @(Get-ChildItem -LiteralPath (Join-Path $pkg 'extensions\x4mp\server') -Recurse -File -Force)
  Check ($serverFiles.Count -gt 50) ('payload: the bundled server has {0} files' -f $serverFiles.Count)

  # ---- 3. The mod binary is the one built from this tree.
  Write-Host ''
  Write-Host 'mod binary'
  Check (Test-Path -LiteralPath $payloadDll) 'binary: extensions\x4mp\native\x4mp.dll is in the archive'
  if (Test-Path -LiteralPath $payloadDll) {
    $dllSize = (Get-Item -LiteralPath $payloadDll).Length
    Check ($dllSize -gt 1MB) ('binary: the dll is {0:N0} bytes, a release build not a stub' -f $dllSize)
    Check (Test-Pe64 $payloadDll) 'binary: the dll is a 64-bit PE'
    $needle = Read-BinaryText $payloadDll
    # Capability bit 11 is a number, so the enum name "SaveHttp" never reaches the binary; the strings that do are the
    # downloader's own log lines and the session's fallback warning.
    Check ($needle.Contains('X4MP-Download')) 'binary: the dll carries the HTTP save fallback (X4MP-Download)'
    Check ($needle.Contains('save: HTTP download')) "binary: the dll carries the HTTP downloader's log lines"
    Check ($needle.Contains('HTTP fallback')) 'binary: the dll carries the in-band fallback path'
  }

  # ---- 4. The checksum file agrees with the archive.
  Write-Host ''
  Write-Host 'checksums'
  $sums = Join-Path $Dist 'SHA256SUMS'
  Check (Test-Path -LiteralPath $sums) 'checksums: dist\SHA256SUMS exists'
  if (Test-Path -LiteralPath $sums) {
    $line = @(Get-Content -LiteralPath $sums | Where-Object { $_ -match ('\s' + [Regex]::Escape($zipName) + '$') }) | Select-Object -First 1
    Check ([bool]$line) ('checksums: SHA256SUMS lists {0}' -f $zipName)
    if ($line) {
      $expected = ($line -split '\s+')[0].ToLowerInvariant()
      $actual = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
      Check ($expected -eq $actual) ('checksums: the hash matches ({0})' -f $actual.Substring(0, 16))
    }
  }

  # ---- 5. A player's machine: a game folder, a profile, a save, another extension.
  Write-Host ''
  Write-Host 'install'
  New-Item -ItemType Directory -Force $ext, $account, (Join-Path $account 'save') | Out-Null
  Write-Text (Join-Path $game 'X4.exe') 'not a real game, the installer only checks that it exists'
  Write-Text (Join-Path $game 'version.dat') '900'
  Set-ProtectedUi $profileConfig $false
  Set-ExtensionEnabled $profileContent
  Write-Text $stray 'another mod lives here'
  Write-Text $save 'a save that must survive the uninstall'

  $r = Run $installScript @('-GameDir', $game, '-AccountRoot', $profile, '-Source', $pkg, '-NoPause')
  Expect 'install' $r 0 'Now do these three things:'
  Check (Test-Path -LiteralPath (Join-Path $ext 'x4mp\content.xml')) 'install: extensions\x4mp\content.xml is there'
  $installedDll = Join-Path $ext 'x4mp\native\x4mp.dll'
  Check (Test-Path -LiteralPath $installedDll) 'install: extensions\x4mp\native\x4mp.dll is there'
  if (Test-Path -LiteralPath $installedDll) {
    Check (Test-Pe64 $installedDll) 'install: the installed dll is a 64-bit PE'
    Check ((Get-Item -LiteralPath $installedDll).Length -gt 1MB) 'install: the installed dll is the full build'
  }
  Check (Test-Path -LiteralPath (Join-Path $ext 'x4native\VERSION')) 'install: X4Native carries its VERSION file'
  Check (Test-Path -LiteralPath (Join-Path $ext 'x4mp\server\x4mp-server.exe')) 'install: the server came with the extension'

  # ---- 6. The health check a player runs from the start menu.
  Write-Host ''
  Write-Host 'check'
  $r = Run $checkScript @('-GameDir', $game, '-AccountRoot', $profile, '-NoPause', '-JsonPath', $jsonPath)
  Expect 'check' $r 0 'READY'
  Check ($r.Out.Contains('version_db:')) 'check: the version_db data for this build is reported'
  Check (Test-Path -LiteralPath $jsonPath) 'check: -JsonPath wrote a report'
  if (Test-Path -LiteralPath $jsonPath) {
    $report = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    Check (@($report.problems | Where-Object { $_.severity -eq 'blocker' }).Count -eq 0) 'check: the report has no blocker'
  }

  # ---- 7. Uninstall leaves the player's data alone.
  Write-Host ''
  Write-Host 'uninstall'
  $r = Run $uninstallScript @('-GameDir', $game, '-NoPause')
  Expect 'uninstall' $r 0 'Your saves, profiles and screenshots were not touched'
  Check (-not (Test-Path -LiteralPath (Join-Path $ext 'x4mp'))) 'uninstall: extensions\x4mp is gone'
  Check (-not (Test-Path -LiteralPath (Join-Path $ext 'x4native'))) 'uninstall: extensions\x4native is gone'
  Check (Test-Path -LiteralPath $stray) 'uninstall: another extension in extensions\ survived'
  Check (Test-Path -LiteralPath $save) 'uninstall: the save survived'
  Check (Test-Path -LiteralPath $profileContent) 'uninstall: the profile survived'
} finally {
  if ($KeepTemp) {
    Write-Host ''
    Write-Host ('Kept: {0}' -f $tmp)
  } elseif (Test-Path -LiteralPath $tmp) {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
  }
}

Write-Host ''
if ($script:failed -gt 0) {
  Write-Host ('Release verification: {0} of {1} checks failed' -f $script:failed, $script:checks)
  exit 1
}
Write-Host ('Release verification: all {0} checks passed for {1}' -f $script:checks, $zipName)
exit 0
