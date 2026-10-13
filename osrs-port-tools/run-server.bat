@echo off
cd /d %~dp0
title OpenRune server
echo Pulling latest code...
git pull
set JAVA_HOME=E:\tools\jdk-21.0.12.1+1
echo Starting server (close this window to stop it)...
call gradlew.bat run
pause
