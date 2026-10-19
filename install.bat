@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %* 
if %ERRORLEVEL% NEQ 0 pause
