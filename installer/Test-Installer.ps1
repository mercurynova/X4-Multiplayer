<#
.SYNOPSIS
  Tests the installer scripts against a fake X4 tree. This is the guard.installer ctest.
.DESCRIPTION
  Builds a throwaway game folder and profile in %TEMP%, assembles a package from this checkout, then
  runs the real scripts: Install.ps1 must install, CheckInstall.ps1 must say READY, one broken thing at
  a time must flip the verdict to NOT READY with exit code 1, and Uninstall.ps1 must remove the two
  extension folders while leaving everything else alone.

  Nothing here touches the real game, the real profile or the real mod data.
.PARAMETER KeepTemp
  Leave the fake tree on disk and print its path, for debugging a failure.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File Test-Installer.ps1
#>
[CmdletBinding()]
param(
  [switch]$KeepTemp
)
$ErrorActionPreference = 'Stop'

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Repo = Split-Path -Parent $Here
$ModExt = Join-Path $Repo 'mod\extension\x4mp'
$X4NativeSrc = Join-Path $Repo 'mod\third_party\x4native\v9.0.0-611726'
$InstallScript = Join-Path $Here 'Install.ps1'
$CheckScript = Join-Path $Here 'CheckInstall.ps1'
$UninstallScript = Join-Path $Here 'Uninstall.ps1'

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

# A 512-byte file that is a valid x64 PE as far as Test-Pe64 is concerned: the test must not depend on a
# built x4mp.dll, and the copy is faster than 8 MB per install run.
function New-FakeDll([string]$Path) {
  $b = New-Object byte[] 512
  $b[0] = 0x4D; $b[1] = 0x5A                       # MZ
  [BitConverter]::GetBytes([int]0x80).CopyTo($b, 0x3C)
  $b[0x80] = 0x50; $b[0x81] = 0x45                  # PE\0\0
  [BitConverter]::GetBytes([uint16]0x8664).CopyTo($b, 0x84)
  [IO.File]::WriteAllBytes($Path, $b)
}

