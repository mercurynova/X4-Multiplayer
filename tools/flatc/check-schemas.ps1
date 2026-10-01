<#
.SYNOPSIS
  Compiles every protocol/schema/*.fbs with the pinned flatc (--cpp --csharp) into a temp dir.
  Exit code is flatc's. Run fetch-flatc.ps1 first.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$flatc = Join-Path $PSScriptRoot 'bin/flatc.exe'
if (-not (Test-Path $flatc)) { $flatc = Join-Path $PSScriptRoot 'bin/flatc' }
if (-not (Test-Path $flatc)) { Write-Error 'flatc not found: run tools/flatc/fetch-flatc.ps1 (or .sh) first'; exit 1 }
$out = Join-Path ([IO.Path]::GetTempPath()) ("x4mp-flatc-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $out | Out-Null
try {
    $schemas = Get-ChildItem (Join-Path $root 'protocol/schema') -Filter *.fbs | ForEach-Object { $_.FullName }
    & $flatc --cpp --csharp -o $out -I (Join-Path $root 'protocol/schema') @schemas
    if ($LASTEXITCODE -ne 0) { Write-Error "flatc failed ($LASTEXITCODE)"; exit $LASTEXITCODE }
    Write-Host "OK: $($schemas.Count) schemas compiled with --cpp --csharp"
} finally {
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
}
