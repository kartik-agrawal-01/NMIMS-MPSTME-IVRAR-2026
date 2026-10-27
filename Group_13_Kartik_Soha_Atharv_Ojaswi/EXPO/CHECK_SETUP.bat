@echo off
rem ===================================================================
rem  CHECK_SETUP.bat - pre-expo readiness check. Runs WITHOUT a headset.
rem  Prints PASS or FAIL for every line. Fix any FAIL before the expo.
rem ===================================================================
setlocal EnableDelayedExpansion
title CHECK SETUP - BoardroomXR expo readiness

set "EXPO=%~dp0"
if "%EXPO:~-1%"=="\" set "EXPO=%EXPO:~0,-1%"
for %%I in ("%EXPO%\..") do set "PROJ=%%~fI"

set "ADB=C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
set "APK=%EXPO%\BoardroomXR_v5.apk"
set "HASHFILE=%EXPO%\BoardroomXR_v5.apk.sha256"
set FAILED=0

echo.
echo  BoardroomXR - expo setup check
echo  ==============================
echo  project : %PROJ%
echo  expo    : %EXPO%
echo.

call :check_file "adb"                      "%ADB%"
call :check_file "APK"                      "%APK%"
call :check_file "APK hash record"          "%HASHFILE%"
call :check_file "START_EXPO.bat"           "%EXPO%\START_EXPO.bat"
call :check_file "STOP_AND_SAVE.bat"        "%EXPO%\STOP_AND_SAVE.bat"
call :check_file "analyse_session.ps1"      "%EXPO%\analyse_session.ps1"
call :check_file "EXPO_DAY_STEPS.md"        "%EXPO%\EXPO_DAY_STEPS.md"
call :check_file "EXPO_SCENARIOS.md"        "%PROJ%\EXPO_SCENARIOS.md"

rem ---- APK hash must match the recorded one ----
if exist "%APK%" if exist "%HASHFILE%" (
  for /f "usebackq delims=" %%H in ("%HASHFILE%") do set "WANT=%%H"
  for /f "delims=" %%H in ('powershell -NoProfile -Command "(Get-FileHash -LiteralPath '%APK%' -Algorithm SHA256).Hash"') do set "GOT=%%H"
  rem the record file may be "HASH  filename"; keep only the first token
  for /f "tokens=1" %%T in ("!WANT!") do set "WANT=%%T"
  if /i "!WANT!"=="!GOT!" (
    call :pass "APK SHA-256 matches the tested build"
    echo        !GOT!
  ) else (
    call :fail "APK SHA-256 does NOT match - this is not the tested build"
    echo        expected !WANT!
    echo        found    !GOT!
  )
) else (
  call :fail "APK SHA-256 could not be compared (file or hash record missing)"
)

rem ---- APK size sanity ----
if exist "%APK%" (
  for %%S in ("%APK%") do set "APKBYTES=%%~zS"
  set /a APKMB=!APKBYTES! / 1048576
  if !APKMB! GEQ 40 (
    call :pass "APK size !APKMB! MB (expected ~80 MB)"
  ) else (
    call :fail "APK is only !APKMB! MB - looks truncated"
  )
)

rem ---- port 8080 must be free for the dashboard forward ----
set PORTBUSY=0
for /f %%P in ('powershell -NoProfile -Command "@(Get-NetTCPConnection -State Listen -LocalPort 8080 -ErrorAction SilentlyContinue).Count"') do set PORTBUSY=%%P
if "!PORTBUSY!"=="0" (
  call :pass "port 8080 is free for the dashboard"
) else (
  call :fail "port 8080 is already in use by !PORTBUSY! listener(s) - close it or the dashboard will not open"
  powershell -NoProfile -Command "Get-NetTCPConnection -State Listen -LocalPort 8080 -ErrorAction SilentlyContinue | ForEach-Object { '       held by PID ' + $_.OwningProcess + ' (' + (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName + ')' }"
)

rem ---- disk space on the expo drive ----
for /f %%F in ('powershell -NoProfile -Command "[int]((Get-PSDrive ((Get-Item '%EXPO%').PSDrive.Name)).Free/1GB)"') do set FREEGB=%%F
if !FREEGB! GEQ 5 (
  call :pass "free disk space !FREEGB! GB on the log drive"
) else (
  call :fail "only !FREEGB! GB free - logs and pulls need a few GB"
)

rem ---- PowerShell available for the analyser ----
powershell -NoProfile -Command "exit 0" >nul 2>&1
if errorlevel 1 ( call :fail "PowerShell is not usable - analyse_session.ps1 will not run" ) else ( call :pass "PowerShell available for analyse_session.ps1" )

rem ---- optional: Python (never required) ----
where python >nul 2>&1
if errorlevel 1 (
  echo  [ note ] Python not found - optional extra analysis will be skipped ^(not a failure^)
) else (
  echo  [ note ] Python found - optional Tools\analyse_headset_logs.py will also run
)

rem ---- logs folder writable ----
if not exist "%EXPO%\logs" mkdir "%EXPO%\logs" >nul 2>&1
echo test> "%EXPO%\logs\.writetest" 2>nul
if exist "%EXPO%\logs\.writetest" (
  del "%EXPO%\logs\.writetest" >nul 2>&1
  call :pass "log folder is writable (%EXPO%\logs)"
) else (
  call :fail "cannot write to %EXPO%\logs"
)

rem ---- is a headset already attached? informational only ----
if exist "%ADB%" (
  echo.
  echo  Attached devices right now ^(not required for this check^):
  "%ADB%" devices
)

echo.
if !FAILED!==0 (
  echo  RESULT: ALL CHECKS PASSED - ready for the expo.
  echo  On the day: plug in the headset, then run START_EXPO.bat
) else (
  echo  RESULT: !FAILED! CHECK^(S^) FAILED - see the FAIL lines above.
)
echo.
pause
exit /b !FAILED!

:check_file
if exist %2 ( call :pass "%~1 found" ) else ( call :fail "%~1 MISSING: %~2" )
exit /b 0

:pass
echo  [ PASS ] %~1
exit /b 0

:fail
echo  [ FAIL ] %~1
set /a FAILED+=1
exit /b 0
