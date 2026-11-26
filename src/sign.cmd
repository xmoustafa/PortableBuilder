@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0sign.ps1" -Pfx "%~1" -Password "%~2"
endlocal
