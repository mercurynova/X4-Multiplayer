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
    # The web GUI is built first (npm ci + npm run build in server\web) when server\web\dist is missing: a server published without it
    # only shows a "GUI not built" page, and every dashboard check of the session needs the GUI.
    $webDir = Join-Path $Repo 'server\web'
    $needWeb = -not (Test-Path (Join-Path $webDir 'dist\index.html'))
    if ($Force -or $needWeb -or -not (Test-Path $serverExe) -or -not (Test-Path $fakeNodeExe)) {
        if (-not (Test-Path (Join-Path $Repo 'tools\flatc\bin\flatc.exe'))) {
            Invoke-Native 'fetch-flatc' { & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $Repo 'tools\flatc\fetch-flatc.ps1') }
        }
        if ($needWeb) {
            Write-Host 'Building the web GUI once (npm ci + npm run build; a few minutes the first time)...'
            Push-Location $webDir
            try {
                Invoke-Native 'npm ci' { npm ci --no-audit --no-fund }
                Invoke-Native 'npm run build' { npm run build }
            }
            finally { Pop-Location }
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
    try {
        if ((Invoke-WebRequest -UseBasicParsing "$url/" -TimeoutSec 5).Content -match 'GUI not built') {
            Write-Warning "This server was published WITHOUT the web GUI ($url shows 'GUI not built'). Stop it (Ctrl+C) and run the script again with -Rebuild."
        }
    } catch { }
    return $p
}

# Signs in as admin. The password lives in out\session3\admin-password.txt (local test server, loopback only): when it does not
# exist yet and the server wrote initial-admin-password.txt, the forced first change is done here with a generated password.
# Returns a WebRequestSession; $Admin.Url / $Admin.PasswordFile are set. Never prints the password.
# The server keeps the mod policy in its database once an admin edit (also the Nexus entry below) stored it, and then ignores the
# X4MP__Mods__Enforcement start-up setting (found by the session-3 dry run: a -Strict run made the next "normal" run strict too). So the
# start scripts set the enforcement mode explicitly through the admin API after sign-in.
function Set-S3Enforcement([switch]$Strict) {
    $want = if ($Strict) { 'Strict' } else { 'Warn' }
    try {
        $null = Invoke-RestMethod -Method Patch -Uri "$($Admin.Url)/api/v1/mods/policy" -Headers $Admin.Headers -WebSession $Admin.Session -ContentType 'application/json' -Body (@{ enforcement = $want } | ConvertTo-Json)
        Write-Host "Mod enforcement: $want"
    }
    catch { Write-Warning "Could not set the mod enforcement to ${want}: $($_.Exception.Message)" }
}

# The "modded" fake authority has a Nexus-installed mod (sn_better_traders). For the mod-refusal screen to show its Nexus address, an admin
# entry gives it the link (what an admin would type on the GUI Mods page). It is added for -AuthorityExtensions modded and removed again
# otherwise, so a normal run is not influenced by it. Failures are only warnings.
function Set-S3NexusEntry([switch]$Present) {
    $uri = "$($Admin.Url)/api/v1/mods/entries/sn_better_traders"
    try {
        if ($Present) {
            $body = @{ name = 'Better Traders'; rule = 'Required'; enabled = $true; versionRule = 'Exact'; version = '2.0'
                nexusUrl = 'https://www.nexusmods.com/x4foundations/mods/1234'; notes = '' } | ConvertTo-Json
            $null = Invoke-RestMethod -Method Put -Uri $uri -Headers $Admin.Headers -WebSession $Admin.Session -ContentType 'application/json' -Body $body
            Write-Host 'Mod list: added the Nexus link for the fake mod "Better Traders" (https://www.nexusmods.com/x4foundations/mods/1234).'
        }
        else {
            try { $null = Invoke-RestMethod -Method Delete -Uri $uri -Headers $Admin.Headers -WebSession $Admin.Session }
            catch { if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { return }; throw }
        }
    }
    catch { Write-Warning "Could not $(if ($Present) { 'add' } else { 'remove' }) the Nexus mod entry: $($_.Exception.Message)" }
}

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

# The DLC part of the fake authority's extension report (session-3 finding: without it the server refused every real player that owns
# a DLC, because a DLC difference refuses even in Warn mode). Reads <X4 install>\extensions\ego_dlc_*\content.xml (id, name, version) and
# the enabled flags of <Documents>\Egosoft\X4\<id>\content.xml (<extension id=".." enabled="true|false"/>; absent = enabled), the same
# sources the game uses. The version is written the way the game and the mod report it ("900" in content.xml -> "9.00"). The server also
# compares DLC versions normalised, so either form would match. Returns objects (empty array when no install or no DLC).
function Get-LocalDlcExtensions([string]$X4Dir, [string]$UserDir) {
    $result = New-Object System.Collections.Generic.List[object]
    if (-not $X4Dir -or -not (Test-Path (Join-Path $X4Dir 'extensions'))) { return @() }
    $enabled = @{}
    $contentXml = if ($UserDir) { Join-Path $UserDir 'content.xml' } else { $null }
    if ($contentXml -and (Test-Path $contentXml)) {
        try {
            $xml = [xml](Get-Content $contentXml -Raw)
            foreach ($n in $xml.SelectNodes('//extension')) { $enabled[[string]$n.id] = ([string]$n.enabled -ne 'false' -and [string]$n.enabled -ne '0') }
        } catch { Write-Warning "Could not read $contentXml : $($_.Exception.Message)" }
    }
    foreach ($dir in Get-ChildItem (Join-Path $X4Dir 'extensions') -Directory -Filter 'ego_dlc_*' | Sort-Object Name) {
        $file = Join-Path $dir.FullName 'content.xml'
        if (-not (Test-Path $file)) { continue }
        try { $c = ([xml](Get-Content $file -Raw)).content } catch { Write-Warning "Could not read $file : $($_.Exception.Message)"; continue }
        $id = [string]$c.id
        if (-not $id) { continue }
        $on = if ($enabled.ContainsKey($id)) { $enabled[$id] } else { [string]$c.enabled -ne '0' -and [string]$c.enabled -ne 'false' }
        if (-not $on) { continue }
        $version = ([string]$c.version).Trim()
        if ($version -match '^\d{3,9}$') { $d = $version.TrimStart('0').PadLeft(3, '0'); $version = $d.Insert($d.Length - 2, '.') }   # 900 -> 9.00
        $name = if ([string]$c.name) { [string]$c.name } else { $id }
        $result.Add([pscustomobject]@{ id = $id; name = $name; version = $version; source = 'Dlc'; enabled = $true; egosoft = $true; classHint = 'Dlc' })
    }
    return $result.ToArray()
}
