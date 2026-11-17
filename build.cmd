@echo off
rem BatteryRings derleme: Windows'ta hazir gelen .NET Framework 4 derleyicisi yeterli, ek kurulum gerekmez.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist dist mkdir dist
"%CSC%" -nologo -optimize+ -target:winexe -win32manifest:src\app.manifest -win32icon:src\app.ico -out:dist\BatteryRings.exe src\*.cs
if errorlevel 1 exit /b 1
echo Hazir: dist\BatteryRings.exe
