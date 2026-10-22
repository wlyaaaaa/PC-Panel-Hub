@echo off
pwsh.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0install-startup-admin.ps1" %*
exit /b %errorlevel%