function Write-Text([string]$Path, [string]$Text) {
  [IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

function Set-ProtectedUi([string]$Path, [bool]$On) {
  $v = if ($On) { 'true' } else { 'false' }
  Write-Text $Path ('<?xml version="1.0" encoding="utf-8"?>' + "`n" + '<root>' + "`n" + ('  <uisafemode>{0}</uisafemode>' -f $v) + "`n" + '</root>' + "`n")
}

function Set-ExtensionEnabled([string]$Path, [string]$Id, [bool]$On) {
  $v = if ($On) { 'true' } else { 'false' }
  $text = '<?xml version="1.0" encoding="utf-8"?>' + "`n" + '<content>' + "`n" +
    ('  <extension id="x4mp" enabled="{0}" />' -f $(if ($Id -eq 'x4mp') { $v } else { 'true' })) + "`n" +
    ('  <extension id="x4native" enabled="{0}" />' -f $(if ($Id -eq 'x4native') { $v } else { 'true' })) + "`n" +
    '</content>' + "`n"
  Write-Text $Path $text
}

foreach ($p in @($ModExt, $X4NativeSrc, $InstallScript, $CheckScript, $UninstallScript)) {
  if (-not (Test-Path -LiteralPath $p)) {
    Write-Host ('Missing test input: {0}' -f $p)
    exit 1
  }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-installer-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$game = Join-Path $tmp 'game'
$data = Join-Path $tmp 'profile'
$pkg = Join-Path $tmp 'package'
$account = Join-Path $data '123456789'
$ext = Join-Path $game 'extensions'
$profileContent = Join-Path $account 'content.xml'
$profileConfig = Join-Path $account 'config.xml'
$jsonPath = Join-Path $tmp 'check.json'
$stray = Join-Path $ext 'some-other-mod.txt'
$save = Join-Path $account 'save\quicksave.xml.gz'

try {
  Write-Host 'X4MP installer test'
  Write-Host ('  temp: {0}' -f $tmp)
  Write-Host ''

  # ---- 1. The fake machine and the fake package.
  New-Item -ItemType Directory -Force $ext, $account, (Join-Path $account 'save'), (Join-Path $pkg 'extensions') | Out-Null
  Write-Text (Join-Path $game 'X4.exe') 'not a real game, the installer only checks that it exists'
  Write-Text (Join-Path $game 'version.dat') '900'
  Set-ProtectedUi $profileConfig $false
  Set-ExtensionEnabled $profileContent 'x4mp' $true
  Write-Text $stray 'another mod lives here'
  Write-Text $save 'a save that must survive the uninstall'
  New-Item -ItemType Directory -Force (Join-Path $pkg 'extensions\x4mp'), (Join-Path $pkg 'extensions\x4native') | Out-Null
  Copy-Item -Recurse -Force (Join-Path $ModExt '*') (Join-Path $pkg 'extensions\x4mp')
  Copy-Item -Recurse -Force (Join-Path $X4NativeSrc 'x4native\*') (Join-Path $pkg 'extensions\x4native')
  foreach ($extra in 'LICENSE', 'VERSION') {
    Copy-Item -LiteralPath (Join-Path $X4NativeSrc $extra) -Destination (Join-Path $pkg 'extensions\x4native') -Force
  }
  New-Item -ItemType Directory -Force (Join-Path $pkg 'extensions\x4mp\native') | Out-Null
  New-FakeDll (Join-Path $pkg 'extensions\x4mp\native\x4mp.dll')

  # ---- 2. Install.
  Write-Host 'install'
  $r = Run $InstallScript @('-GameDir', $game, '-AccountRoot', $data, '-Source', $pkg, '-NoPause')
  Expect 'install' $r 0 'Now do these three things:'
  Check (Test-Path -LiteralPath (Join-Path $ext 'x4mp\content.xml')) 'install: extensions\x4mp\content.xml is there'
  Check (Test-Path -LiteralPath (Join-Path $ext 'x4mp\native\x4mp.dll')) 'install: extensions\x4mp\native\x4mp.dll is there'
  Check (Test-Path -LiteralPath (Join-Path $ext 'x4native\VERSION')) 'install: X4Native carries its VERSION file'

  # ---- 3. A clean install must pass the health check.
  Write-Host ''
  Write-Host 'check, clean install'
  $r = Run $CheckScript @('-GameDir', $game, '-AccountRoot', $data, '-NoPause', '-JsonPath', $jsonPath)
  Expect 'check' $r 0 'READY'
  Check ($r.Out.Contains('version_db:')) 'check: the version_db data for this build is reported'
  Check (Test-Path -LiteralPath $jsonPath) 'check: -JsonPath wrote a report'
  if (Test-Path -LiteralPath $jsonPath) {
    $report = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    Check (@($report.problems | Where-Object { $_.severity -eq 'blocker' }).Count -eq 0) 'check: the report has no blocker'
  }

  # ---- 4. Protected UI on: the one setting that silently stops every mod.
  Write-Host ''
  Write-Host 'check, Protected UI on'
  Set-ProtectedUi $profileConfig $true
  $r = Run $CheckScript @('-GameDir', $game, '-AccountRoot', $data, '-NoPause')
  Expect 'protected ui' $r 1 'Protected UI is on'
  Set-ProtectedUi $profileConfig $false
  $r = Run $CheckScript @('-GameDir', $game, '-AccountRoot', $data, '-NoPause')
  Check ($r.Code -eq 0) 'protected ui: back off, the verdict is READY again'

  # ---- 5. A missing dll.
  Write-Host ''
  Write-Host 'check, dll removed'
  Remove-Item -LiteralPath (Join-Path $ext 'x4mp\native\x4mp.dll') -Force
  $r = Run $CheckScript @('-GameDir', $game, '-AccountRoot', $data, '-NoPause')
  Expect 'missing dll' $r 1 'is missing or is not a 64-bit dll'
  $r = Run $InstallScript @('-GameDir', $game, '-AccountRoot', $data, '-Source', $pkg, '-NoPause')
  Check ($r.Code -eq 0) 'missing dll: the installer repairs the tree'

  # ---- 6. X4Native for another game build.
  Write-Host ''
  Write-Host 'check, version_db for another build'
  $db = Join-Path $ext 'x4native\native\version_db\internal_functions.json'
  $parsed = Get-Content -LiteralPath $db -Raw | ConvertFrom-Json
  $first = @($parsed.functions.PSObject.Properties)[0]
  $first.Value.PSObject.Properties.Remove('900-611726')
  Write-Text $db ($parsed | ConvertTo-Json -Depth 20)
  $r = Run $CheckScript @('-GameDir', $game, '-AccountRoot', $data, '-NoPause')
  Expect 'wrong build' $r 1 'no data for game build'
  $r = Run $InstallScript @('-GameDir', $game, '-AccountRoot', $data, '-Source', $pkg, '-NoPause')
  Check ($r.Code -eq 0) 'wrong build: the installer repairs the tree'

  # ---- 7. The extension is installed but not enabled in the profile.
  Write-Host ''
  Write-Host 'check, extension disabled'
  Set-ExtensionEnabled $profileContent 'x4mp' $false
  $r = Run $CheckScript @('-GameDir', $game, '-AccountRoot', $data, '-NoPause')
  Expect 'disabled extension' $r 1 'is not enabled for this profile'
  Set-ExtensionEnabled $profileContent 'x4mp' $true
  $r = Run $CheckScript @('-GameDir', $game, '-AccountRoot', $data, '-NoPause')
  Check ($r.Code -eq 0) 'disabled extension: enabled again, the verdict is READY'

  # ---- 8. The zip unzipped into the game folder: the payload is already where the copy would put
  # it, and the destination must not be deleted as if it were an old install.
  Write-Host ''
  Write-Host 'install, package already in the game folder'
  $inPlace = Join-Path $tmp 'game-in-place'
  New-Item -ItemType Directory -Force (Join-Path $inPlace 'extensions') | Out-Null
  Write-Text (Join-Path $inPlace 'X4.exe') 'not a real game, the installer only checks that it exists'
  Copy-Item -Recurse -Force (Join-Path $pkg 'extensions\*') (Join-Path $inPlace 'extensions')
  $r = Run $InstallScript @('-GameDir', $inPlace, '-AccountRoot', $data, '-Source', $inPlace, '-NoPause')
  Expect 'in place' $r 0 'Already in place'
  Check (Test-Path -LiteralPath (Join-Path $inPlace 'extensions\x4mp\native\x4mp.dll')) 'in place: the dll the package carried is still there'
  Check (Test-Path -LiteralPath (Join-Path $inPlace 'extensions\x4mp\ui\x4mp_economy.lua')) 'in place: the ui folder is still there'
  Check (Test-Path -LiteralPath (Join-Path $inPlace 'extensions\x4native\native\x4native_64.dll')) 'in place: X4Native is still there'

  # ---- 9. Uninstall leaves everything else alone.
  Write-Host ''
  Write-Host 'uninstall'
  $r = Run $UninstallScript @('-GameDir', $game, '-NoPause')
  Expect 'uninstall' $r 0 'Your saves, profiles and screenshots were not touched'
  Check (-not (Test-Path -LiteralPath (Join-Path $ext 'x4mp'))) 'uninstall: extensions\x4mp is gone'
  Check (-not (Test-Path -LiteralPath (Join-Path $ext 'x4native'))) 'uninstall: extensions\x4native is gone'
  Check (Test-Path -LiteralPath $stray) 'uninstall: another extension in extensions\ survived'
  Check (Test-Path -LiteralPath $save) 'uninstall: the save survived'
  Check (Test-Path -LiteralPath $profileContent) 'uninstall: the profile survived'
  $r = Run $UninstallScript @('-GameDir', $game, '-NoPause')
  Check ($r.Code -eq 0) 'uninstall: running it twice is harmless'
  Check ($r.Out.Contains('already gone')) 'uninstall: the second run says the folders are already gone'
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
  Write-Host ('Installer test: {0} of {1} checks failed' -f $script:failed, $script:checks)
  exit 1
}
Write-Host ('Installer test: all {0} checks passed' -f $script:checks)
exit 0
