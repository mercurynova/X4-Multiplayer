<#
.SYNOPSIS
  Session 4, sittings 1-3: installs the REAL x4mp extension and x4native into <X4 install>\extensions\ (nothing else is touched).
.DESCRIPTION
  Wraps mod\tools\deploy.ps1 (same files: mod\extension\x4mp + native\x4mp.dll, and the vendored X4Native), after checking that the
  throwaway test kit of sitting 0 (x4mp_probe, x4mp_spike) is gone: it REFUSES when one of them is installed or enabled (they must not run
  with the real x4mp). Run `install-spike.ps1 -Restore` first (it takes the kit out), or pass -RemoveTestExtensions to remove the two
  folders (asks first; -Force skips the question). If the product was parked by install-spike.ps1 and is still parked, the freshly built mod
  replaces it and the stale parked copy is reported (delete out\session4\parked yourself).
  After the copy it makes sure x4mp and x4native are ENABLED in your X4 user content.xml (existing entries only; a backup content.xml.x4mp-bak is
  made first). Build first: powershell -ExecutionPolicy Bypass -File mod\build.ps1. Supports -WhatIf (nothing is copied, removed or edited).
.PARAMETER X4Dir   Folder containing X4.exe (default: auto-detected via Steam).
.PARAMETER UserId  The numeric folder under Documents\Egosoft\X4 (only needed when there are several).
.PARAMETER RemoveTestExtensions  Remove x4mp_probe and x4mp_spike from <X4>\extensions\ (after asking).
.PARAMETER Force   With -RemoveTestExtensions: do not ask. Also continue while X4 is running (not recommended).
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
if (-not $Force -and (Test-X4Running)) {
    if ($dry) { Write-Warning 'X4 is running (a real run would stop here; quit X4 first).' }
    else { throw 'X4 is running. Quit X4 first (it keeps the extension files open), or pass -Force.' }
}
$state = @(Get-ForbiddenExtensionState $X4Dir $user)
$blocking = @($state | Where-Object { $_.Installed -or $_.Enabled })

foreach ($s in $state) { Write-Host ('  {0,-11} installed={1,-5} enabled={2}' -f $s.Id, $s.Installed, $s.Enabled) }
if ($blocking.Count -gt 0) {
    if (-not $RemoveTestExtensions) {
        $msg = "Refusing to install the real x4mp: $(($blocking | ForEach-Object { $_.Id }) -join ', ') is installed or enabled. The sitting-0 test kit must not run together with x4mp. " +
               "Run tools\session4\install-spike.ps1 -Restore (kit out), or run this script again with -RemoveTestExtensions (it asks first)."
        if ($dry) { Write-Warning "$msg (a real run would stop here)" } else { throw $msg }
    }
    else {
        foreach ($b in ($blocking | Where-Object { $_.Installed })) {
            $go = $Force
            if (-not $go -and -not $dry) { $go = ((Read-Host "Remove $($b.Path)? (yes/no)") -eq 'yes') }
            if (-not $go -and -not $dry) { throw "Not removed: $($b.Path). The real x4mp cannot be installed next to it." }
            if ($PSCmdlet.ShouldProcess($b.Path, "Remove $($b.Id)")) { Remove-Item -Path $b.Path -Recurse -Force; Write-Host "Removed $($b.Path)" }
        }
        foreach ($b in ($blocking | Where-Object { $_.Enabled })) {
            $r = Set-ExtensionEnabled $user $b.Id $false -Preview:$dry
            if ($r -eq 'changed') { Write-Host "  content.xml: $($b.Id) disabled" }
        }
    }
}
$parkedProduct = Join-Path $ParkedDir $ProductExtension
if (Test-Path $parkedProduct) {
    Write-Warning "A product x4mp is still parked in $parkedProduct (install-spike.ps1 put it there). This install deploys the freshly built mod instead; delete the parked copy yourself (or run install-spike.ps1 -Restore first, then this script)."
}

$deploy = Join-Path $Repo 'mod\tools\deploy.ps1'
$dArgs = @('-Config', $Config)
if ($X4Dir -and (Test-Path (Join-Path $X4Dir 'X4.exe'))) { $dArgs += @('-X4Dir', $X4Dir) }
if ($dry) {
    Write-Host "Would run: $deploy $($dArgs -join ' ')   (copies extension\x4mp + x4mp.dll and the vendored X4Native into $(Join-Path $X4Dir 'extensions'))"
    foreach ($id in 'x4native', $ProductExtension) { Write-Host "Would set $id enabled in $(Join-Path $user 'content.xml') (existing entry only; backup content.xml.x4mp-bak)" }
    if (-not (Test-Path (Join-Path $Repo "mod\build\msvc-x64-$Config\x4mp.dll"))) { Write-Warning 'x4mp.dll is not built yet (mod\build.ps1); a real run would stop here.' }
    return
}
& (Get-Process -Id $PID).Path -NoProfile -ExecutionPolicy Bypass -File $deploy @dArgs
if ($LASTEXITCODE -ne 0) { throw "deploy.ps1 failed (exit $LASTEXITCODE)" }
foreach ($id in 'x4native', $ProductExtension) {
    try { $r = Set-ExtensionEnabled $user $id $true }
    catch { Write-Warning "Could not enable $id in content.xml ($($_.Exception.Message)). Enable x4native and x4mp in X4 > Settings > Extensions."; continue }
    if ($r -eq 'changed') { Write-Host "  content.xml: $id enabled" }
    elseif ($r -eq 'unavailable') { Write-Host "  content.xml: $id NOT changed (start OneDrive, or enable x4native and x4mp in X4 > Settings > Extensions)" }
}
Write-Host ''
Write-Host 'Next: in X4 > Settings > Extensions: Protected UI Mode OFF; x4native and x4mp enabled; the sitting-0 kit (x4mp_probe, x4mp_spike) gone or disabled.'
Write-Host 'Steam launch options: -debug all -logfile x4mp_s4.log   (collect-logs.ps1 reads that file name).'
