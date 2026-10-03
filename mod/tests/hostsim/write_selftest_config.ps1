# Writes {"selftest": true} to the x4mp.json path given as the first argument (used by selftest.hostsim through `exec`).
param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = 'Stop'
[System.IO.File]::WriteAllText($Path, '{"selftest": true}')
