<#
.SYNOPSIS
  Rehearse .github/workflows/release.yml on this machine, without pushing anything.

.DESCRIPTION
  The release pipeline only runs on a v* tag push, so nothing proves the artifacts until a
  release is cut. This script runs the same steps locally and leaves the same files in dist\:
  the server zip (single-file win-x64), the server bundle zip (the folder form the game starts),
  the linux-x64 tarball, the launcher exe, the mod zip a player unzips, and SHA256SUMS.

  Differences from the workflow, on purpose:
    - the tag defaults to v0.0.0-local, so a rehearsal never looks like a release;
    - no gh release create and no upload-artifact (both need GitHub);
    - the bundle is published straight into out\server-win-x64 instead of being zipped and
      unzipped again; the zip itself is still produced and checked.
  tools\verify-release.ps1 takes the mod zip from here and runs the installer against it.

.PARAMETER Tag
  Version stamped into the file names. Default v0.0.0-local.
.PARAMETER SkipServer
  Do not publish the server (reuse out\win-x64, out\server-win-x64, out\linux-x64).
.PARAMETER SkipLauncher
  Do not publish the launcher (reuse out\launcher-win-x64).
.PARAMETER SkipMod
  Do not build the mod (reuse mod\build\msvc-x64-release\x4mp.dll).
.PARAMETER SkipPackage
  Stop after the publishes; do not assemble pkg\ or dist\.
.PARAMETER SkipChecksums
  Do not rewrite dist\SHA256SUMS.
.PARAMETER SkipWebBuild
  Pass -p:SkipWebBuild=true to the publishes. Use it on a machine whose Node is too old for
  the web build (the real release uses Node 24).
.PARAMETER ReuseOutputs
  Alias for -SkipServer -SkipLauncher -SkipMod: package whatever is already in out\ and
  mod\build\msvc-x64-release.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/release-local.ps1

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/release-local.ps1 -Tag v1.0.0-rc1 -SkipWebBuild
#>
[CmdletBinding()]
param(
  [string]$Tag = 'v0.0.0-local',
  [switch]$SkipServer,
  [switch]$SkipLauncher,
  [switch]$SkipMod,
  [switch]$SkipPackage,
  [switch]$SkipChecksums,
  [switch]$SkipWebBuild,
  [switch]$ReuseOutputs
)

$ErrorActionPreference = 'Stop'
if ($ReuseOutputs) { $SkipServer = $true; $SkipLauncher = $true; $SkipMod = $true }

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Repo = Split-Path -Parent $Here
$Dist = Join-Path $Repo 'dist'
$Pkg = Join-Path $Repo ('pkg\x4mp-mod-' + $Tag)
$ModDll = Join-Path $Repo 'mod\build\msvc-x64-release\x4mp.dll'
$X4Native = Join-Path $Repo 'mod\third_party\x4native\v9.0.0-611726'

$script:started = Get-Date
$script:artifacts = New-Object System.Collections.Generic.List[object]

function Say([string]$Text) { Write-Host $Text }

function Step([string]$Name, [scriptblock]$Body) {
  Say ''
  Say ('== {0}' -f $Name)
  $clock = [Diagnostics.Stopwatch]::StartNew()
  & $Body
  $clock.Stop()
  Say ('   {0} in {1:N1} s' -f $Name, $clock.Elapsed.TotalSeconds)
}

function Fail([string]$Text) { throw ('release-local: ' + $Text) }

# Native exit codes are not exceptions in Windows PowerShell, so every tool call ends with this.
function Expect([string]$What) {
  if ($LASTEXITCODE -ne 0) { Fail ('{0} exited with {1}' -f $What, $LASTEXITCODE) }
}

function Publish([string]$Project, [string]$Profile) {
  $profileArg = '-p:PublishProfile=' + $Profile
  $argv = @('publish', $Project, '-c', 'Release', $profileArg, '--nologo', '-v', 'minimal')
  if ($SkipWebBuild) { $argv += '-p:SkipWebBuild=true' }
  Say ('   dotnet ' + ($argv -join ' '))
  & dotnet @argv
  Expect ('dotnet publish ' + $Profile)
}

