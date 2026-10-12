@echo off
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
set "FRAMEWORK=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FRAMEWORK%\csc.exe" set "FRAMEWORK=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
"%FRAMEWORK%\csc.exe" /nologo /target:exe /optimize+ /out:bin\EngineCli.exe /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll src\ArchiveEngine.cs tests\EngineCli.cs
if errorlevel 1 exit /b 1
python tests\verify.py
exit /b %errorlevel%
