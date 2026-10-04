<#
.SYNOPSIS
  Session 4, sitting 3: builds the CLIENT KIT zip for PC B (the second PC): the built mod, X4Native, and the install / uninstall / log scripts.
  PC B needs NO build tools, NO repo clone, NO .NET SDK: only X4 (same build 611726, same DLCs) and Windows PowerShell.
.DESCRIPTION
  Run on PC A after `powershell -ExecutionPolicy Bypass -File mod\build.ps1`. The zip mirrors the repo layout for exactly the files the kit scripts
  need, so the SAME scripts run on PC B unchanged:
    tools\session2\common.ps1  tools\session3\common.ps1  tools\session4\{common,install,uninstall,collect-logs,sync-report}.ps1
    mod\tools\{deploy,check-packaging}.ps1   mod\extension\x4mp\**   mod\third_party\x4native\<tag>\**   mod\build\msvc-x64-<config>\x4mp.dll
    docs\in-game-session-4.md   START-HERE-PC-B.txt (the steps for PC B)
  It contains no secrets (no passwords, no server data, no saves, no launch.json, no pdb) and no personal paths in any script or text file (the script
  scans for the repo folder, the user profile and the Documents folder paths and refuses to write the zip when it finds one). The x4mp.dll itself carries the build folder in its
  debug-info path string: the zip is for your own PCs only, never publish it.
  On PC B (unzip anywhere, e.g. a folder on the Desktop; open PowerShell in the unzipped folder):
    powershell -ExecutionPolicy Bypass -File tools\session4\install.ps1        (quit X4 first; add -WhatIf to look first)
    ... play ...
    powershell -ExecutionPolicy Bypass -File tools\session4\collect-logs.ps1 -Label s3b      (after EVERY X4 quit)
    powershell -ExecutionPolicy Bypass -File tools\session4\sync-report.ps1
    powershell -ExecutionPolicy Bypass -File tools\session4\uninstall.ps1      (at the very end)
  Supports -WhatIf (lists what would go into the zip; writes nothing).
.PARAMETER Out     The zip path (default out\session4\x4mp-client-kit-<yyyyMMdd-HHmmss>.zip).
.PARAMETER Config  Which native build to pack: relwithdebinfo (default), release or debug.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Out,
    [ValidateSet('debug', 'release', 'relwithdebinfo')][string]$Config = 'relwithdebinfo'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$tag = 'v9.0.0-611726'
$dll = Join-Path $Repo "mod\build\msvc-x64-$Config\x4mp.dll"
$dry = [bool]$WhatIfPreference
if (-not (Test-Path $dll)) {
    $msg = "x4mp.dll not found at $dll. Run: powershell -ExecutionPolicy Bypass -File mod\build.ps1"
    if ($dry) { Write-Warning "$msg (a real run would stop here)" } else { throw $msg }
}
if (-not $Out) { $Out = Join-Path $OutDir ('x4mp-client-kit-{0}.zip' -f (Get-Date -Format 'yyyyMMdd-HHmmss')) }

# what goes in: source (relative to the repo) -> same relative path in the zip
$files = @(
    'tools\session2\common.ps1', 'tools\session3\common.ps1',
    'tools\session4\common.ps1', 'tools\session4\install.ps1', 'tools\session4\uninstall.ps1', 'tools\session4\collect-logs.ps1', 'tools\session4\sync-report.ps1',
    'mod\tools\deploy.ps1', 'mod\tools\check-packaging.ps1', 'docs\in-game-session-4.md')
$trees = @('mod\extension\x4mp', "mod\third_party\x4native\$tag")
Write-Host "Kit zip   : $Out"
Write-Host "Native    : $dll"
foreach ($f in $files) { Write-Host "  + $f" }
foreach ($t in $trees) { Write-Host "  + $t\**" }
Write-Host "  + mod\build\msvc-x64-$Config\x4mp.dll"
Write-Host '  + START-HERE-PC-B.txt (generated)'
if (-not $PSCmdlet.ShouldProcess($Out, 'Build the client kit zip')) { return }

$startHere = @"
X4MP client kit for the second PC (session 4, sitting 3)
=========================================================
This folder is a self-contained copy of what PC B needs. No build tools, no .NET SDK, no repo.

