@echo off
setlocal
cd /d "%~dp0"
set "FRAMEWORK=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FRAMEWORK%\csc.exe" set "FRAMEWORK=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
"%FRAMEWORK%\csc.exe" /nologo /target:winexe /optimize+ /utf8output /out:QingZip.exe /win32manifest:app.manifest /resource:MainView.xaml,QingZip.MainView.xaml /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Xaml.dll /reference:"%FRAMEWORK%\WPF\WindowsBase.dll" /reference:"%FRAMEWORK%\WPF\PresentationCore.dll" /reference:"%FRAMEWORK%\WPF\PresentationFramework.dll" ArchiveEngine.cs Main.cs AssemblyInfo.cs
exit /b %errorlevel%
