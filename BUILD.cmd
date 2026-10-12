@echo off
setlocal
call "%~dp0src\BUILD.cmd"
if errorlevel 1 exit /b 1
if not exist "%~dp0bin" mkdir "%~dp0bin"
copy /y "%~dp0src\QingZip.exe" "%~dp0bin\QingZip.exe" >nul
copy /y "%~dp0src\QingZip.exe.config" "%~dp0bin\QingZip.exe.config" >nul
echo Built bin\QingZip.exe
exit /b 0
