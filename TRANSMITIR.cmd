@echo off
setlocal
set "APP=%~dp0VDO-Ninja-Streamer.exe"

if not exist "%APP%" (
  echo Supervisor compilado nao encontrado:
  echo %APP%
  pause
  exit /b 1
)

start "VDO-Ninja Streamer" "%APP%"
exit /b 0
