@echo off
REM ==== OSRS Unity port - EASY BUTTON ====
REM Starts the OpenRune server (pre-built). Then press Play in Unity.
cd /d "%~dp0"
title OpenRune server (close this window to stop)
set "JAVA_HOME=E:\tools\jdk-21.0.12.1+1"
set "PATH=%JAVA_HOME%\bin;%PATH%"

echo Freeing port 43610 if an old server is still running...
powershell -NoProfile -Command "Get-NetTCPConnection -LocalPort 43610 -State Listen -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }"

echo.
echo Starting server... wait for "Server ready", then open Unity and press Play.
echo Unity project: %~dp0osrs-port-tools\unity
echo.
call gradlew.bat run --offline
echo.
echo Server stopped.
pause
