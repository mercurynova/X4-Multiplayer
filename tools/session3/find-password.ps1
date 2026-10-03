<#
.SYNOPSIS
  Session 3 (criterion 14): searches every file X4MP and X4 wrote for a test password and reports only HIT or NO HIT per file.
.DESCRIPTION
  The password you typed into the Join dialog (a THROWAWAY test value) must appear NOWHERE on disk. This script looks, as bytes (UTF-8,
  UTF-16 and URL-quoted forms), in:
    * Documents\Egosoft\X4\x4mp\          x4mp.json, launch.json, logs\x4mp.log (and anything else the mod wrote)
    * Documents\Egosoft\X4\<id>\          uidata.xml, config.xml, content.xml, every top-level *.log (also -logfile x4mp_s3.log), x4native\ (all)
    * <X4 install>\extensions\x4mp and x4native
    * out\session3\                       server logs, the server database (data\), FakeNode logs (save files *.xml.gz are skipped)
  It prints file paths and HIT / NO HIT only: never the password and never a matching line. Exit code 0 = no hit anywhere, 1 = at least one hit.
  Run it AFTER a test (quit X4 first so every file is flushed). -WhatIf lists what would be searched.
.PARAMETER Password  The test password. Omit it to be asked (typed hidden).
.PARAMETER ExtraPath Additional files or folders to search.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Password,
    [string]$X4Dir,
    [string]$UserId,
    [string[]]$ExtraPath = @()
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$dry = [bool]$WhatIfPreference

$X4Dir = Resolve-X4Dir $X4Dir -AllowMissing
$user = Resolve-X4UserDir $UserId -AllowMissing

# ---- what to search -------------------------------------------------------------------------------------------------
$files = New-Object System.Collections.Generic.List[string]
function Add-Tree([string]$path, [string[]]$skipExt = @('.gz', '.dll', '.exe', '.pdb')) {
    if ($path -match '[<>]' -or -not (Test-Path $path)) { Write-Host "  (not present) $path"; return }
    if ((Get-Item $path).PSIsContainer) {
        Get-ChildItem $path -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $skipExt -notcontains $_.Extension.ToLowerInvariant() } | ForEach-Object { $files.Add($_.FullName) }
    }
    else { $files.Add((Get-Item $path).FullName) }
}
Add-Tree (Get-X4MPConfigDir)
foreach ($n in 'uidata.xml', 'config.xml', 'content.xml') { Add-Tree (Join-Path $user $n) }
if (Test-Path $user) { Get-ChildItem $user -File -Filter '*.log' -ErrorAction SilentlyContinue | ForEach-Object { $files.Add($_.FullName) } }
Add-Tree (Join-Path $user 'x4native')
foreach ($n in 'x4mp', 'x4native') { Add-Tree (Join-Path (Join-Path $X4Dir 'extensions') $n) }
Add-Tree $OutDir
foreach ($e in $ExtraPath) { Add-Tree $e }
$files = @($files | Sort-Object -Unique)

Write-Host "Searching $($files.Count) file(s) (paths and verdicts only; the password is never printed)."
if ($dry) { $files | ForEach-Object { Write-Host "  would search $_" }; return }
if (-not $Password) {
    $secure = Read-Host 'Test password (hidden)' -AsSecureString
    $Password = (New-Object System.Net.NetworkCredential('', $secure)).Password
}
if ($Password.Length -lt 6) { throw 'Use a test password of at least 6 characters (a short one would match unrelated bytes).' }

$needles = New-Object System.Collections.Generic.List[byte[]]
foreach ($t in @($Password, [uri]::EscapeDataString($Password))) {
    $needles.Add([Text.Encoding]::UTF8.GetBytes($t)); $needles.Add([Text.Encoding]::Unicode.GetBytes($t))
}
$seen = @{}; $unique = New-Object System.Collections.Generic.List[byte[]]; $maxLen = 0
foreach ($n in $needles) {   # (no pipeline here: it would unroll the byte arrays)
    $key = [BitConverter]::ToString($n)
    if (-not $seen.ContainsKey($key)) { $seen[$key] = $true; $unique.Add($n); if ($n.Length -gt $maxLen) { $maxLen = $n.Length } }
}
$needles = $unique

function Test-FileForNeedles([string]$path) {
    $fs = $null
    try {
        $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
        $buf = New-Object byte[] (1MB + $maxLen)
        $carry = 0
        while ($true) {
            $n = $fs.Read($buf, $carry, 1MB)
            if ($n -le 0) { return $false }
            $total = $carry + $n
            foreach ($needle in $needles) {
                $limit = $total - $needle.Length
                $i = 0
                while ($i -le $limit) {
                    $i = [Array]::IndexOf($buf, $needle[0], $i, $limit - $i + 1)
                    if ($i -lt 0) { break }
                    $k = 1
                    while ($k -lt $needle.Length -and $buf[$i + $k] -eq $needle[$k]) { $k++ }
                    if ($k -eq $needle.Length) { return $true }
                    $i++
                }
            }
            $carry = [Math]::Min($maxLen - 1, $total)
            [Array]::Copy($buf, $total - $carry, $buf, 0, $carry)
        }
    }
    finally { if ($fs) { $fs.Dispose() } }
}

$hits = 0; $errors = 0
foreach ($f in $files) {
    try {
        if (Test-FileForNeedles $f) { Write-Host "  HIT     $f" -ForegroundColor Red; $hits++ }
        else { Write-Host "  NO HIT  $f" }
    }
    catch { Write-Host "  UNREADABLE $f ($($_.Exception.Message))" -ForegroundColor Yellow; $errors++ }
}
Write-Host ''
if ($hits -gt 0) { Write-Host "RESULT: the password was found in $hits file(s). Criterion 14 FAILS: send Claude this output and out\session3\ logs." -ForegroundColor Red; exit 1 }
Write-Host "RESULT: no hit in $($files.Count - $errors) file(s)$(if ($errors) { " ($errors unreadable)" }). Criterion 14 passes for everything searched." -ForegroundColor Green
exit 0
