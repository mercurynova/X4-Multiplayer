<#
.SYNOPSIS
  Runs the Lua unit tests of the X4MP extension (mod/tests/lua). Needs LuaJIT or Lua 5.1 on PATH.
.DESCRIPTION
  Without an interpreter the script says so and exits 0 (SKIPPED), because a stock Windows dev machine has none.
  Pass -Require to make that an error (exit 2). Set $env:LUA (a path) to override the search.
  Install options: scoop install luajit, or choco install lua51, or download LuaJIT/Lua 5.1 binaries.
#>
param([switch]$Require)
$ErrorActionPreference = 'Stop'

$interp = $env:LUA
if (-not $interp) {
    foreach ($c in 'luajit', 'lua5.1', 'lua51', 'lua') {
        $cmd = Get-Command $c -ErrorAction SilentlyContinue
        if ($cmd) { $interp = $cmd.Source; break }
    }
}

if (-not $interp) {
    Write-Warning 'Lua unit tests: no Lua interpreter found (luajit, lua5.1, lua). Install LuaJIT or Lua 5.1 to run them (e.g. scoop install luajit).'
    if ($Require) { exit 2 }
    Write-Host 'SKIPPED'
    exit 0
}

Write-Host "Lua unit tests: using $interp"
& $interp (Join-Path $PSScriptRoot 'run_all.lua')
exit $LASTEXITCODE
