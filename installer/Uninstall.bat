@echo off
rem Removes the X4MP mod. Saves and profiles are never touched.
set "SCRIPT=%~dp0installer\Uninstall.ps1"
if not exist "%SCRIPT%" set "SCRIPT=%~dp0Uninstall.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
