<#
.SYNOPSIS
  Claude Code PreToolUse hook (Bash and PowerShell tools): blocks commands that break the X4MP working rules
  (docs/execution-plan.md). Reads the hook JSON on stdin; exit 2 + a message on stderr blocks the call and tells the agent why.

  Blocked:
    - sleep / Start-Sleep / timeout /t (agents must use run_in_background and wait for the notification)
    - killing processes by name: Stop-Process -Name, taskkill /IM, kill/pkill/killall by name, Get-Process <name> | Stop-Process
    - git commit messages with attribution lines (Co-Authored-By, "Generated with")
  Anything that cannot be parsed is allowed (the hook must never block normal work by accident).
#>
$ErrorActionPreference = 'Stop'
try {
    $raw = [Console]::In.ReadToEnd()
    $cmd = (ConvertFrom-Json $raw).tool_input.command
} catch {
    exit 0
}
if ([string]::IsNullOrWhiteSpace($cmd)) { exit 0 }

function Block([string]$why) {
    [Console]::Error.WriteLine("Blocked by the X4MP command guard (tools/hooks/guard-commands.ps1): $why")
    exit 2
}

# A command word at the start of the command or after a separator / pipe / subshell / call operator.
$start = '(^|[;&|(\n]|\$\(|&\s|\bdo\s|\bthen\s)\s*'

if ($cmd -match "(?i)$start(sleep|start-sleep)\b") {
    Block 'never run sleep or Start-Sleep; run long commands with run_in_background and wait for the completion notification.'
}
if ($cmd -match "(?i)$start(timeout(\.exe)?\s+/t\s+\d)") {
    Block 'never wait with timeout /t; run long commands with run_in_background and wait for the completion notification.'
}
if ($cmd -match '(?i)\b(stop-process|spps|kill)\b[^;|\n]*\s-(name|processname)\b' -or
    $cmd -match '(?i)\btaskkill(\.exe)?\b[^;|\n]*\s/im\b' -or
    $cmd -match "(?i)$start(pkill|killall)\b" -or
    $cmd -match '(?i)\b(get-process|gps|ps)\s+(-name\s+)?["'']?[a-z][\w.*-]*["'']?\s*\|\s*(stop-process|spps|kill)\b') {
    Block 'never kill processes by name (other agents and the lead run tests on this machine); stop only the process ids you started.'
}
if ($cmd -match '(?i)\bgit\b[^\n]*\bcommit\b' -and $cmd -match '(?i)(co-authored-by\s*:|generated with)') {
    Block 'no attribution lines in commit messages (no Co-Authored-By, no "Generated with").'
}
exit 0
