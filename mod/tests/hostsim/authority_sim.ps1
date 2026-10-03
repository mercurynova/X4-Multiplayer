<#
.SYNOPSIS
  Helper of the M2-09 hostsim scenarios (called with `exec` between frame blocks; never sleeps or polls).
.DESCRIPTION
  -Mode save : plays the part of X4 writing the checkpoint save. Finds the newest "SaveGame requested as x4mp_ckpt_<16 hex>" line in the
               mod log and writes -Src to <SaveDir>\<name>.xml.gz in two pieces (a growing file, like the game's slow write).
  -Mode post : POST to the admin API (login with -Url/-User/-Password), for example /api/v1/sessions/<id>/request-save ("Save now").
#>
param(
    [Parameter(Mandatory)][ValidateSet('save', 'post')][string]$Mode,
    [string]$Log, [string]$SaveDir, [string]$Src,
    [string]$Url, [string]$User = 'admin', [string]$Password, [string]$Path
)
$ErrorActionPreference = 'Stop'
if ($Mode -eq 'save') {
    $m = Select-String -Path $Log -Pattern 'SaveGame requested as (x4mp_ckpt_[0-9a-f]{16})' | Select-Object -Last 1
    if (-not $m) { throw "no 'SaveGame requested' line in $Log" }
    $name = $m.Matches[0].Groups[1].Value
    New-Item -ItemType Directory -Force $SaveDir | Out-Null
    $bytes = [IO.File]::ReadAllBytes($Src)
    $target = Join-Path $SaveDir "$name.xml.gz"
    $fs = [IO.File]::Open($target, 'Create', 'Write', 'Read')
    try {
        $half = [int]($bytes.Length / 2)
        $fs.Write($bytes, 0, $half); $fs.Flush()
        $fs.Write($bytes, $half, $bytes.Length - $half); $fs.Flush()
    } finally { $fs.Dispose() }
    Write-Host "authority_sim: wrote $target ($($bytes.Length) bytes)"
}
else {
    $h = @{ 'X-X4MP' = '1' }
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $body = @{ username = $User; password = $Password } | ConvertTo-Json
    $null = Invoke-RestMethod -Method Post -Uri "$Url/api/v1/auth/login" -Headers $h -WebSession $s -ContentType 'application/json' -Body $body
    if ($Path -like '*{current}*') {
        $cur = Invoke-RestMethod -Uri "$Url/api/v1/sessions/current" -Headers $h -WebSession $s
        $Path = $Path.Replace('{current}', [string]$cur.id)
    }
    $r = Invoke-RestMethod -Method Post -Uri "$Url$Path" -Headers $h -WebSession $s -ContentType 'application/json' -Body '{}'
    Write-Host "authority_sim: POST $Path -> $($r | ConvertTo-Json -Compress -Depth 3)"
}