Before you start (once)
 1. Same X4 build as PC A (9.00, build 611726) and the SAME DLCs enabled. A DLC difference refuses the join.
 2. Back up Documents\Egosoft\X4\<id>\save\ (Steam Cloud syncs it and restores deleted files).
 3. Steam launch options for X4: -debug all -logfile x4mp_s4.log      (and switch Steam auto-update for X4 off).
 4. In X4: Settings > Controls: bind "Toggle Chat Window".
 5. Open PowerShell IN THIS FOLDER (Shift + right click > Open PowerShell window here).

Install (X4 must be closed)
    powershell -ExecutionPolicy Bypass -File tools\session4\install.ps1 -WhatIf     (look first)
    powershell -ExecutionPolicy Bypass -File tools\session4\install.ps1
 Then in X4: Settings > Extensions: Protected UI Mode OFF; x4native and x4mp enabled.

Play (see docs\in-game-session-4.md, Sitting 3)
 Join address: <PC A address>:47780 (PC A's script prints it), name and password as PC A tells you.

After EVERY X4 quit, before you start X4 again (X4 overwrites its log at the next start)
    powershell -ExecutionPolicy Bypass -File tools\session4\collect-logs.ps1 -Label s3b
    powershell -ExecutionPolicy Bypass -File tools\session4\sync-report.ps1
 The zip lands in out\session4\ (it never contains saves, passwords or launch.json). Send it to PC A / Claude.

At the very end
    powershell -ExecutionPolicy Bypass -File tools\session4\uninstall.ps1
 Then remove the launch option -logfile x4mp_s4.log from Steam.
"@

$stage = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-kit-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    New-Item -ItemType Directory -Force $stage | Out-Null
    foreach ($f in $files) {
        $dest = Join-Path $stage $f
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item (Join-Path $Repo $f) $dest
    }
    foreach ($t in $trees) {
        $dest = Join-Path $stage $t
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item (Join-Path $Repo $t) $dest -Recurse
    }
    Get-ChildItem $stage -Recurse -File -Filter '.gitkeep' | Remove-Item -Force
    $destDll = Join-Path $stage "mod\build\msvc-x64-$Config\x4mp.dll"
    New-Item -ItemType Directory -Force (Split-Path $destDll) | Out-Null
    Copy-Item $dll $destDll
    [IO.File]::WriteAllText((Join-Path $stage 'START-HERE-PC-B.txt'), $startHere.Replace("`r`n", "`n").Replace("`n", "`r`n"))

    # guards: nothing private in the kit
    $textExt = '.ps1', '.txt', '.md', '.xml', '.lua', '.json', '.cmd', '.h'
    $needles = @($Repo, $env:USERPROFILE, [Environment]::GetFolderPath('MyDocuments')) | Where-Object { $_ -and $_.Length -ge 6 } | Select-Object -Unique
    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($f in Get-ChildItem $stage -Recurse -File) {
        if ($f.Name -match 'password|launch\.json|\.pdb$|\.db$|\.sqlite$|\.xml\.gz$' ) { $problems.Add("forbidden file: $($f.FullName.Substring($stage.Length))"); continue }
        if ($textExt -contains $f.Extension.ToLowerInvariant()) {
            $text = [IO.File]::ReadAllText($f.FullName)
            foreach ($n in $needles) { if ($text.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $problems.Add("personal path or name in $($f.FullName.Substring($stage.Length)): '$n'") } }
        }
    }
    if ($problems.Count -gt 0) { throw ("The kit would contain private data; not written:`n  " + ($problems -join "`n  ")) }

    New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
    if (Test-Path $Out) { Remove-Item $Out -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $Out -Force
}
finally { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$za = [IO.Compression.ZipFile]::OpenRead($Out)
try { $count = $za.Entries.Count } finally { $za.Dispose() }
$sha256 = [Security.Cryptography.SHA256]::Create()
try { $sha = ([BitConverter]::ToString($sha256.ComputeHash([IO.File]::ReadAllBytes($Out))) -replace '-', '').ToLowerInvariant() } finally { $sha256.Dispose() }
Write-Host ('Created {0}: {1} files, {2:N1} MB, sha256 {3}' -f $Out, $count, ((Get-Item $Out).Length / 1MB), $sha.Substring(0, 16))
Write-Host 'Copy it to PC B (USB stick or a shared folder), unzip, and follow START-HERE-PC-B.txt. The zip is for your own PCs only.'
