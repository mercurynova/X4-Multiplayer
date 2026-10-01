<#
.SYNOPSIS
  Downloads the pinned flatc (see flatc.lock.json) into tools/flatc/bin/ and verifies SHA-256.
  Fails (exit 1) on a checksum mismatch. Idempotent: skips if the right version is installed.
.EXAMPLE
  pwsh tools/flatc/fetch-flatc.ps1            # or: powershell -File tools/flatc/fetch-flatc.ps1
#>
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$lock = Get-Content (Join-Path $here 'flatc.lock.json') -Raw | ConvertFrom-Json
$isWin = ($env:OS -eq 'Windows_NT')
$plat = if ($isWin) { $lock.platforms.windows } else { $lock.platforms.linux }
$bin = Join-Path $here 'bin'
$exe = Join-Path $bin $plat.executable
$stamp = Join-Path $bin 'flatc.version'

if (-not $Force -and (Test-Path $exe) -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq "$($lock.version) $($plat.sha256)")) {
    Write-Host "flatc $($lock.version) already present: $exe"
    exit 0
}

New-Item -ItemType Directory -Force $bin | Out-Null
$zip = Join-Path $bin $plat.asset
$url = "$($lock.baseUrl)/$($plat.asset)"
Write-Host "Downloading $url"
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

$actual = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
if ($actual -ne $plat.sha256.ToLowerInvariant()) {
    Remove-Item $zip -Force
    Write-Error "SHA-256 mismatch for $($plat.asset): expected $($plat.sha256), got $actual"
    exit 1
}

Expand-Archive -Path $zip -DestinationPath $bin -Force
Remove-Item $zip -Force
if (-not (Test-Path $exe)) { Write-Error "Archive did not contain $($plat.executable)"; exit 1 }
if (-not $isWin) { & chmod +x $exe }
Set-Content -Path $stamp -Value "$($lock.version) $($plat.sha256)" -Encoding ascii
& $exe --version