function Compress([string]$SourceDir, [string]$ZipPath) {
  if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
  [IO.Compression.ZipFile]::CreateFromDirectory($SourceDir, $ZipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
  $item = Get-Item -LiteralPath $ZipPath
  $script:artifacts.Add([pscustomobject]@{ Name = $item.Name; MB = [Math]::Round($item.Length / 1MB, 1) })
  Say ('   {0} ({1:N1} MB)' -f $item.Name, ($item.Length / 1MB))
}

function Assert-File([string]$Path, [string]$What) {
  if (-not (Test-Path -LiteralPath $Path)) { Fail ($What + ' is missing: ' + $Path) }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---------------------------------------------------------------- preconditions
foreach ($p in (Join-Path $Repo 'mod\build.ps1'), (Join-Path $Repo 'server\src\X4MP.Server\X4MP.Server.csproj'),
               (Join-Path $Repo 'launcher\X4MP.Launcher\X4MP.Launcher.csproj')) {
  if (-not (Test-Path -LiteralPath $p)) { Fail ('not the X4MP checkout (missing ' + $p + ')') }
}
if ($Tag -notmatch '^[A-Za-z0-9._-]+$') { Fail ('the tag may only hold letters, digits, dot, dash and underscore: ' + $Tag) }

Say ('X4MP local release rehearsal, tag {0}' -f $Tag)
Say ('  repo: {0}' -f $Repo )
$head = (& git -C $Repo log -1 --format='%h %s') 2>$null
Say ('  head: {0}' -f $head)
$dirty = (& git -C $Repo status --porcelain) 2>$null
if ($dirty) { Say '  note: the working tree is dirty; the artifacts carry the working copy, not the commit' }

New-Item -ItemType Directory -Force $Dist | Out-Null

# The web build is the one step that needs a recent Node. The bundled Node 24 from the agent
# toolbox is used when it is there and nothing newer is already on PATH.
if (-not $SkipWebBuild) {
  $nodeExe = Get-Command node -ErrorAction SilentlyContinue
  $version = if ($nodeExe) { (& node --version) } else { '' }
  if (-not $version -or [int]($version.TrimStart('v').Split('.')[0]) -lt 20) {
    $fallback = 'C:\Users\Admin\AppData\Local\Temp\opencode\node-v24.22.2-win-x64'
    if (Test-Path -LiteralPath (Join-Path $fallback 'node.exe')) {
      $env:PATH = $fallback + ';' + $env:PATH
      Say ('  node: using {0} ({1})' -f $fallback, (& node --version))
    } else {
      Fail ('the web build needs Node 20+ (found "{0}"); install a newer Node or pass -SkipWebBuild' -f $version)
    }
  } else {
    Say ('  node: {0}' -f $version)
  }
}

# The mod build uses vcpkg in manifest mode; a binary cache keeps a rehearsal from rebuilding
# FlatBuffers, Catch2 and nlohmann-json from source every time.
if (-not (Get-Item -Path Env:VCPKG_DEFAULT_BINARY_CACHE -ErrorAction SilentlyContinue)) {
  $env:VCPKG_DEFAULT_BINARY_CACHE = Join-Path $Repo '.vcpkg-archives'
}
New-Item -ItemType Directory -Force $env:VCPKG_DEFAULT_BINARY_CACHE | Out-Null

# ---------------------------------------------------------------- 1. server (win-x64, bundle, linux-x64)
if (-not $SkipServer) {
  Step 'server publish: win-x64 (single file)' {
    Publish 'server/src/X4MP.Server' 'win-x64'
    Copy-Item -LiteralPath (Join-Path $Repo 'docs\README-server.md') -Destination (Join-Path $Repo 'out\win-x64\README.md') -Force
    Assert-File (Join-Path $Repo 'out\win-x64\x4mp-server.exe') 'out\win-x64\x4mp-server.exe'
    Compress (Join-Path $Repo 'out\win-x64') (Join-Path $Dist ('x4mp-server-{0}-win-x64.zip' -f $Tag))
  }

  Step 'server publish: win-x64-bundle (the folder the game starts)' {
    Publish 'server/src/X4MP.Server' 'win-x64-bundle'
    Assert-File (Join-Path $Repo 'out\server-win-x64\x4mp-server.exe') 'out\server-win-x64\x4mp-server.exe'
    Compress (Join-Path $Repo 'out\server-win-x64') (Join-Path $Dist ('x4mp-server-{0}-win-x64-bundle.zip' -f $Tag))
  }

  Step 'server publish: linux-x64 (cross-target tarball)' {
    Publish 'server/src/X4MP.Server' 'linux-x64'
    Assert-File (Join-Path $Repo 'out\linux-x64\x4mp-server') 'out\linux-x64\x4mp-server'
    $tar = Join-Path $Dist ('x4mp-server-{0}-linux-x64.tar.gz' -f $Tag)
    if (Test-Path -LiteralPath $tar) { Remove-Item -LiteralPath $tar -Force }
    & tar -czf $tar -C (Join-Path $Repo 'out\linux-x64') .
    Expect 'tar'
    $item = Get-Item -LiteralPath $tar
    $script:artifacts.Add([pscustomobject]@{ Name = $item.Name; MB = [Math]::Round($item.Length / 1MB, 1) })
    Say ('   {0} ({1:N1} MB)' -f $item.Name, ($item.Length / 1MB))
  }
} else {
  Say '  server: reused from out\'
}

# ---------------------------------------------------------------- 2. launcher
if (-not $SkipLauncher) {
  Step 'launcher publish: win-x64 (single file)' {
    Publish 'launcher/X4MP.Launcher' 'win-x64'
    Assert-File (Join-Path $Repo 'out\launcher-win-x64\x4mp-launcher.exe') 'out\launcher-win-x64\x4mp-launcher.exe'
  }
} else {
  Say '  launcher: reused from out\launcher-win-x64'
}
Assert-File (Join-Path $Repo 'out\launcher-win-x64\x4mp-launcher.exe') 'the launcher exe (needed by the mod zip)'

# ---------------------------------------------------------------- 3. mod (native dll, release)
if (-not $SkipMod) {
  Step 'mod build: release (x4mp.dll, with tests)' {
    Push-Location $Repo
    try {
      & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo 'mod\build.ps1') -Config release
      Expect 'mod/build.ps1'
    } finally {
      Pop-Location
    }
  }
} else {
  Say '  mod: reused from mod\build\msvc-x64-release'
}
Assert-File $ModDll 'the release x4mp.dll'

# The HTTP fallback and the capability advertisement are only in the build after a218d04; a stale
# dll would otherwise slip into a zip that looks perfectly fine.
$dllBytes = [IO.File]::ReadAllBytes($ModDll)
$needle = [Text.Encoding]::ASCII.GetBytes('X4MP-Download')
$found = $false
for ($i = 0; $i -le $dllBytes.Length - $needle.Length; $i++) {
  if ($dllBytes[$i] -eq $needle[0]) {
    $ok = $true
    for ($j = 1; $j -lt $needle.Length; $j++) { if ($dllBytes[$i + $j] -ne $needle[$j]) { $ok = $false; break } }
    if ($ok) { $found = $true; break }
  }
}
if (-not $found) { Fail 'the release dll does not mention X4MP-Download: it predates the HTTP save fallback' }
Say ('  x4mp.dll: {0:N1} MB, carries the HTTP save fallback' -f ((Get-Item -LiteralPath $ModDll).Length / 1MB))

if ($SkipPackage) {
  Say ''
  Say 'Package: skipped.'
  exit 0
}

# ---------------------------------------------------------------- 4. package the mod zip
Step ('package pkg\x4mp-mod-' + $Tag) {
  if (Test-Path -LiteralPath $Pkg) { Remove-Item -LiteralPath $Pkg -Recurse -Force }
  $ext = Join-Path $Pkg 'extensions\x4mp'
  New-Item -ItemType Directory -Force (Join-Path $ext 'native') | Out-Null
  Copy-Item -Recurse -Force (Join-Path $Repo 'mod\extension\x4mp\*') $ext
  Copy-Item -Force $ModDll (Join-Path $ext 'native\x4mp.dll')
  Copy-Item -Recurse -Force (Join-Path $Repo 'out\server-win-x64') (Join-Path $ext 'server')
  $n = Join-Path $Pkg 'extensions\x4native'
  New-Item -ItemType Directory -Force $n | Out-Null
  Copy-Item -Recurse -Force (Join-Path $X4Native 'x4native\*') $n
  Copy-Item -LiteralPath (Join-Path $X4Native 'LICENSE') -Destination $n -Force
  Copy-Item -LiteralPath (Join-Path $X4Native 'VERSION') -Destination $n -Force
  Copy-Item (Join-Path $Repo 'installer\*.bat') $Pkg
  Copy-Item -Force (Join-Path $Repo 'out\launcher-win-x64\x4mp-launcher.exe') $Pkg
  New-Item -ItemType Directory -Force (Join-Path $Pkg 'installer') | Out-Null
  foreach ($script in 'Common.ps1', 'Install.ps1', 'CheckInstall.ps1', 'CollectLogs.ps1', 'Uninstall.ps1') {
    Copy-Item -LiteralPath (Join-Path $Repo ('installer\' + $script)) -Destination (Join-Path $Pkg 'installer') -Force
  }

  # Everything a player needs must be in the package before it is zipped: the four .bat files, the
  # launcher, both extensions, the server the game starts, and the dll with the fallback in it.
  $required = @(
    'extensions\x4mp\content.xml',
    'extensions\x4mp\native\x4mp.dll',
    'extensions\x4mp\server\x4mp-server.exe',
    'extensions\x4native\VERSION',
    'extensions\x4native\native\x4native_64.dll',
    'x4mp-launcher.exe',
    'Install.bat',
    'Check Install.bat',
    'Collect Logs.bat',
    'Uninstall.bat',
    'installer\Install.ps1',
    'installer\CheckInstall.ps1',
    'installer\CollectLogs.ps1',
    'installer\Uninstall.ps1',
    'installer\Common.ps1'
  )
  foreach ($rel in $required) { Assert-File (Join-Path $Pkg $rel) ('package entry ' + $rel) }

  $files = @(Get-ChildItem -LiteralPath $Pkg -Recurse -File -Force)
  $bytes = ($files | Measure-Object -Property Length -Sum).Sum
  Say ('   {0} files, {1:N1} MB unpacked' -f $files.Count, ($bytes / 1MB))

  Compress $Pkg (Join-Path $Dist ('x4mp-mod-{0}.zip' -f $Tag))
}

# ---------------------------------------------------------------- 5. checksums
if (-not $SkipChecksums) {
  Step 'SHA256SUMS' {
    $sums = Join-Path $Dist 'SHA256SUMS'
    $lines = @()
    foreach ($file in (Get-ChildItem -LiteralPath $Dist -File | Where-Object { $_.Name -match '\.(zip|tar\.gz)$' } | Sort-Object Name)) {
      $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
      $lines += ('{0}  {1}' -f $hash, $file.Name)
    }
    if ($lines.Count -eq 0) { Fail 'no artifacts to checksum' }
    [IO.File]::WriteAllText($sums, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
    Say ('   {0} lines' -f $lines.Count)
    Say ('   ' + $sums)
  }
}

# ---------------------------------------------------------------- summary
Say ''
Say ('Done in {0:N1} min. Artifacts in dist\:' -f ((Get-Date) - $script:started).TotalMinutes)
Get-ChildItem -LiteralPath $Dist -File | Sort-Object Name | ForEach-Object {
  Say ('  {0,-46} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB))
}
Say ''
Say 'Next: powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-release.ps1'
