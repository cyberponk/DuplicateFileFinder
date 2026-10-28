@echo off
rem Builds DuplicateFileFinder.exe with the C# compiler that comes with .NET Framework 4.x
rem (part of Windows 10/11 and Windows Server), so nothing needs to be installed.
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
rem The icon (made by make-icon.ps1) goes into the .exe for Explorer, and as a resource for the window
set "ICON="
if exist "%~dp0DuplicateFileFinder.ico" set ICON=/win32icon:"%~dp0DuplicateFileFinder.ico" /resource:"%~dp0DuplicateFileFinder.ico",DuplicateFileFinder.ico
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:"%~dp0bin\DuplicateFileFinder.exe" %ICON% ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Management.dll "%~dp0DuplicateFileFinder.cs"
if errorlevel 1 (echo Build FAILED & exit /b 1)
echo Built "%~dp0bin\DuplicateFileFinder.exe"
