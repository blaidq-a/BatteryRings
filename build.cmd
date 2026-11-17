@echo off
rem Build BatteryRings with the .NET Framework 4 compiler that ships with Windows (nothing else to install).
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist dist mkdir dist
rem Windows Runtime metadata (Bluetooth LE, for AirPods) ships with Windows 10 and 11
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata
set REFS=-r:"%WM%\Windows.Devices.winmd" -r:"%WM%\Windows.Foundation.winmd" -r:"%WM%\Windows.Storage.winmd" -r:"%FW%\System.Runtime.dll" -r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll"
"%CSC%" -nologo -optimize+ -target:winexe -win32manifest:src\app.manifest -win32icon:src\app.ico %REFS% -out:dist\BatteryRings.exe src\*.cs
if errorlevel 1 exit /b 1
echo Built: dist\BatteryRings.exe
