@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [X] csc.exe not found. Install .NET Framework 4.8.
  exit /b 1
)

"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /debug- ^
  /out:PortableLauncher.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  Program.cs EmbeddedConfig.cs AssemblyInfo.cs

if errorlevel 1 (
  echo [X] BUILD FAILED
  exit /b 1
)

echo [OK] PortableLauncher.exe built.
endlocal
