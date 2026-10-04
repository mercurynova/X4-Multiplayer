<#
.SYNOPSIS
  Session 4: scans an X4 save for X4MP leftovers with tools\X4MP.SaveScan (M3-13, docs\m3-plan.md 4.10). Reads only; changes nothing.
.DESCRIPTION
  Lists the "[MP] "-named objects, x4mp_team_* owned ships, the reference mod's x4mp_host / x4mp_client_* traces, the team factions and our MD
  cues in a save (.xml.gz or plain .xml), prints a human summary, optionally writes JSON, and exits non-zero when something is there that should not be:
    0  nothing unexpected          1  leftovers (or an expected avatar is missing)          2  usage / unreadable save
  -Role picks the expectation:
    Checkpoint  an authority checkpoint: the avatars of -Manifest (the x4mp_<session>_<n>.x4mf next to it) are expected and must be owned by a
                team faction (sitting 2, check 8: "0 [MP] ghosts, the avatars under x4mp_team_k").
    Client      a client quicksave: -OwnAvatar (the idcode of the player's own avatar copy) may be a player-owned [MP] ship; everything else
                (ghosts, other avatars' copies, reference traces) is a leftover the janitor must remove at the next load.
    Any         no expectations: every X4MP trace is a leftover.
.PARAMETER Save       A path, or just a save name (with or without .xml.gz) that is looked up in the X4 user folder's save\ folder.
.PARAMETER Manifest   Checkpoint: the .x4mf manifest whose PlayerShip entries are the expected avatars.
.PARAMETER OwnAvatar  Client: the idcode of the player's own avatar copy.
.PARAMETER Json       Also write the JSON report here ('-' = stdout, the summary then goes to stderr).
.PARAMETER UserId     The numeric folder under Documents\Egosoft\X4 (only needed when there are several, for -Save <name>).
.EXAMPLE
  .\tools\session4\savescan.ps1 -Save x4mp_ckpt_0123456789abcdef -Role Checkpoint -Manifest .\x4mp_ckpt.x4mf -Json out\session4\savescan.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Save,
    [ValidateSet('Checkpoint', 'Client', 'Any')][string]$Role = 'Any',
    [string]$Manifest,
    [string]$OwnAvatar,
    [string]$Json,
    [string]$UserId
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# resolve the save: an existing path wins, else a name in the X4 save folder
$savePath = $null
if (Test-Path -LiteralPath $Save -PathType Leaf) {
    $savePath = (Resolve-Path -LiteralPath $Save).Path
}
else {
    . (Join-Path $PSScriptRoot 'common.ps1')
    $user = Resolve-X4UserDir $UserId
    $name = if ($Save -match '\.xml(\.gz)?$') { $Save } else { "$Save.xml.gz" }
    $candidate = Join-Path (Join-Path $user 'save') $name
    if (-not (Test-Path -LiteralPath $candidate)) { [Console]::Error.WriteLine("save not found: '$Save' (also tried $candidate)"); exit 2 }
    $savePath = $candidate
}

$argsList = @($savePath)
switch ($Role) {
    'Checkpoint' { $argsList += @('--avatar-owner', 'team'); if ($Manifest) { $argsList += @('--manifest', $Manifest) } }
    'Client' { $argsList += @('--avatar-owner', 'player'); if ($OwnAvatar) { $argsList += @('--expect-avatar', $OwnAvatar) } else { $argsList += '--no-require-avatars' } }
}
if ($Json) { $argsList += @('--json', $Json) }

$dll = Join-Path $repo 'tools\X4MP.SaveScan\bin\Release\net10.0\X4MP.SaveScan.dll'
if (-not (Test-Path $dll)) {
    & dotnet build (Join-Path $repo 'tools\X4MP.SaveScan') -c Release -nologo -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine('building tools\X4MP.SaveScan failed'); exit 2 }
}
& dotnet $dll @argsList
exit $LASTEXITCODE
