@echo off
rem Installs the X4MP mod into X4: Foundations. Pass a folder to override the game location.
set "SCRIPT=%~dp0installer\Install.ps1"
if not exist "%SCRIPT%" set "SCRIPT=%~dp0Install.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
