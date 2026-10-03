# Shared helpers for the session-3 kit (dot-sourced by the other scripts). Builds on the session-2 helpers (X4 install and
# Documents discovery, no hard-coded user paths) and overrides what differs: output folder, extension names, ports.

. (Join-Path $PSScriptRoot '..\session2\common.ps1')

# X4MP_S3_OUT_DIR: test-only override of out\session3 (dry runs keep their files in a temp folder).
$script:OutDir = if ($env:X4MP_S3_OUT_DIR) { $env:X4MP_S3_OUT_DIR } else { Join-Path $script:Repo 'out\session3' }
$script:ExtensionNames = @('x4native', 'x4mp')                 # what install.ps1 puts into <X4>\extensions\
$script:ForbiddenExtensions = @('x4mp_probe', 'x4mp_spike')   # the session-1/2 test extensions: must not run with the real mod
$script:Admin = @{}                                           # filled by Initialize-AdminSession

function Get-X4MPConfigDir { return (Join-Path (Get-X4DocsRoot) 'x4mp') }   # x4mp.json, launch.json, logs\x4mp.log live here

# Which forbidden extensions are installed (folder exists) or enabled (content.xml of the X4 user folder says enabled="true").
function Get-ForbiddenExtensionState([string]$X4Dir, [string]$UserDir) {
    $found = New-Object System.Collections.Generic.List[object]
    $enabled = @{}
    $contentXml = Join-Path $UserDir 'content.xml'
    if (Test-Path $contentXml) {
        try {
            $xml = [xml](Get-Content $contentXml -Raw)
            foreach ($n in $xml.SelectNodes('//extension')) { $enabled[[string]$n.id] = ([string]$n.enabled -eq 'true') }
        } catch { Write-Warning "Could not read $contentXml : $($_.Exception.Message)" }
    }
    foreach ($id in $ForbiddenExtensions) {
        $dir = Join-Path (Join-Path $X4Dir 'extensions') $id
        $found.Add([pscustomobject]@{ Id = $id; Installed = (Test-Path $dir); Enabled = ($enabled.ContainsKey($id) -and $enabled[$id]); Path = $dir })
    }
    return $found
}

function Invoke-Native([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

# Publishes the server and FakeNode when missing (or -Force). Returns @{ Server; FakeNode }.
function Ensure-Published([switch]$Force) {
    $serverExe = Join-Path $Repo 'out\win-x64\x4mp-server.exe'
    $fakeNodeExe = Join-Path $Repo 'out\fakenode\X4MP.FakeNode.exe'
    if ($Force -or -not (Test-Path $serverExe) -or -not (Test-Path $fakeNodeExe)) {
        if (-not (Test-Path (Join-Path $Repo 'tools\flatc\bin\flatc.exe'))) {
            Invoke-Native 'fetch-flatc' { & powershell -NoProfile -File (Join-Path $Repo 'tools\flatc\fetch-flatc.ps1') }
        }
        Invoke-Native 'dotnet publish (server)' { dotnet publish (Join-Path $Repo 'server\src\X4MP.Server') -c Release '-p:PublishProfile=win-x64' -p:SkipWebBuild=true -nologo -v:m }
        Invoke-Native 'dotnet build (FakeNode)' { dotnet build (Join-Path $Repo 'tools\X4MP.FakeNode') -c Release -o (Join-Path $Repo 'out\fakenode') -nologo -v:m }
    }
    return @{ Server = $serverExe; FakeNode = $fakeNodeExe }
}

# Starts the published server (data in out\session3\data). Settings travel through the server process environment only.
# Returns the Process. -Env adds more X4MP__ settings.
function Start-S3Server([string]$ServerExe, [hashtable]$Env = @{}) {
    New-Item -ItemType Directory -Force $OutDir, (Join-Path $OutDir 'data') | Out-Null
    $vars = @{
        X4MP__Net__NodeTcpEndpoint = "127.0.0.1:$($Ports.Tcp)"     # this PC only
        X4MP__Net__UdpPort         = "$($Ports.Udp)"
        X4MP__Net__ModBuildStrict  = 'false'                         # FakeNode and the real mod report different build strings
        X4MP__Net__MaxConnectionsPerIp = '16'                        # real X4 + FakeNode clients all come from 127.0.0.1
    }
    foreach ($k in $Env.Keys) { $vars[$k] = $Env[$k] }
    $old = @{}
    foreach ($k in $vars.Keys) { $old[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, [string]$vars[$k]) }
    try {
        $p = Start-Process -FilePath $ServerExe -ArgumentList @('--data-dir', "`"$(Join-Path $OutDir 'data')`"", '--port', "$($Ports.Http)") -WorkingDirectory $OutDir `
            -RedirectStandardOutput (Join-Path $OutDir 'server.out.log') -RedirectStandardError (Join-Path $OutDir 'server.err.log') -PassThru -WindowStyle Hidden
    }
    finally { foreach ($k in $old.Keys) { [Environment]::SetEnvironmentVariable($k, $old[$k]) } }
    $url = "http://127.0.0.1:$($Ports.Http)"
    $deadline = (Get-Date).AddSeconds(40)
    while ($true) {
        if ($p.HasExited) { throw "The server exited with $($p.ExitCode); see $(Join-Path $OutDir 'server.err.log')" }
        try { if ((Invoke-WebRequest -UseBasicParsing "$url/healthz" -TimeoutSec 2).StatusCode -eq 200) { break } } catch { }
        if ((Get-Date) -gt $deadline) { throw "The server did not answer $url/healthz within 40 s" }
        Wait-Process -Id $p.Id -Timeout 1 -ErrorAction SilentlyContinue
    }
    return $p
}

# Signs in as admin. The password lives in out\session3\admin-password.txt (local test server, loopback only): when it does not
# exist yet and the server wrote initial-admin-password.txt, the forced first change is done here with a generated password.
# Returns a WebRequestSession; $Admin.Url / $Admin.PasswordFile are set. Never prints the password.
function Initialize-AdminSession {
    $url = "http://127.0.0.1:$($Ports.Http)"
    $pwFile = Join-Path $OutDir 'admin-password.txt'
    $initialFile = Join-Path $OutDir 'data\initial-admin-password.txt'
    $h = @{ 'X-X4MP' = '1' }
    $post = { param($path, $body, $session) Invoke-RestMethod -Method Post -Uri "$url$path" -Headers $h -WebSession $session -ContentType 'application/json' -Body ($body | ConvertTo-Json) }
    if (-not (Test-Path $pwFile)) {
        if (-not (Test-Path $initialFile)) { throw "No $pwFile and no initial-admin-password.txt: sign in at $url yourself and put the admin password into $pwFile." }
        $initial = (Get-Content $initialFile -Raw).Trim()
        $new = 'S3-' + [guid]::NewGuid().ToString('N')
        $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $null = & $post '/api/v1/auth/login' @{ username = 'admin'; password = $initial } $s
        $null = & $post '/api/v1/auth/change-password' @{ current = $initial; new = $new } $s
        Set-Content -Path $pwFile -Value $new -Encoding ASCII -NoNewline
    }
    $pw = (Get-Content $pwFile -Raw).Trim()
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $null = & $post '/api/v1/auth/login' @{ username = 'admin'; password = $pw } $session
    $script:Admin = @{ Url = $url; PasswordFile = $pwFile; Headers = $h; Session = $session; Password = $pw }
    return $session
}
