<#
.SYNOPSIS
  Scripted click-through of the WPF launcher (M6-X2..X4): start a server, open the real window, fill the address, press
  Check, read back the session line, the plan and the status text, and keep a screenshot.

.DESCRIPTION
  This is the "click-through" that would otherwise need a person at the keyboard. It drives the built launcher through
  UI Automation - the same accessibility layer a screen reader uses - so the assertions are on the real controls and the
  real HTTP round trip to a real server, not on a mock.

  Safety rules in the script:
  - it refuses to start when %LOCALAPPDATA%\X4MP\pending-restore.json exists (a player's mod set is waiting to be
    restored) or when X4.exe is running;
  - it never presses Apply, Restore or Start X4, so the player's content.xml and launch.json are not touched;
  - the server it starts uses a throwaway data dir under the output folder.

.PARAMETER Serve
  Start `out\win-x64\x4mp-server.exe` on the address in -ServerUrl instead of expecting one to be running.
.PARAMETER KeepOpen
  Leave the launcher and the server running after the checks (for a manual look at the window).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/launcher-clickthrough.ps1 -Serve
#>
[CmdletBinding()]
param(
  [string]$Launcher = '',
  [string]$ServerUrl = 'http://127.0.0.1:47790',
  [string]$ServerExe = '',
  [string]$Out = '',
  [int]$TimeoutSeconds = 120,
  [switch]$Serve,
  [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$Repo = Split-Path -Parent $PSScriptRoot
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
if (-not $Out) { $Out = Join-Path $Repo "out\launcher-check-$stamp" }
if (-not $Launcher) { $Launcher = Join-Path $Repo 'out\launcher-win-x64\x4mp-launcher.exe' }
if (-not $ServerExe) { $ServerExe = Join-Path $Repo 'out\win-x64\x4mp-server.exe' }
$work = Join-Path $Out 'work'
New-Item -ItemType Directory -Force $Out, $work | Out-Null

function Say([string]$text) { Write-Host $text }
function Fail([string]$text) { throw "launcher-check: $text" }

function Stop-Tree($proc) {
  if ($null -eq $proc) { return }
  try { if ($proc.HasExited) { return } } catch { return }
  try { & taskkill /PID $proc.Id /T /F *> $null } catch { }
}

function Get-Health([string]$url) {
  try { return [int](Invoke-WebRequest -UseBasicParsing "$url/healthz" -TimeoutSec 3).StatusCode } catch { return 0 }
}

function Wait-Until([string]$what, [scriptblock]$probe, [int]$timeoutSec = 30) {
  $end = (Get-Date).AddSeconds($timeoutSec)
  while ($true) {
    try { if (& $probe) { return } } catch { }
    if ((Get-Date) -gt $end) { Fail "timed out after ${timeoutSec}s waiting for $what" }
    Start-Sleep -Milliseconds 200
  }
}

function Start-Proc([string]$name, [string]$exe, [string[]]$argList, [hashtable]$envVars = @{}) {
  $old = @{}
  foreach ($k in $envVars.Keys) {
    $old[$k] = [Environment]::GetEnvironmentVariable($k)
    [Environment]::SetEnvironmentVariable($k, [string]$envVars[$k])
  }
  try {
    $quoted = @($argList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
    $p = Start-Process -FilePath $exe -ArgumentList $quoted -PassThru -NoNewWindow `
      -RedirectStandardOutput (Join-Path $Out "$name.log") -RedirectStandardError (Join-Path $Out "$name.err.log")
  } finally {
    foreach ($k in $old.Keys) {
      if ($null -eq $old[$k]) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue }
      else { [Environment]::SetEnvironmentVariable($k, $old[$k]) }
    }
  }
  $null = $p.Handle
  return $p
}

# ------------------------------------------------------------------------------------------------ safety gates

$pending = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'X4MP\pending-restore.json'
if (Test-Path $pending) {
  Fail "a previous session's mod set is still pending ($pending); let the launcher restore it first, then run this again"
}
if (Get-Process X4 -ErrorAction SilentlyContinue) { Fail 'X4.exe is running; the launcher would refuse to write, so the check would prove less' }
if (-not (Test-Path $Launcher)) { Fail "missing $Launcher (publish the launcher: dotnet publish launcher/X4MP.Launcher -c Release -p:PublishProfile=win-x64)" }

$uri = [Uri]$ServerUrl
$httpPort = if ($uri.Port -gt 0) { $uri.Port } else { 80 }
$tcpPort = $httpPort - 10
$udpPort = $tcpPort + 1

Say "launcher-check: launcher $Launcher"
Say "launcher-check: server   $ServerUrl (tcp $tcpPort, udp $udpPort)"
Say "launcher-check: output   $Out"

# ------------------------------------------------------------------------------------------------ server

$server = $null
if ((Get-Health $ServerUrl) -ne 200) {
  if (-not $Serve) { Fail "nothing answers /healthz at $ServerUrl; pass -Serve to start the published server" }
  if (-not (Test-Path $ServerExe)) { Fail "missing $ServerExe" }
  $data = Join-Path $work 'server-data'
  New-Item -ItemType Directory -Force $data | Out-Null
  $envVars = @{
    X4MP__Net__MaxConnectionsPerIp = '64'
    X4MP__Net__MaxPlayers          = '16'
    X4MP__Net__NodeTcpEndpoint     = "0.0.0.0:$tcpPort"
    X4MP__Net__UdpPort             = "$udpPort"
  }
  Say 'launcher-check: starting the server'
  $server = Start-Proc 'server' $ServerExe @('--data-dir', $data, '--port', "$httpPort") $envVars
  Wait-Until 'the server to answer /healthz' {
    if ($server.HasExited) { Fail "the server exited with $($server.ExitCode); see $Out\server.err.log" }
    (Get-Health $ServerUrl) -eq 200
  } 60
}
Say 'launcher-check: server is up'

# ------------------------------------------------------------------------------------------------ window

$proc = Start-Process -FilePath $Launcher -PassThru
$null = $proc.Handle
Say "launcher-check: launcher pid $($proc.Id)"
Wait-Until 'the launcher window' {
  $proc.Refresh()
  $proc.MainWindowHandle -ne [IntPtr]::Zero
} 60

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$scope = [System.Windows.Automation.TreeScope]::Descendants
function Find-ById([string]$id) {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $root.FindFirst($scope, $cond)
}
function Text-Of([string]$id) {
  $el = Find-ById $id
  if ($null -eq $el) { return '' }
  # A TextBox exposes its text through ValuePattern; a TextBlock only has a Name. Reading Name alone returned '' for the
  # player name box even though the window had filled it in.
  $pattern = $null
  if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
    return ([System.Windows.Automation.ValuePattern]$pattern).Current.Value
  }
  return $el.Current.Name
}
function Set-Text([string]$id, [string]$text) {
  $el = Find-ById $id
  if ($null -eq $el) { Fail "control '$id' not found" }
  $pattern = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  $pattern.SetValue($text)
}
function Invoke-Control([string]$id) {
  $el = Find-ById $id
  if ($null -eq $el) { Fail "control '$id' not found" }
  $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Enabled([string]$id) {
  $el = Find-ById $id
  if ($null -eq $el) { return $false }
  return $el.Current.IsEnabled
}

$title = $root.Current.Name
Say "launcher-check: window '$title'"
$initialStatus = Text-Of 'StatusBlock'
if ($initialStatus -match 'pending') { Fail "the window reports a pending restore: $initialStatus" }
$applyBefore = Enabled 'ApplyButton'

Say "launcher-check: setting the address to $ServerUrl and pressing Check"
Set-Text 'ServerBox' $ServerUrl
Invoke-Control 'CheckButton'

Wait-Until 'the check to finish' {
  $s = Text-Of 'StatusBlock'
  ($s.Length -gt 0) -and ($s -ne 'Checking...') -and ($s -ne $initialStatus)
} $TimeoutSeconds

$statusText = Text-Of 'StatusBlock'
$sessionText = Text-Of 'SessionBlock'
$nameText = Text-Of 'NameBox'
$planList = Find-ById 'PlanList'
$planItems = @()
if ($null -ne $planList) {
  # The rows carry the PlanItem text in their Name; the same enumeration also returns the GridView column headers.
  $columnTitles = @('Action', 'Mod', 'Class', 'Rule', 'Version', 'Notes')
  $items = $planList.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($item in $items) {
    $text = $item.Current.Name
    # The row's own Name is the PlanItem dump; the same pass also yields its cells one by one.
    if ($text -and $text.StartsWith('PlanItem {')) { $planItems += $text }
  }
}
$planItems = @($planItems | Where-Object { $_ })
Say "launcher-check: session: $sessionText"
Say "launcher-check: status:  $statusText"
Say ("launcher-check: plan:    {0} row(s)" -f $planItems.Count)
foreach ($row in $planItems) { Say "launcher-check:   $row" }

# ------------------------------------------------------------------------------------------------ screenshot

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WinRect {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
'@
$shot = Join-Path $Out 'launcher.png'
$rect = New-Object WinRect+RECT
if ([WinRect]::GetWindowRect($proc.MainWindowHandle, [ref]$rect)) {
  $w = $rect.Right - $rect.Left
  $h = $rect.Bottom - $rect.Top
  if ($w -gt 0 -and $h -gt 0) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
    $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    Say "launcher-check: screenshot $shot"
  }
}

# ------------------------------------------------------------------------------------------------ verdict

$checks = New-Object System.Collections.Generic.List[object]
function Check([bool]$Ok, [string]$What, [string]$Detail) {
  $checks.Add([pscustomobject]@{ Ok = $Ok; What = $What; Detail = $Detail })
  $mark = if ($Ok) { 'ok  ' } else { 'FAIL' }
  Say ("launcher-check: {0} {1} - {2}" -f $mark, $What, $Detail)
}

Check ($title -like '*X4MP*') 'window is the launcher' "'$title'"
Check (-not ($statusText -match 'could not be reached')) 'server reached' $statusText
Check ($sessionText -match '^Session ".*", policy v\d+, game build \S+') 'session line' $sessionText
Check ($statusText -match '^(Nothing to do: .*|\d+ change\(s\) are needed before this session will accept you\.)') 'status summary' $statusText
Check ($nameText.Length -gt 0) 'player name filled in' "'$nameText'"
Check (-not $applyBefore) 'Apply was disabled before the check' "enabled=$applyBefore"
Check ($sessionText -match 'policy v\d+') 'policy version came from the server' $sessionText
if ($statusText -match '(\d+) change\(s\) are needed') {
  $expected = [int]$Matches[1]
  Check ($planItems.Count -eq $expected) 'the plan lists one row per change' "$($planItems.Count) row(s), status says $expected"
} elseif ($statusText -match '^Nothing to do') {
  Check ($planItems.Count -eq 0) 'the plan is empty when nothing is to do' "$($planItems.Count) row(s)"
}

$failed = @($checks | Where-Object { -not $_.Ok })
$verdict = if ($failed.Count -eq 0) { 'PASS' } else { 'FAIL' }

$report = Join-Path $Out 'report.md'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Launcher click-through $stamp")
$lines.Add('')
$lines.Add("- Verdict: **$verdict** (" + ($checks.Count - $failed.Count) + ' of ' + $checks.Count + ' checks)')
$lines.Add("- Server: $ServerUrl")
$lines.Add('- Launcher: ' + $Launcher)
$lines.Add('- Window: ' + $title)
$lines.Add('- Git: ' + (& git -C $Repo log -1 --format='%h %s' 2>$null))
$lines.Add('')
$lines.Add('## What the window showed')
$lines.Add('')
$lines.Add('- Player name: ' + $nameText)
$lines.Add('- Session: ' + $sessionText)
$lines.Add('- Status: ' + $statusText)
$lines.Add('- Plan rows: ' + $planItems.Count)
foreach ($row in $planItems) { $lines.Add('  - ' + $row) }
$lines.Add('')
$lines.Add('## Checks')
$lines.Add('')
foreach ($c in $checks) {
  $mark = if ($c.Ok) { 'x' } else { ' ' }
  $lines.Add("- [$mark] $($c.What): $($c.Detail)")
}
$lines.Add('')
$lines.Add('Apply, Restore and Start X4 were never pressed, so no profile file and no launch.json were written.')
[IO.File]::WriteAllText($report, ($lines -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
Say ''
Say "launcher-check: verdict $verdict - $report"

# ------------------------------------------------------------------------------------------------ teardown

if (-not $KeepOpen) {
  try { $proc.CloseMainWindow() | Out-Null } catch { }
  Start-Sleep -Seconds 2
  Stop-Tree $proc
  if ($null -ne $server) {
    Stop-Tree $server
    Remove-Item -Recurse -Force (Join-Path $work 'server-data') -ErrorAction SilentlyContinue
  }
} else {
  Say 'launcher-check: leaving the launcher and the server running (-KeepOpen)'
}

if ($failed.Count -gt 0) { exit 1 }
exit 0
