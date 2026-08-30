@echo off
rem One-click build & launch for Pixbian (delegates to run.ps1).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
