@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [X] csc.exe not found. Install .NET Framework 4.8.
  exit /b 1
)

if not exist "apps.json" (
  echo [X] apps.json missing.
  exit /b 2
)
if not exist "app.ico" (
  echo [X] app.ico missing.
  exit /b 3
)
if not exist "PortableLauncher\Program.cs" (
  echo [X] PortableLauncher\Program.cs missing.
  exit /b 4
)
if not exist "RobloxLauncher\Program.cs" (
  echo [X] RobloxLauncher\Program.cs missing.
  exit /b 5
)
if not exist "GameLoopLauncher\Program.cs" (
  echo [X] GameLoopLauncher\Program.cs missing.
  exit /b 6
)
if not exist "FiveMLauncher\Program.cs" (
  echo [X] FiveMLauncher\Program.cs missing.
  exit /b 7
)

echo [i] Building generic launcher...
pushd "PortableLauncher"
call build.cmd
if errorlevel 1 exit /b 1
popd

echo [i] Building Roblox launcher...
pushd "RobloxLauncher"
call build.cmd
if errorlevel 1 exit /b 1
popd

echo [i] Building GameLoop launcher...
pushd "GameLoopLauncher"
call build.cmd
if errorlevel 1 exit /b 1
popd

echo [i] Building FiveM launcher...
pushd "FiveMLauncher"
call build.cmd
if errorlevel 1 exit /b 1
popd

echo Building GUI...
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /debug- ^
  /out:..\PortableBuilder.exe ^
  /win32icon:app.ico ^
  /win32manifest:app.manifest ^
  /resource:apps.json,PortableBuilder.apps.json ^
  /resource:app.ico,PortableBuilder.app.ico ^
  /resource:PortableLauncher\Program.cs,PortableBuilder.LauncherProgram.cs ^
  /resource:PortableLauncher\PortableLauncher.exe,PortableBuilder.PortableLauncher.exe ^
  /resource:RobloxLauncher\RobloxLauncher.exe,PortableBuilder.RobloxLauncher.exe ^
  /resource:GameLoopLauncher\GameLoopLauncher.exe,PortableBuilder.GameLoopLauncher.exe ^
  /resource:FiveMLauncher\FiveMLauncher.exe,PortableBuilder.FiveMLauncher.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  GuiProgram.cs Gui.cs Engine.cs Updater.cs UpdateConfig.cs AssemblyInfo.cs
if errorlevel 1 (
  echo [X] GUI BUILD FAILED
  exit /b 1
)

echo [OK] Built ..\PortableBuilder.exe (single file)
endlocal
