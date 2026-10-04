<#
.SYNOPSIS
  Helper of the M3-11 scenario (avatars.hostsim), called with `exec`: starts or stops FakeNode bot players WITHOUT waiting for them, so the
  hostsim authority keeps ticking frames while they play. Never sleeps or polls.
.DESCRIPTION
  -Mode start : starts `FakeNode swarm --clients N --avatars` against the server, output to -Out, appends the process id to -PidFile.
  -Mode stop  : kills the processes listed in -PidFile.
#>
param(
    [Parameter(Mandatory)][ValidateSet('start', 'stop')][string]$Mode,
    [string]$Fake, [int]$Port, [int]$Clients = 1, [int]$Duration = 60, [string]$Out, [string]$PidFile, [string]$Prefix = 'Bot'
)
$ErrorActionPreference = 'Stop'
if ($Mode -eq 'start') {
    $a = @('swarm', '--server', "127.0.0.1:$Port", '--clients', "$Clients", '--avatars', '--duration', "$Duration", '--name-prefix', $Prefix)
    $p = Start-Process -FilePath $Fake -ArgumentList $a -PassThru -NoNewWindow -RedirectStandardOutput $Out -RedirectStandardError ($Out + '.err')
    Add-Content -Path $PidFile -Value $p.Id
    Write-Host "avatars_sim: started $Clients bot(s) (pid $($p.Id))"
}
else {
    if (Test-Path $PidFile) {
        foreach ($l in Get-Content $PidFile) { try { & taskkill /PID ([int]$l) /T /F 2>$null | Out-Null } catch { } }
        Remove-Item $PidFile -Force
    }
    Write-Host 'avatars_sim: bots stopped'
}
