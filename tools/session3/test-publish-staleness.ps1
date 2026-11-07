# F30 test: Get-PublishStaleness (tools\session3\common.ps1) on a synthetic repo tree with controlled file times. Called by the Session4Kit
# dry run (mod\tests\hostsim\session4_dry_run.ps1); exit 0 = all checks passed.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ('x4mp-stale-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$script:n = 0
function Check([bool]$c, [string]$what) { $script:n++; if (-not $c) { throw "CHECK FAILED: $what" }; Write-Host "  ok #$($script:n) $what" }
function Touch([string]$rel, [datetime]$t) {
    $f = Join-Path $root $rel
    New-Item -ItemType Directory -Force (Split-Path $f) | Out-Null
    if (-not (Test-Path $f)) { Set-Content $f 'x' }
    (Get-Item $f).LastWriteTimeUtc = $t.ToUniversalTime()
}
function Why($r, [string]$k) { return (@($r[$k]) -join '; ') }

try {
    $t0 = [datetime]'2026-01-01T12:00:00Z'
    $old = $t0; $mid = $t0.AddHours(1); $new = $t0.AddHours(2)
    # everything built after the sources (web dist at mid, exes at new)
    foreach ($f in 'server\src\X4MP.Core\A.cs', 'server\web\src\App.tsx', 'server\web\index.html', 'server\web\package.json', 'server\web\package-lock.json',
        'server\web\vite.config.ts', 'protocol\schema\x4mp.fbs', 'Directory.Packages.props', 'tools\X4MP.FakeNode\Program.cs') { Touch $f $old }
    Touch 'server\web\dist\index.html' $mid
    Touch 'server\web\dist\assets\app.js' $mid
    Touch 'out\win-x64\x4mp-server.exe' $new
    Touch 'out\fakenode\X4MP.FakeNode.exe' $new
    Touch 'server\src\X4MP.Server\obj\junk.cs' $new.AddHours(5)    # build output must be ignored
    Touch 'server\web\node_modules\x\i.js' $new.AddHours(5)

    $r = Get-PublishStaleness -RepoRoot $root -BuiltHash 'abc1234567' -HeadHash 'abc1234567'
    Check ($r.Web.Count -eq 0 -and $r.Server.Count -eq 0 -and $r.FakeNode.Count -eq 0) 'everything built after its sources: nothing is stale (obj and node_modules ignored)'

    $r = Get-PublishStaleness -RepoRoot $root -BuiltHash 'abc1234567' -HeadHash 'abc1234567' -Force
    Check ($r.Web.Count -eq 1 -and $r.Server.Count -eq 1 -and $r.FakeNode.Count -eq 1 -and (Why $r 'Web') -eq '-Rebuild') '-Rebuild marks all three'

    Touch 'server\web\src\Sessions.tsx' $new.AddHours(1)
    $r = Get-PublishStaleness -RepoRoot $root -BuiltHash 'abc1234567' -HeadHash 'abc1234567'
    Check ((Why $r 'Web') -match 'Sessions\.tsx') 'a web source newer than dist\index.html: the GUI is stale, naming the file'
    Check ($r.Server.Count -gt 0) 'and the server is republished (it embeds the GUI)'
    Check ($r.FakeNode.Count -eq 0) 'the FakeNode is not affected by web changes'
    (Get-Item (Join-Path $root 'server\web\src\Sessions.tsx')).LastWriteTimeUtc = $old
    foreach ($f in 'package.json', 'package-lock.json', 'vite.config.ts', 'index.html') {
        Touch "server\web\$f" $new.AddHours(1)
        $r = Get-PublishStaleness -RepoRoot $root -BuiltHash 'abc1234567' -HeadHash 'abc1234567'
        Check ((Why $r 'Web') -match [regex]::Escape($f)) "$f newer than dist\index.html: the GUI is stale"
        (Get-Item (Join-Path $root "server\web\$f")).LastWriteTimeUtc = $old
    }

    Touch 'server\src\X4MP.Core\A.cs' $new.AddHours(1)
    $r = Get-PublishStaleness -RepoRoot $root -BuiltHash 'abc1234567' -HeadHash 'abc1234567'
    Check ($r.Web.Count -eq 0 -and (Why $r 'Server') -match 'A\.cs' -and $r.FakeNode.Count -gt 0) 'a server source newer than the exes: server and FakeNode stale, the GUI is not'
    (Get-Item (Join-Path $root 'server\src\X4MP.Core\A.cs')).LastWriteTimeUtc = $old

    Touch 'Directory.Packages.props' $new.AddHours(1)
    $r = Get-PublishStaleness -RepoRoot $root -BuiltHash 'abc1234567' -HeadHash 'abc1234567'
    Check ((Why $r 'Server') -match 'Directory\.Packages\.props') 'Directory.Packages.props newer than the exe: stale'
    (Get-Item (Join-Path $root 'Directory.Packages.props')).LastWriteTimeUtc = $old

    Touch 'tools\X4MP.FakeNode\Program.cs' $new.AddHours(1)
    $r = Get-PublishStaleness -RepoRoot $root -BuiltHash 'abc1234567' -HeadHash 'abc1234567'
    Check ($r.Server.Count -eq 0 -and (Why $r 'FakeNode') -match 'Program\.cs') 'a FakeNode source newer than its exe: only the FakeNode is stale'
    (Get-Item (Join-Path $root 'tools\X4MP.FakeNode\Program.cs')).LastWriteTimeUtc = $old

    $r = Get-PublishStaleness -RepoRoot $root -BuiltHash 'abc1234567' -HeadHash 'def7654321'
    Check ((Why $r 'Server') -match 'built from git abc1234567, HEAD is def7654321') 'the exe reports another build hash than HEAD: the server is republished'
    $r = Get-PublishStaleness -RepoRoot $root -BuiltHash 'unknown' -HeadHash 'def7654321'
    Check ($r.Server.Count -eq 0) 'an unknown build hash does not force a rebuild'
    $r = Get-PublishStaleness -RepoRoot $root -BuiltHash $null -HeadHash 'def7654321'
    Check ($r.Server.Count -eq 0) 'an unreadable build hash does not force a rebuild'

    Touch 'server\web\dist\assets\app.js' $new.AddHours(2)
    $r = Get-PublishStaleness -RepoRoot $root -BuiltHash 'abc1234567' -HeadHash 'abc1234567'
    Check ((Why $r 'Server') -match 'embeds it') 'a built GUI newer than the exe: the server is republished'

    Remove-Item (Join-Path $root 'server\web\dist') -Recurse -Force
    Remove-Item (Join-Path $root 'out\win-x64\x4mp-server.exe') -Force
    $r = Get-PublishStaleness -RepoRoot $root -BuiltHash $null -HeadHash 'abc1234567'
    Check ((Why $r 'Web') -match 'missing' -and (Why $r 'Server') -match 'missing') 'missing dist and missing exe are stale'
    Write-Host "test-publish-staleness: PASS ($script:n checks)"
}
finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
