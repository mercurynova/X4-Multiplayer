@echo off
rem Checks that the mod can run here and prints one verdict. Exit code 1 means it cannot.
set "SCRIPT=%~dp0installer\CheckInstall.ps1"
if not exist "%SCRIPT%" set "SCRIPT=%~dp0CheckInstall.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
