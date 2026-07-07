@echo off
rem Double-click-friendly launcher for build-store-package.ps1.
rem Keeps the window open when started from Explorer so the result is visible.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-store-package.ps1" %*
set EXITCODE=%ERRORLEVEL%
echo %cmdcmdline% | find /i "/c" >nul && pause
exit /b %EXITCODE%
