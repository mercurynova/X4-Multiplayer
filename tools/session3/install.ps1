<#
.SYNOPSIS
  Session 3: installs the REAL x4mp extension and x4native into <X4 install>\extensions\ (nothing else is touched).
.DESCRIPTION
  Wraps mod\tools\deploy.ps1 (same files: mod\extension\x4mp + native\x4mp.dll, and the vendored X4Native), after checking that
  the session-1/2 test extensions are gone: it REFUSES when x4mp_probe or x4mp_spike is installed or enabled (they must not run
  with the real x4mp). With -RemoveTestExtensions it offers to remove those two folders (asks first; -Force skips the question).
  Build first: powershell -ExecutionPolicy Bypass -File mod\build.ps1. Supports -WhatIf (nothing is copied or removed).
.PARAMETER X4Dir   Folder containing X4.exe (default: auto-detected via Steam).
.PARAMETER UserId  The numeric folder under Documents\Egosoft\X4 (only needed when there are several).
.PARAMETER RemoveTestExtensions  Remove x4mp_probe and x4mp_spike from <X4>\extensions\ (after asking).
.PARAMETER Force   With -RemoveTestExtensions: do not ask.
.PARAMETER Config  Which native build to deploy: relwithdebinfo (default), release or debug.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$X4Dir,
    [string]$UserId,
    [switch]$RemoveTestExtensions,
    [switch]$Force,
    [ValidateSet('debug', 'release', 'relwithdebinfo')][string]$Config = 'relwithdebinfo'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$dry = [bool]$WhatIfPreference
$X4Dir = Resolve-X4Dir $X4Dir -AllowMissing:$dry
$user = Resolve-X4UserDir $UserId -AllowMissing:$dry
$state = @(Get-ForbiddenExtensionState $X4Dir $user)
$blocking = @($state | Where-Object { $_.Installed -or $_.Enabled })

foreach ($s in $state) { Write-Host ('  {0,-11} installed={1,-5} enabled={2}' -f $s.Id, $s.Installed, $s.Enabled) }
if ($blocking.Count -gt 0) {
    if (-not $RemoveTestExtensions) {
        $msg = "Refusing to install the real x4mp: $(($blocking | ForEach-Object { $_.Id }) -join ', ') is installed or enabled. The session-2 test extensions must not run together with x4mp. " +
               "Run this script again with -RemoveTestExtensions to remove them (it asks first), or run tools\session2\uninstall.ps1."
        if ($dry) { Write-Warning "$msg (a real run would stop here)" } else { throw $msg }
    }
    else {
        foreach ($b in ($blocking | Where-Object { $_.Installed })) {
            $go = $Force
            if (-not $go -and -not $dry) { $go = ((Read-Host "Remove $($b.Path)? (yes/no)") -eq 'yes') }
            if (-not $go -and -not $dry) { throw "Not removed: $($b.Path). The real x4mp cannot be installed next to it." }
            if ($PSCmdlet.ShouldProcess($b.Path, "Remove $($b.Id)")) { Remove-Item -Path $b.Path -Recurse -Force; Write-Host "Removed $($b.Path)" }
        }
        # An extension that is only ENABLED in content.xml (folder already gone) is harmless; one that is installed elsewhere (Workshop) is not ours to delete.
        $still = @(Get-ForbiddenExtensionState $X4Dir $user | Where-Object { $_.Enabled -and -not $dry })
        if ($still.Count -gt 0) { Write-Warning "$(($still | ForEach-Object { $_.Id }) -join ', ') is still marked enabled in X4's content.xml. After the folders are gone X4 ignores it; in X4 > Settings > Extensions make sure it is not listed as enabled." }
    }
}

$deploy = Join-Path $Repo 'mod\tools\deploy.ps1'
$dArgs = @('-Config', $Config)
if ($X4Dir -and (Test-Path (Join-Path $X4Dir 'X4.exe'))) { $dArgs += @('-X4Dir', $X4Dir) }
if ($dry) {
    Write-Host "Would run: $deploy $($dArgs -join ' ')   (copies extension\x4mp + x4mp.dll and the vendored X4Native into $(Join-Path $X4Dir 'extensions'))"
    if (-not (Test-Path (Join-Path $Repo "mod\build\msvc-x64-$Config\x4mp.dll"))) { Write-Warning 'x4mp.dll is not built yet (mod\build.ps1); a real run would stop here.' }
    return
}
& powershell -NoProfile -ExecutionPolicy Bypass -File $deploy @dArgs
if ($LASTEXITCODE -ne 0) { throw "deploy.ps1 failed (exit $LASTEXITCODE)" }
Write-Host ''
Write-Host 'Next: in X4 > Settings > Extensions: Protected UI Mode OFF; x4native and x4mp enabled; the X4MP test extensions (x4mp_probe, x4mp_spike) gone or disabled.'
