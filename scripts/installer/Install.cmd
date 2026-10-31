@echo off
rem Double-click to install ContactManage for this user (no administrator needed).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
echo.
pause
