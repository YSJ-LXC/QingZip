@echo off
call "%~dp0..\BUILD.cmd"
if errorlevel 1 exit /b 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0RUN_UI_TESTS.ps1"
exit /b %errorlevel%
