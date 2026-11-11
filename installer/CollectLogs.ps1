<#
.SYNOPSIS
  Packs the logs and settings a bug report needs into one zip on the Desktop.
.DESCRIPTION
  Copies the mod logs (the live file and its 20 MB rotations), the server console log, the launcher
  files, the game settings, the extension metadata and a short environment report into a staging
  folder and zips it. Both log locations are read: the machine-wide %LOCALAPPDATA%\X4MP\logs and the
  portable install's <game>\extensions\x4mp\logs.

  Join secrets are redacted: launch.json can carry the session password and the admin password, so
  the collected copy has those values replaced with <redacted>. The admin-password.txt file and the
  server database are never included: the first is a secret and the second can hold other players'
  names. Check the zip before you send it if you care about your player name - it is in the log.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File CollectLogs.ps1
#>
[CmdletBinding()]
param(
  [string]$GameDir,
  [string]$AccountRoot,
  [string]$OutPath,
  [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'Common.ps1')

if (-not $AccountRoot) { $AccountRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Egosoft\X4' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if (-not $OutPath) { $OutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) ('X4MP-logs-' + $stamp + '.zip') }

Write-Head 'X4MP Collect Logs'
$stage = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-logs-' + $stamp)
New-Item -ItemType Directory -Path $stage -Force | Out-Null

$taken = New-Object System.Collections.Generic.List[string]
function New-StageFile([string]$Into) {
  $dst = Join-Path $stage $Into
  $dir = Split-Path -Parent $dst
  if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
  return $dst
}
function Take([string]$Path, [string]$Into) {
  if (-not (Test-Path -LiteralPath $Path)) { return }
  $dst = New-StageFile $Into
  Copy-Item -LiteralPath $Path -Destination $dst -Force
  $taken.Add($Into)
}

# A log folder can hold the live file plus rotations (x4mp.log.1 .. .4); take all of them.
function Take-Logs([string]$Dir, [string]$Prefix) {
  if (-not $Dir -or -not (Test-Path -LiteralPath $Dir)) { return }
  foreach ($f in Get-ChildItem -LiteralPath $Dir -File -ErrorAction SilentlyContinue) {
    if ($f.Name -notmatch '\.log(\.\d+)?$') { continue }
    Take $f.FullName ($Prefix + $f.Name)
  }
}

# launch.json carries the session password and the admin password. Write a redacted copy instead of
# the original, and skip the file entirely if it cannot be parsed (never ship a raw secret).
$secretKeys = @('password', 'admin_password', 'adminPassword', 'admin-password')
function Take-JsonRedacted([string]$Path, [string]$Into) {
  if (-not (Test-Path -LiteralPath $Path)) { return }
  $raw = Get-Content -LiteralPath $Path -Raw -ErrorAction SilentlyContinue
  if (-not $raw) { return }
  $obj = $null
  try { $obj = $raw | ConvertFrom-Json } catch { return }
  $out = [ordered]@{}
  foreach ($p in $obj.PSObject.Properties) {
    if ($secretKeys -contains $p.Name) { $out[$p.Name] = '<redacted>' }
    else { $out[$p.Name] = $p.Value }
  }
  $dst = New-StageFile $Into
  $text = ($out | ConvertTo-Json -Depth 5) -replace '\\u003c', '<' -replace '\\u003e', '>'
  [IO.File]::WriteAllText($dst, $text, (New-Object System.Text.UTF8Encoding($false)))
  $taken.Add($Into)
}

# The mod's own state: this is where the interesting lines are.
$local = Join-Path $env:LOCALAPPDATA 'X4MP'
Take-Logs (Join-Path $local 'logs') ''
Take (Join-Path $local 'x4mp.json') 'x4mp.json'
Take-JsonRedacted (Join-Path $local 'launch.json') 'launch.json'
$accounts = @(Get-X4AccountDirs $AccountRoot)
if ($accounts.Count -gt 0) {
  Write-Host ('  profile: {0}' -f $accounts[0])
  Take (Join-Path $accounts[0] 'config.xml') 'profile\config.xml'
  Take (Join-Path $accounts[0] 'content.xml') 'profile\content.xml'
  Take (Join-Path $accounts[0] 'uidata.xml') 'profile\uidata.xml'
}

# The game side: build, game log, the portable install's own logs, and what the extension folder
# says about itself.
if (-not $GameDir) {
  $found = @(Get-X4DirsFound)
  if ($found.Count -gt 0) { $GameDir = $found[0] }
}
if ($GameDir) {
  Write-Host ('  game:    {0}' -f $GameDir)
  Take (Join-Path $GameDir 'version.dat') 'game\version.dat'
  foreach ($log in Get-ChildItem -LiteralPath $GameDir -Filter '*.log' -File -ErrorAction SilentlyContinue) {
    Take $log.FullName ('game\' + $log.Name)
  }
  Take-Logs (Join-Path $GameDir 'extensions\x4mp\logs') 'game\extensions\x4mp\logs\'
  Take (Join-Path $GameDir 'extensions\x4mp\content.xml') 'game\extensions\x4mp\content.xml'
  Take (Join-Path $GameDir 'extensions\x4mp\x4native.json') 'game\extensions\x4mp\x4native.json'
  Take (Join-Path $GameDir 'extensions\x4mp\x4mp.json') 'game\extensions\x4mp\x4mp.json'
  Take (Join-Path $GameDir 'extensions\x4native\VERSION') 'game\extensions\x4native\VERSION'
  Take (Join-Path $GameDir 'extensions\x4native\content.xml') 'game\extensions\x4native\content.xml'
  Take (Join-Path $GameDir 'extensions\x4mp\server\appsettings.json') 'game\extensions\x4mp\server\appsettings.json'
}

# What the machine is, so a reader does not have to ask.
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
$gpu = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object { '{0} {1:n0} MB driver {2}' -f $_.Name, ($_.AdapterRAM / 1MB), $_.DriverVersion })
$info = @(
  ('X4MP installer: ' + $script:X4MPInstallerVersion),
  ('Collected:      ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')),
  ('Computer:       ' + $env:COMPUTERNAME),
  ('Windows:        ' + $(if ($os) { '{0} {1} {2}' -f $os.Caption, $os.Version, $os.BuildNumber } else { [Environment]::OSVersion.VersionString })),
  ('PowerShell:     ' + $PSVersionTable.PSVersion),
  ('64-bit process: ' + [Environment]::Is64BitProcess),
  ('Game folder:    ' + $GameDir),
  ('Profile:        ' + $(if ($accounts.Count -gt 0) { $accounts[0] } else { '(none found)' })),
  ('Profiles found: ' + $accounts.Count),
  'GPUs:'
)
foreach ($g in $gpu) { $info += ('  ' + $g) }
$info += @(
  '',
  'Files in this zip:'
)
foreach ($t in $taken) { $info += ('  ' + $t) }
$info += @(
  '',
  'Not included on purpose: admin-password.txt (a secret) and x4mp.db (other players'' names and money).',
  'Join secrets in launch.json are redacted: password and admin password read "<redacted>".',
  'Ask the person who sent this zip for those only if the problem is about accounts or the ledger.'
)
[IO.File]::WriteAllText((Join-Path $stage 'README.txt'), ($info -join [Environment]::NewLine), (New-Object System.Text.UTF8Encoding($false)))

if ($taken.Count -eq 0) {
  Write-Host ''
  Write-Host 'Nothing to collect: no X4MP log and no game folder were found.'
  Write-Host 'Start the game once with the mod installed, then run this again.'
  exit 1
}

if (Test-Path -LiteralPath $OutPath) { Remove-Item -LiteralPath $OutPath -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $OutPath -CompressionLevel Optimal
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue

$size = (Get-Item -LiteralPath $OutPath).Length
Write-Host ''
Write-Host ('Collected {0} file(s) into:' -f $taken.Count)
Write-Host ('  {0} ({1:n0} KB)' -f $OutPath, ($size / 1KB))
Write-Host ''
Write-Host 'Attach that zip to the bug report. It holds no password and no server database.'
if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') { Write-Host ''; Read-Host 'Press Enter to close' | Out-Null }
exit 0
