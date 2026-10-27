@echo off
rem ===================================================================
rem  STOP_AND_SAVE.bat - EMERGENCY SAVE.
rem  Use this if START_EXPO.bat was closed, crashed, or the laptop needs
rem  to be shut down right now. It pulls everything off the headset one
rem  last time, stops the capture loops and runs the analysis.
rem  Safe to run twice. Never deletes anything on the headset.
rem ===================================================================
setlocal EnableDelayedExpansion
title STOP AND SAVE - BoardroomXR

set "EXPO=%~dp0"
if "%EXPO:~-1%"=="\" set "EXPO=%EXPO:~0,-1%"

set "ADB=C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
set "PKG=com.kartikagrawal.boardroomxr"
set "REMOTE=/sdcard/Android/data/%PKG%/files"

echo.
echo   STOP AND SAVE
echo   =============

rem ---- find the newest session folder ----
set "SESSION="
for /f "delims=" %%D in ('powershell -NoProfile -Command "$l=Join-Path '%EXPO%' 'logs'; if(Test-Path -LiteralPath $l){ $d=Get-ChildItem -LiteralPath $l -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1; if($d){$d.FullName} }"') do set "SESSION=%%D"

if "!SESSION!"=="" (
  echo   No session folder under %EXPO%\logs - creating one for this rescue pull.
  for /f %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd_HH-mm-ss"') do set "STAMP=%%T"
  set "SESSION=%EXPO%\logs\RESCUE_!STAMP!"
  mkdir "!SESSION!" 2>nul
)
mkdir "!SESSION!\pulled" 2>nul
echo   session: !SESSION!
echo.

rem ---- stop the capture loops ----
echo   Stopping background capture windows...
if exist "!SESSION!\.running" del "!SESSION!\.running" 2>nul
taskkill /f /fi "WINDOWTITLE eq BXR logcat*"  >nul 2>&1
taskkill /f /fi "WINDOWTITLE eq BXR puller*"  >nul 2>&1
taskkill /f /fi "WINDOWTITLE eq BXR forward*" >nul 2>&1

rem ---- final pull, if a headset is there ----
set "SERIAL="
for /f "skip=1 tokens=1,2" %%A in ('"%ADB%" devices 2^>nul') do if /i "%%B"=="device" if "!SERIAL!"=="" set "SERIAL=%%A"

if "!SERIAL!"=="" (
  echo   No headset connected - skipping the pull and analysing whatever is
  echo   already in the session folder.
) else (
  echo   Pulling from !SERIAL! ...
  for %%D in (BiometricLogs MFALogs BiometricTemplates MFAEnrolment Telemetry) do (
    echo     %%D
    "%ADB%" -s !SERIAL! pull "%REMOTE%/%%D" "!SESSION!\pulled" >nul 2>&1
  )
  "%ADB%" -s !SERIAL! pull "%REMOTE%" "!SESSION!\pulled\files_all" >nul 2>&1
  rem One last logcat dump, in case the streaming capture had already died.
  "%ADB%" -s !SERIAL! logcat -d -v time Unity:V BoardroomXR:V CRASH:E DEBUG:E AndroidRuntime:E *:S >> "!SESSION!\logcat_full.txt" 2>nul
  "%ADB%" -s !SERIAL! forward --remove tcp:8080 >nul 2>&1
  echo   Pull complete.
)
echo.

echo   Running analysis...
powershell -NoProfile -ExecutionPolicy Bypass -File "%EXPO%\analyse_session.ps1" -SessionFolder "!SESSION!"

echo rescue_save,%DATE% %TIME%>> "!SESSION!\session_info.txt"
echo.
echo   Everything is saved in:
echo   !SESSION!
echo.
echo   Copy that whole folder to a pendrive before shutting down.
start "" "!SESSION!"
pause
exit /b 0
