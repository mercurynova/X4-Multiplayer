@echo off
rem Packs the mod logs and settings into one zip on the Desktop, ready to send.
set "SCRIPT=%~dp0installer\CollectLogs.ps1"
if not exist "%SCRIPT%" set "SCRIPT=%~dp0CollectLogs.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
