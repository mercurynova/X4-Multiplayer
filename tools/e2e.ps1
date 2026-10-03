<#
.SYNOPSIS
  End-to-end run of X4MP exactly as CI does it (task M1-C1): publish the server, FakeNode swarm with invariant checks,
  the Playwright GUI suite, and (Windows only) the headless C++ mod client. One script for CI and for developers.

.DESCRIPTION
  Steps (pick with -Steps; default Publish,Swarm,Playwright plus Headless on Windows):
    Publish     dotnet publish of the single-file server (Release, PublishProfile=<rid>, -p:SkipWebBuild=true) and a
                Release build of FakeNode into out/fakenode. Fetches flatc if tools/flatc/bin is missing.
    Swarm       starts the published server on a temp data dir, does the bootstrap admin password change over the API,
                runs `fakenode swarm --with-authority --clients N --teams 3 --relations ffa --economy casual
                --dupe-attack --verify --admin-url ...` and fails on any non-zero exit (drift, duplicate effect,
                invariant failure, verify error).
    Playwright  npm ci, installs Chromium, runs `npm run e2e` in server/web against the published server exe
                (the suite's global setup starts it on its own ports and temp data dir).
    Headless    (Windows) x4mp-headless against a fresh server with a FakeNode `authority`: handshake, heartbeat,
                ClientReload + resume, in-band save download. Builds the mod first unless -SkipModBuild.
    HostSim     (Windows) x4mp-hostsim (a fake X4Native host, M2-01) loads the real x4mp.dll and runs
                mod/tests/hostsim/server_smoke.hostsim against a fresh server on ports 47950-47954: 600 frames, a
                reload with the stash kept, admin API checks, server kill + restart. Builds the mod first unless
                -SkipModBuild. Scenario language: docs/hostsim.md.

  Every step runs even if an earlier one failed; the exit code is non-zero if any step failed. Logs, the summary and
  Playwright traces end up in -ArtifactDir (default out/e2e) for upload. Ports are non-default (base 47960) so a
  developer's own server keeps running. Everything it starts is killed on exit.

.EXAMPLE
  pwsh tools/e2e.ps1                          # everything this OS can run
  powershell -NoProfile -File tools/e2e.ps1 -Steps Swarm -SkipPublish -SwarmSeconds 30
#>
[CmdletBinding()]
param(
  [ValidateSet('Publish', 'Swarm', 'Playwright', 'Headless', 'HostSim')][string[]]$Steps,
  [int]$Clients = 6,
  [int]$SwarmSeconds = 45,
  [int]$PortBase = 47960,
  [string]$ArtifactDir,
  [switch]$SkipPublish,      # reuse out/<rid> and out/fakenode from an earlier run
  [switch]$SkipModBuild,     # reuse the mod build tree
  [switch]$SkipNpmInstall,
  [int]$PlaywrightRetries = $(if ($env:CI) { 1 } else { 0 }),   # the economy specs are timing sensitive; CI gets one retry
  [string[]]$PlaywrightArgs, # extra arguments for `npm run e2e --`, e.g. economy
  [string]$HeadlessExe,      # override the x4mp-headless path
  [string]$HostSimExe,       # override the x4mp-hostsim path
  [string]$HostSimDll,       # override the x4mp.dll hostsim loads
  [string]$HostSimScript     # override the hostsim script (default mod/tests/hostsim/server_smoke.hostsim)
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$isWin = [System.Environment]::OSVersion.Platform -eq 'Win32NT'
$exeExt = if ($isWin) { '.exe' } else { '' }
$rid = if ($isWin) { 'win-x64' } else { 'linux-x64' }
if (-not $ArtifactDir) { $ArtifactDir = Join-Path $repo 'out/e2e' }
if (-not $Steps) { $Steps = @('Publish', 'Swarm', 'Playwright'); if ($isWin) { $Steps += 'Headless', 'HostSim' } }
if ($SkipPublish) { $Steps = $Steps | Where-Object { $_ -ne 'Publish' } }

$serverExe = Join-Path $repo "out/$rid/x4mp-server$exeExt"
$fakeNodeExe = Join-Path $repo "out/fakenode/X4MP.FakeNode$exeExt"
$logDir = Join-Path $ArtifactDir 'logs'
$work = Join-Path $ArtifactDir 'work'
Remove-Item -Recurse -Force $logDir, $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $logDir, $work | Out-Null

$adminPassword = 'E2e-ci-only-password-12345'
$results = New-Object System.Collections.Generic.List[object]
$started = New-Object System.Collections.Generic.List[object]

function Invoke-Native([string]$what, [scriptblock]$cmd) {
  & $cmd
  if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

function Stop-Tree($proc) {
  if ($null -eq $proc -or $proc.HasExited) { return }
  if ($isWin) { try { & taskkill /PID $proc.Id /T /F *> $null } catch { } } else { try { $proc.Kill() } catch { } }
}

function Start-Proc([string]$name, [string]$exe, [string[]]$argList, [hashtable]$envVars = @{}) {
  $old = @{}
  foreach ($k in $envVars.Keys) { $old[$k] = [System.Environment]::GetEnvironmentVariable($k); [System.Environment]::SetEnvironmentVariable($k, [string]$envVars[$k]) }
  try {
    $out = Join-Path $logDir "$name.log"
    $err = Join-Path $logDir "$name.err.log"
    # Windows PowerShell 5.1 does not quote array elements that contain spaces.
    $quoted = @($argList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
    $p = Start-Process -FilePath $exe -ArgumentList $quoted -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err
  }
  finally { foreach ($k in $old.Keys) { [System.Environment]::SetEnvironmentVariable($k, $old[$k]) } }
  $null = $p.Handle   # PS 5.1: without touching the handle, ExitCode reads as empty after exit
  $started.Add($p)
  return $p
}

function Wait-Until([string]$what, [scriptblock]$probe, [int]$timeoutSec = 30) {
  $end = (Get-Date).AddSeconds($timeoutSec)
  while ($true) {
    try { if (& $probe) { return } } catch { }
    if ((Get-Date) -gt $end) { throw "Timed out after ${timeoutSec}s waiting for $what" }
    Start-Sleep -Milliseconds 250
  }
}

# Starts the published server on a fresh data dir and ports tcp/udp/http; returns @{ Proc; Data; Http; Tcp }.
function Start-Server([string]$name, [int]$slot, [string[]]$extraArgs = @(), [hashtable]$moreEnv = @{}, [int[]]$ports = @()) {
  $tcp = $PortBase + 2 * $slot; $udp = $tcp + 1; $http = $PortBase + 10 + $slot
  if ($ports.Count -eq 3) { $tcp, $udp, $http = $ports }   # explicit tcp, udp, http (the HostSim step uses 47950-47952)
  $data = Join-Path $work "$name-data"
  New-Item -ItemType Directory -Force $data | Out-Null
  $envVars = @{
    X4MP__Net__MaxConnectionsPerIp = '64'
    X4MP__Net__MaxPlayers          = '16'
    X4MP__Net__NodeTcpEndpoint     = "0.0.0.0:$tcp"
    X4MP__Net__UdpPort             = "$udp"
  }
  foreach ($k in $moreEnv.Keys) { $envVars[$k] = $moreEnv[$k] }
  $serverArgs = @('--data-dir', $data, '--port', "$http") + $extraArgs
  $proc = Start-Proc "$name-server" $serverExe $serverArgs $envVars
  $url = "http://127.0.0.1:$http"
  Wait-Until "the server to answer /healthz" {
    if ($proc.HasExited) { throw "server exited with $($proc.ExitCode); see $logDir/$name-server.err.log" }
    (Invoke-WebRequest -UseBasicParsing "$url/healthz" -TimeoutSec 3).StatusCode -eq 200
  } 40
  return @{ Proc = $proc; Data = $data; Http = $http; Url = $url; Tcp = $tcp; Args = $serverArgs; Env = $envVars }
}

# Bootstrap admin: sign in with initial-admin-password.txt, do the forced password change, sign in again.
function Set-AdminPassword($srv) {
  $file = Join-Path $srv.Data 'initial-admin-password.txt'
  Wait-Until 'initial-admin-password.txt' { (Test-Path $file) -and (Get-Content $file -Raw).Trim() } 20
  $initial = (Get-Content $file -Raw).Trim()
  $h = @{ 'X-X4MP' = '1' }
  $post = { param($path, $body, $session) Invoke-RestMethod -Method Post -Uri "$($srv.Url)$path" -Headers $h -WebSession $session -ContentType 'application/json' -Body ($body | ConvertTo-Json) }
  $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
  $null = & $post '/api/v1/auth/login' @{ username = 'admin'; password = $initial } $s
  $null = & $post '/api/v1/auth/change-password' @{ current = $initial; new = $adminPassword } $s
  $s2 = New-Object Microsoft.PowerShell.Commands.WebRequestSession
  $null = & $post '/api/v1/auth/login' @{ username = 'admin'; password = $adminPassword } $s2
}

function Invoke-Step([string]$name, [scriptblock]$body) {
  Write-Host ""
  Write-Host "=== $name ===" -ForegroundColor Cyan
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $ok = $true; $msg = ''
  try { & $body } catch { $ok = $false; $msg = $_.Exception.Message; Write-Host "FAILED: $msg" -ForegroundColor Red }
  finally {
    foreach ($p in @($started.ToArray())) { Stop-Tree $p }
    $started.Clear()
  }
  $sw.Stop()
  $results.Add([pscustomobject]@{ Step = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Detail = $msg })
}

# ---------------------------------------------------------------------------------------------------------------
if ($Steps -contains 'Publish') {
  Invoke-Step 'Publish server + FakeNode' {
    if (-not (Test-Path (Join-Path $repo "tools/flatc/bin/flatc$exeExt"))) {
      if ($isWin) { Invoke-Native 'fetch-flatc' { & powershell -NoProfile -File (Join-Path $repo 'tools/flatc/fetch-flatc.ps1') } }
      else { Invoke-Native 'fetch-flatc' { & bash (Join-Path $repo 'tools/flatc/fetch-flatc.sh') } }
    }
    Invoke-Native 'dotnet publish' { dotnet publish (Join-Path $repo 'server/src/X4MP.Server') -c Release "-p:PublishProfile=$rid" -p:SkipWebBuild=true -nologo -v:m }
    Invoke-Native 'dotnet build FakeNode' { dotnet build (Join-Path $repo 'tools/X4MP.FakeNode') -c Release -o (Join-Path $repo 'out/fakenode') -nologo -v:m }
  }
}

foreach ($f in @($serverExe, $fakeNodeExe)) {
  if (($Steps -contains 'Swarm' -or $Steps -contains 'Headless' -or $Steps -contains 'Playwright') -and -not (Test-Path $f)) {
    throw "$f not found: run without -SkipPublish first."
  }
}

if ($Steps -contains 'Swarm') {
  Invoke-Step "Swarm ($Clients clients, 3 teams, ffa, economy casual, dupe-attack, ${SwarmSeconds}s)" {
    # The server settings the layout needs (what `fakenode swarm --relations ffa` prints as server-settings).
    $srv = Start-Server 'swarm' 0 @('--X4MP:Teams:JoinMode=Lobby', '--X4MP:Teams:AllowCreateInLobby=true', '--X4MP:Teams:AutoAssign=Balance', '--X4MP:Teams:DefaultRelation=Hostile')
    Set-AdminPassword $srv
    $swarmArgs = @('swarm', '--server', "127.0.0.1:$($srv.Tcp)", '--with-authority', '--clients', "$Clients", '--teams', '3', '--relations', 'ffa',
      '--economy', 'casual', '--dupe-attack', '--verify', '--duration', "$SwarmSeconds",
      '--admin-url', $srv.Url, '--admin-user', 'admin', '--admin-password', $adminPassword)
    $p = Start-Proc 'swarm-fakenode' $fakeNodeExe $swarmArgs
    if (-not $p.WaitForExit(($SwarmSeconds + 90) * 1000)) { Stop-Tree $p; throw "swarm did not finish within $($SwarmSeconds + 90) s" }
    $p.WaitForExit()
    Get-Content (Join-Path $logDir 'swarm-fakenode.log') -Tail 25 | ForEach-Object { Write-Host "  $_" }
    if ($p.ExitCode -ne 0) { throw "fakenode swarm exited with $($p.ExitCode) (see swarm-fakenode.log)" }
    if ($srv.Proc.HasExited) { throw "the server died during the swarm (exit $($srv.Proc.ExitCode))" }
  }
}

if ($Steps -contains 'Playwright') {
  Invoke-Step 'Playwright (chromium)' {
    $web = Join-Path $repo 'server/web'
    Push-Location $web
    try {
      if (-not $SkipNpmInstall) { Invoke-Native 'npm ci' { npm ci --no-audit --no-fund } }
      $install = @('playwright', 'install'); if (-not $isWin -and $env:CI) { $install += '--with-deps' }
      Invoke-Native 'playwright install' { npx @install chromium }
      $env:X4MP_E2E_SERVER_EXE = $serverExe
      $env:X4MP_E2E_FAKENODE_EXE = $fakeNodeExe
      if (-not $env:X4MP_E2E_TCP_PORT) { $env:X4MP_E2E_TCP_PORT = "$($PortBase + 4)" }
      if (-not $env:X4MP_E2E_UDP_PORT) { $env:X4MP_E2E_UDP_PORT = "$($PortBase + 5)" }
      if (-not $env:X4MP_E2E_HTTP_PORT) { $env:X4MP_E2E_HTTP_PORT = "$($PortBase + 12)" }
      if (-not $env:X4MP_E2E_WEB_PORT) { $env:X4MP_E2E_WEB_PORT = '5274' }
      $env:X4MP_E2E_DATA_DIR = Join-Path $work 'playwright-data'
      Remove-Item -Recurse -Force (Join-Path $web 'test-results') -ErrorAction SilentlyContinue
      try { Invoke-Native 'npm run e2e' { npm run e2e -- "--retries=$PlaywrightRetries" @PlaywrightArgs | Tee-Object -FilePath (Join-Path $logDir 'playwright.log') } }
      finally {
        $tr = Join-Path $web 'test-results'
        if (Test-Path $tr) { Copy-Item -Recurse -Force $tr (Join-Path $ArtifactDir 'playwright-test-results') }
        $sl = Join-Path $env:X4MP_E2E_DATA_DIR 'server-stdout.log'
        if (Test-Path $sl) { Copy-Item -Force $sl (Join-Path $logDir 'playwright-server.log') }
      }
    }
    finally { Pop-Location }
  }
}

if ($Steps -contains 'Headless') {
  Invoke-Step 'Headless mod client (handshake, heartbeat, resume, save download)' {
    if (-not $isWin) { throw 'the headless client is Windows-only' }
    $headless = $HeadlessExe
    if (-not $headless) {
      $buildTree = Join-Path $repo 'mod/build/msvc-x64-relwithdebinfo'
      if (-not $SkipModBuild) { Invoke-Native 'mod build' { & powershell -NoProfile -File (Join-Path $repo 'mod/build.ps1') -NoTest } }
      $headless = (Get-ChildItem -Path $buildTree -Recurse -Filter 'x4mp-headless.exe' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
    }
    if (-not $headless -or -not (Test-Path $headless)) { throw 'x4mp-headless.exe not found (build the mod first)' }
    # The headless client reports mod build 'dev' while FakeNode's authority reports 'fakenode': relax the strict build compare.
    $srv = Start-Server 'headless' 1 @() @{ X4MP__Net__ModBuildStrict = 'false' }
    # A fake authority uploads the first checkpoint (starts the session) and answers the save request.
    $auth = Start-Proc 'headless-authority' $fakeNodeExe @('authority', '--server', "127.0.0.1:$($srv.Tcp)", '--duration', '90', '--sectors', '20', '--ships', '200')
    Wait-Until 'the fake authority to be in game' {
      if ($auth.HasExited) { throw "authority exited with $($auth.ExitCode)" }
      (Get-Content (Join-Path $logDir 'headless-authority.log') -Raw) -match '(?i)in game'
    } 40
    $saveDir = Join-Path $work 'headless-saves'
    New-Item -ItemType Directory -Force $saveDir | Out-Null
    $hArgs = @('--host', '127.0.0.1', '--port', "$($srv.Tcp)", '--name', 'HeadlessCI', '--key-file', (Join-Path $work 'headless.key'),
      '--save-dir', $saveDir, '--save-wait', '30', '--ping', '4', '--reload', '--timeout', '90')
    $p = Start-Proc 'headless-client' $headless $hArgs
    if (-not $p.WaitForExit(120000)) { Stop-Tree $p; throw 'x4mp-headless did not finish within 120 s' }
    $p.WaitForExit()
    $text = Get-Content (Join-Path $logDir 'headless-client.log') -Raw
    Write-Host $text
    if ($p.ExitCode -ne 0) { throw "x4mp-headless exited with $($p.ExitCode)" }
    if ($text -notmatch 'SaveInfo') { throw 'the server offered no save (no SaveInfo in the output)' }
    if ($text -notmatch 'save: verified') { throw 'the save download was not verified' }
  }
}

if ($Steps -contains 'HostSim') {
  Invoke-Step 'HostSim (real x4mp.dll in a fake X4Native host, real server)' {
    if (-not $isWin) { throw 'x4mp-hostsim is Windows-only' }
    $buildTree = Join-Path $repo 'mod/build/msvc-x64-relwithdebinfo'
    if (-not $HostSimExe -or -not $HostSimDll) {
      if (-not $SkipModBuild) { Invoke-Native 'mod build' { & powershell -NoProfile -File (Join-Path $repo 'mod/build.ps1') -NoTest } }
    }
    $hostSim = $HostSimExe
    if (-not $hostSim) { $hostSim = (Get-ChildItem -Path $buildTree -Recurse -Filter 'x4mp-hostsim.exe' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
    $dll = $HostSimDll
    if (-not $dll) { $dll = (Get-ChildItem -Path $buildTree -Recurse -Filter 'x4mp.dll' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
    if (-not $hostSim -or -not (Test-Path $hostSim)) { throw 'x4mp-hostsim.exe not found (build the mod first)' }
    if (-not $dll -or -not (Test-Path $dll)) { throw 'x4mp.dll not found (build the mod first)' }
    $script = $HostSimScript
    if (-not $script) { $script = Join-Path $repo 'mod/tests/hostsim/server_smoke.hostsim' }
    # Ports 47950-47954: tcp 47950, udp 47951, http 47952 (47953/47954 are reserved for the scenarios M2-06+ add).
    $srv = Start-Server 'hostsim' 0 @() @{ X4MP__Net__ModBuildStrict = 'false' } @(47950, 47951, 47952)
    Set-AdminPassword $srv
    $hsArgs = @('--dll', $dll, '--script', $script, '--work-dir', (Join-Path $work 'hostsim-work'),
      '--admin-url', $srv.Url, '--admin-user', 'admin', '--admin-password', $adminPassword,
      '--server-pid', "$($srv.Proc.Id)", '--server-exe', $serverExe, '--server-log', (Join-Path $logDir 'hostsim-server-restarted.log'),
      '--stash-dump', (Join-Path $logDir 'hostsim-stash.json'), '--max-seconds', '100')
    foreach ($a in $srv.Args) { $hsArgs += @('--server-arg', $a) }
    foreach ($k in $srv.Env.Keys) { $hsArgs += @('--server-env', "$k=$($srv.Env[$k])") }
    $p = Start-Proc 'hostsim' $hostSim $hsArgs
    if (-not $p.WaitForExit(110000)) { Stop-Tree $p; throw 'x4mp-hostsim did not finish within 110 s' }
    $p.WaitForExit()
    Get-Content (Join-Path $logDir 'hostsim.log') -Tail 40 | ForEach-Object { Write-Host "  $_" }
    if ($p.ExitCode -ne 0) { throw "x4mp-hostsim exited with $($p.ExitCode) (see hostsim.log)" }
  }
}

# ---- summary --------------------------------------------------------------------------------------------------
$total = ($results | Measure-Object -Property Seconds -Sum).Sum
$lines = @('| Step | Result | Seconds |', '|---|---|---|')
foreach ($r in $results) { $lines += "| $($r.Step) | $($r.Result) | $($r.Seconds) |" }
$lines += "| **Total** | | **$([math]::Round($total, 1))** |"
$lines | Set-Content -Encoding UTF8 (Join-Path $ArtifactDir 'summary.md')
Write-Host ''
Write-Host ($lines -join [Environment]::NewLine)
if ($env:GITHUB_STEP_SUMMARY) { "### E2E ($rid)`n" + ($lines -join "`n") | Add-Content -Encoding UTF8 $env:GITHUB_STEP_SUMMARY }
if (@($results | Where-Object { $_.Result -eq 'FAIL' }).Count -gt 0) { exit 1 }
exit 0
