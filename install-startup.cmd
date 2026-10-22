@echo off
pwsh.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0scripts\install-startup-admin.ps1" %*
exit /b %errorlevel%
