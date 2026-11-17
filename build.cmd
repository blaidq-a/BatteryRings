@echo off
rem Build BatteryRings with the .NET Framework 4 compiler that ships with Windows (nothing else to install).
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist dist mkdir dist
"%CSC%" -nologo -optimize+ -target:winexe -win32manifest:src\app.manifest -win32icon:src\app.ico -out:dist\BatteryRings.exe src\*.cs
if errorlevel 1 exit /b 1
echo Built: dist\BatteryRings.exe
