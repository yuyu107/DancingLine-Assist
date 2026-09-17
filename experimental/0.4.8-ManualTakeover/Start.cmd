@echo off
setlocal
set "assist_ps=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "assist_ps=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%assist_ps%" -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0Start.ps1"
if errorlevel 1 pause
