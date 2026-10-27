@echo off
rem ===================================================================
rem  START_EXPO.bat - one-click expo runner with automatic log capture.
rem
rem    START_EXPO.bat            normal run, needs a headset
rem    START_EXPO.bat /dryrun    no headset: fakes one visitor + logs,
rem                              so the scripts can be tested beforehand
rem
rem  Built-in Windows tools only (cmd + PowerShell). Handles paths with
rem  spaces, and survives the headset being unplugged and replugged.
rem ===================================================================
setlocal EnableDelayedExpansion
title BoardroomXR EXPO

set "EXPO=%~dp0"
if "%EXPO:~-1%"=="\" set "EXPO=%EXPO:~0,-1%"
for %%I in ("%EXPO%\..") do set "PROJ=%%~fI"

set "ADB=C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
set "PKG=com.kartikagrawal.boardroomxr"
set "ACTIVITY=com.unity3d.player.UnityPlayerGameActivity"
set "APK=%EXPO%\BoardroomXR_v5.apk"
set "REMOTE=/sdcard/Android/data/%PKG%/files"
set "EXPECTED_VERSION=5.0"

rem ---------- background worker re-entry -------------------------------
rem  The capture loops are this same file re-run with a label as argv[1],
rem  so there is only one script to copy around.
if "%~1"==":worker_logcat"  goto :worker_logcat
if "%~1"==":worker_pull"    goto :worker_pull
if "%~1"==":worker_forward" goto :worker_forward

set DRYRUN=0
if /i "%~1"=="/dryrun" set DRYRUN=1
if /i "%~1"=="-dryrun" set DRYRUN=1

rem ---------- session folder ----------
for /f %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd_HH-mm-ss"') do set "STAMP=%%T"
if %DRYRUN%==1 set "STAMP=DRYRUN_!STAMP!"
set "SESSION=%EXPO%\logs\!STAMP!"
mkdir "!SESSION!" 2>nul
mkdir "!SESSION!\pulled" 2>nul
set "VISITORS=!SESSION!\visitors.csv"
set "LOGCAT=!SESSION!\logcat_full.txt"
set "RUNFLAG=!SESSION!\.running"

echo session_start,%DATE% %TIME%> "!SESSION!\session_info.txt"
echo apk,%APK%>> "!SESSION!\session_info.txt"
echo dryrun,%DRYRUN%>> "!SESSION!\session_info.txt"
if not exist "!VISITORS!" echo timestamp,visitor,scenario,badge,note> "!VISITORS!"

cls
echo.
echo   BoardroomXR - EXPO SESSION
echo   ==========================
echo   session folder : !SESSION!
if %DRYRUN%==1 echo   MODE           : DRY RUN ^(no headset, simulated data^)
echo.

if not exist "%ADB%" (
  echo   adb not found at:
  echo   %ADB%
  echo   Run CHECK_SETUP.bat and fix this first.
  pause
  exit /b 1
)

rem ===================================================================
rem  DRY RUN - simulate a device, one visitor and a logcat file
rem ===================================================================
if %DRYRUN%==1 (
  set "SERIAL=DRYRUN-DEVICE"
  echo   Simulating a headset, one visitor and a log file...
  call :dryrun_fake_logs
  echo   Simulated session written.
  echo.
  goto :menu
)

rem ===================================================================
rem  1. wait for a headset, let the user pick if there are several
rem ===================================================================
echo   Waiting for a headset ^(plug in the USB-C cable, allow USB debugging^)...
"%ADB%" start-server >nul 2>&1
"%ADB%" wait-for-device
if errorlevel 1 (
  echo   adb wait-for-device failed. Unplug and replug the cable, then run this again.
  pause
  exit /b 1
)

call :pick_device
if "!SERIAL!"=="" (
  echo   No device selected. Exiting.
  pause
  exit /b 1
)

for /f "tokens=* delims=" %%M in ('"%ADB%" -s !SERIAL! shell getprop ro.product.model 2^>nul') do set "MODEL=%%M"
echo   Headset : !SERIAL!  ^(!MODEL!^)
echo device,!SERIAL!,!MODEL!>> "!SESSION!\session_info.txt"
echo.

rem ===================================================================
rem  2. install only if the installed versionName differs
rem ===================================================================
set "INSTALLED="
for /f "tokens=2 delims==" %%V in ('"%ADB%" -s !SERIAL! shell dumpsys package %PKG% ^| findstr /r /c:"versionName="') do set "INSTALLED=%%V"
rem strip stray whitespace/CR
for /f "tokens=1" %%V in ("!INSTALLED!") do set "INSTALLED=%%V"

if "!INSTALLED!"=="" (
  echo   App is not installed. Installing %EXPECTED_VERSION% ^(data is not touched^)...
  call :install_apk
) else (
  if /i "!INSTALLED!"=="%EXPECTED_VERSION%" (
    echo   Installed version !INSTALLED! already matches - skipping install, visitor profiles kept.
  ) else (
    echo   Installed version !INSTALLED! differs from %EXPECTED_VERSION% - updating ^(keeping data^)...
    call :install_apk
  )
)
echo.

rem ===================================================================
rem  3. ask once about wiping visitor profiles
rem ===================================================================
choice /c YN /n /m "   Reset all visitor profiles now? (Y/N) "
if errorlevel 2 (
  echo   Keeping existing profiles.
  echo profiles_reset,no>> "!SESSION!\session_info.txt"
) else (
  echo   Wiping signatures, behavioural templates and mimicry recordings...
  "%ADB%" -s !SERIAL! shell am force-stop %PKG% >nul 2>&1
  "%ADB%" -s !SERIAL! shell rm -rf %REMOTE%/BiometricTemplates >nul 2>&1
  "%ADB%" -s !SERIAL! shell rm -rf %REMOTE%/MFAEnrolment >nul 2>&1
  echo   Done - every badge now reads "No profile".
  echo profiles_reset,yes>> "!SESSION!\session_info.txt"
)
echo.

rem ===================================================================
rem  4. background workers: logcat capture, puller, port forward
rem ===================================================================
echo running> "!RUNFLAG!"

echo   Starting background capture windows...
start "BXR logcat" /min cmd /c ""%~f0" :worker_logcat "!SESSION!" "!SERIAL!""
start "BXR puller" /min cmd /c ""%~f0" :worker_pull "!SESSION!" "!SERIAL!""
start "BXR forward" /min cmd /c ""%~f0" :worker_forward "!SESSION!" "!SERIAL!""

rem ===================================================================
rem  5. launch the app and open the dashboard
rem ===================================================================
"%ADB%" -s !SERIAL! shell am start -n %PKG%/%ACTIVITY% >nul 2>&1
echo   App launched on the headset.
timeout /t 3 >nul
start "" http://localhost:8080
echo   Dashboard opened at http://localhost:8080
echo.

rem ===================================================================
rem  menu
rem ===================================================================
:menu
echo  ------------------------------------------------------------------
echo   [V] New visitor      [P] Pull now       [R] Restart app
echo   [D] Reopen dashboard [L] Show log tail  [Q] Quit ^(pull + analyse^)
echo  ------------------------------------------------------------------
choice /c VPRDLQ /n /m "   Choose: "
set MENU=!errorlevel!
if !MENU!==1 goto :new_visitor
if !MENU!==2 goto :pull_now
if !MENU!==3 goto :restart_app
if !MENU!==4 goto :open_dash
if !MENU!==5 goto :log_tail
if !MENU!==6 goto :quit
goto :menu

:new_visitor
echo.
rem Auto-suggest the next label from how many visitors are already logged, so a
rem stray Enter can never produce an unlabelled row.
set "NEXTN="
for /f %%C in ('powershell -NoProfile -Command "try{ @(Import-Csv -LiteralPath '!VISITORS!' ^| Where-Object {$_.visitor}).Count + 1 }catch{ 1 }"') do set "NEXTN=%%C"
if "!NEXTN!"=="" set "NEXTN=1"
if !NEXTN! LSS 10 (set "SUGGEST=V0!NEXTN!") else (set "SUGGEST=V!NEXTN!")

set "VLABEL="
set /p "VLABEL=   Visitor label [!SUGGEST!]: "
if "!VLABEL!"=="" set "VLABEL=!SUGGEST!"

set "VSCEN="
set ASK=0
:ask_scenario
set /a ASK+=1
set /p "VSCEN=   Scenario code S1..S7 (see EXPO_SCENARIOS.md): "
if "!VSCEN!"=="" if !ASK! LSS 3 goto :ask_scenario
if "!VSCEN!"=="" set "VSCEN=S?"

set "VBADGE="
set ASK=0
:ask_badge
set /a ASK+=1
set /p "VBADGE=   Badge used (e.g. EMP-1001): "
if "!VBADGE!"=="" if !ASK! LSS 3 goto :ask_badge
if "!VBADGE!"=="" set "VBADGE=EMP-????"

set "VNOTE="
set /p "VNOTE=   Note (optional, press Enter to skip): "
set "VNOTE=!VNOTE:,=;!"

for /f %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-ddTHH:mm:ss"') do set "NOW=%%T"
echo !NOW!,!VLABEL!,!VSCEN!,!VBADGE!,!VNOTE!>> "!VISITORS!"

rem Marker into logcat too, so logs can be split per visitor afterwards.
if %DRYRUN%==0 (
  "%ADB%" -s !SERIAL! shell log -t BoardroomXR "[Visitor] !VLABEL! scenario=!VSCEN! badge=!VBADGE! note=!VNOTE!" >nul 2>&1
) else (
  echo 01-01 00:00:00.000  0  0 I BoardroomXR: [Visitor] !VLABEL! scenario=!VSCEN! badge=!VBADGE! note=!VNOTE!>> "!LOGCAT!"
)
echo   Marked !VLABEL! / !VSCEN! / !VBADGE! at !NOW!.
echo.
goto :menu

:pull_now
echo.
echo   Pulling data from the headset...
call :do_pull
echo   Pull done.
echo.
goto :menu

:restart_app
echo.
if %DRYRUN%==1 ( echo   [dry run] would restart the app. ) else (
  "%ADB%" -s !SERIAL! shell am force-stop %PKG% >nul 2>&1
  "%ADB%" -s !SERIAL! shell am start -n %PKG%/%ACTIVITY% >nul 2>&1
  echo   App restarted.
)
echo.
goto :menu

:open_dash
if %DRYRUN%==0 "%ADB%" -s !SERIAL! forward tcp:8080 tcp:8080 >nul 2>&1
start "" http://localhost:8080
echo   Dashboard reopened.
echo.
goto :menu

:log_tail
echo.
if exist "!LOGCAT!" (
  powershell -NoProfile -Command "Get-Content -LiteralPath '!LOGCAT!' -Tail 15"
) else (
  echo   No logcat file yet.
)
echo.
goto :menu

:quit
echo.
echo   Finishing up...
del "!RUNFLAG!" 2>nul
echo   Final pull...
call :do_pull

echo   Stopping background capture...
if %DRYRUN%==0 (
  taskkill /f /fi "WINDOWTITLE eq BXR logcat*" >nul 2>&1
  taskkill /f /fi "WINDOWTITLE eq BXR puller*" >nul 2>&1
  taskkill /f /fi "WINDOWTITLE eq BXR forward*" >nul 2>&1
  "%ADB%" -s !SERIAL! forward --remove tcp:8080 >nul 2>&1
)

echo   Running analysis...
powershell -NoProfile -ExecutionPolicy Bypass -File "%EXPO%\analyse_session.ps1" -SessionFolder "!SESSION!"

echo session_end,%DATE% %TIME%>> "!SESSION!\session_info.txt"
echo.
echo   Session saved to:
echo   !SESSION!
echo.
start "" "!SESSION!"
pause
exit /b 0

rem ===================================================================
rem  helpers
rem ===================================================================
:install_apk
if not exist "%APK%" (
  echo   APK missing: %APK%
  echo   Run CHECK_SETUP.bat. Not installing.
  exit /b 1
)
"%ADB%" -s !SERIAL! install -r "%APK%"
if errorlevel 1 (
  echo   INSTALL FAILED. Take a photo of this window.
  pause
)
exit /b 0

:pick_device
set "SERIAL="
set COUNT=0
for /f "skip=1 tokens=1,2" %%A in ('"%ADB%" devices') do (
  if /i "%%B"=="device" (
    set /a COUNT+=1
    set "DEV!COUNT!=%%A"
  )
)
if !COUNT!==0 (
  echo   No authorised device. On the headset accept "Allow USB debugging" ^(tick Always allow^).
  exit /b 0
)
if !COUNT!==1 (
  set "SERIAL=!DEV1!"
  exit /b 0
)
echo   More than one device is connected:
for /l %%I in (1,1,!COUNT!) do (
  for /f "tokens=* delims=" %%M in ('"%ADB%" -s !DEV%%I! shell getprop ro.product.model 2^>nul') do echo     %%I^) !DEV%%I!  ^(%%M^)
)
set "PICK="
set /p "PICK=   Pick a number (1-!COUNT!): "
for /l %%I in (1,1,!COUNT!) do if "!PICK!"=="%%I" set "SERIAL=!DEV%%I!"
exit /b 0

:do_pull
if %DRYRUN%==1 (
  echo   [dry run] skipping adb pull.
  exit /b 0
)
for %%D in (BiometricLogs MFALogs BiometricTemplates MFAEnrolment Telemetry) do (
  "%ADB%" -s !SERIAL! pull "%REMOTE%/%%D" "!SESSION!\pulled" >nul 2>&1
)
rem Catch anything else at the top level (mimicry recordings live inside
rem BiometricTemplates, but pull the whole files dir as a safety net too).
"%ADB%" -s !SERIAL! pull "%REMOTE%" "!SESSION!\pulled\files_all" >nul 2>&1
for /f %%T in ('powershell -NoProfile -Command "Get-Date -Format HH:mm:ss"') do echo pull,%%T>> "!SESSION!\session_info.txt"
exit /b 0

rem ===================================================================
rem  background workers (re-entry points of this same script)
rem ===================================================================
:worker_logcat
title BXR logcat
set "SESSION=%~2"
set "SERIAL=%~3"
set "LOGCAT=%SESSION%\logcat_full.txt"
:logcat_loop
if not exist "%SESSION%\.running" exit /b 0
"%ADB%" -s %SERIAL% wait-for-device >nul 2>&1
echo ==== logcat attached %DATE% %TIME% ====>> "%LOGCAT%"
rem -s filters by tag; Unity writes under "Unity", our markers under "BoardroomXR".
"%ADB%" -s %SERIAL% logcat -v time Unity:V BoardroomXR:V CRASH:E DEBUG:E AndroidRuntime:E ActivityManager:E libc:E *:S >> "%LOGCAT%" 2>nul
echo ==== logcat detached %DATE% %TIME% ====>> "%LOGCAT%"
timeout /t 3 >nul
goto :logcat_loop

:worker_pull
title BXR puller
set "SESSION=%~2"
set "SERIAL=%~3"
set "PKG=com.kartikagrawal.boardroomxr"
set "REMOTE=/sdcard/Android/data/%PKG%/files"
:pull_loop
if not exist "%SESSION%\.running" exit /b 0
timeout /t 60 >nul
if not exist "%SESSION%\.running" exit /b 0
for %%D in (BiometricLogs MFALogs BiometricTemplates MFAEnrolment Telemetry) do (
  "%ADB%" -s %SERIAL% pull "%REMOTE%/%%D" "%SESSION%\pulled" >nul 2>&1
)
for /f %%T in ('powershell -NoProfile -Command "Get-Date -Format HH:mm:ss"') do echo auto-pull %%T>> "%SESSION%\pull_history.txt"
goto :pull_loop

:worker_forward
title BXR forward
set "SESSION=%~2"
set "SERIAL=%~3"
:forward_loop
if not exist "%SESSION%\.running" exit /b 0
"%ADB%" -s %SERIAL% wait-for-device >nul 2>&1
"%ADB%" -s %SERIAL% forward --remove tcp:8080 >nul 2>&1
"%ADB%" -s %SERIAL% forward tcp:8080 tcp:8080 >nul 2>&1
timeout /t 20 >nul
goto :forward_loop

rem ===================================================================
rem  dry-run fixture: a small but realistic logcat + visitor row
rem ===================================================================
:dryrun_fake_logs
> "!LOGCAT!" echo ==== logcat attached (DRY RUN) ====
>> "!LOGCAT!" echo 01-01 10:00:00.000  1  1 I BoardroomXR: [Visitor] V01 scenario=S1 badge=EMP-1001 note=dry run genuine
>> "!LOGCAT!" echo 01-01 10:00:05.100  1  1 I Unity   : [BioEvt] t=5.10 type=identity_switch user=EMP-1001 detail=from=EMP-0000 name=A. Mehta role=CEO published=True serverSees=EMP-1001
>> "!LOGCAT!" echo 01-01 10:00:21.400  1  1 I Unity   : [BioEvt] t=21.40 type=gesture_enrol user=EMP-1001 detail=strokes=3 points=58
>> "!LOGCAT!" echo 01-01 10:00:29.900  1  1 I Unity   : [BioEvt] t=29.90 type=gesture_pass user=EMP-1001 detail=score=0.812 matchedSample=1
>> "!LOGCAT!" echo 01-01 10:00:36.200  1  1 I Unity   : [BioEvt] t=36.20 type=mfa_pass user=EMP-1001 detail=all three factors in 31.1s
>> "!LOGCAT!" echo 01-01 10:00:36.500  1  1 I Unity   : [BioEvt] t=36.50 type=enrol_start user=EMP-1001 detail=30s preset=Balanced (0.15, k=6)
>> "!LOGCAT!" echo 01-01 10:01:07.000  1  1 I Unity   : [BioEvt] t=67.00 type=enrol_done user=EMP-1001 detail=windows=116 preset=Balanced (0.15, k=6)
>> "!LOGCAT!" echo 01-01 10:01:07.500  1  1 I Unity   : [BioWin] user=EMP-1001 t=0.50 state=Verified raw=0.8912 smoothed=0.8912 viol=0 preset=Balanced thr=0.15 k=6 top=head_pitch_mean topz=0.41
>> "!LOGCAT!" echo 01-01 10:01:08.000  1  1 I Unity   : [BioWin] user=EMP-1001 t=1.00 state=Verified raw=0.8740 smoothed=0.8852 viol=0 preset=Balanced thr=0.15 k=6 top=head_speed_mean topz=0.55
>> "!LOGCAT!" echo 01-01 10:02:00.000  1  1 I BoardroomXR: [Visitor] V02 scenario=S2 badge=EMP-1001 note=dry run handover
>> "!LOGCAT!" echo 01-01 10:02:02.000  1  1 I Unity   : [BioWin] user=EMP-1001 t=55.00 state=Warning raw=0.0320 smoothed=0.1410 viol=1 preset=Balanced thr=0.15 k=6 top=head_height_mean topz=-4.80
>> "!LOGCAT!" echo 01-01 10:02:04.500  1  1 I Unity   : [BioWin] user=EMP-1001 t=57.50 state=Warning raw=0.0210 smoothed=0.0620 viol=5 preset=Balanced thr=0.15 k=6 top=head_height_mean topz=-5.10
>> "!LOGCAT!" echo 01-01 10:02:06.100  1  1 I Unity   : [BioEvt] t=126.10 type=lockout user=EMP-1001 detail=score=0.041 threshold=0.15 k=6 preset=Balanced top=head_height_mean
>> "!LOGCAT!" echo 01-01 10:02:06.300  1  1 I Unity   : [Biometric] IMPOSTOR LOCKOUT for EMP-1001
>> "!LOGCAT!" echo 01-01 10:03:00.000  1  1 I BoardroomXR: [Visitor] V03 scenario=S6 badge=EMP-1002 note=dry run wrong pin
>> "!LOGCAT!" echo 01-01 10:03:04.000  1  1 I Unity   : [BioEvt] t=184.00 type=gesture_fail user=EMP-1002 detail=score=0.312 required=0.650
for /f %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-ddTHH:mm:ss"') do set "NOW=%%T"
echo !NOW!,V01,S1,EMP-1001,dry run genuine>> "!VISITORS!"
echo !NOW!,V02,S2,EMP-1001,dry run handover>> "!VISITORS!"
echo !NOW!,V03,S6,EMP-1002,dry run wrong pin>> "!VISITORS!"
mkdir "!SESSION!\pulled\BiometricLogs" 2>nul
> "!SESSION!\pulled\BiometricLogs\scores_EMP-1001_enrol_dryrun.csv" echo t,state,raw_score,smoothed_score,violations,head_speed_mean
>> "!SESSION!\pulled\BiometricLogs\scores_EMP-1001_enrol_dryrun.csv" echo 0.500,Verified,0.8912,0.8912,0,0.14210
>> "!SESSION!\pulled\BiometricLogs\scores_EMP-1001_enrol_dryrun.csv" echo 1.000,Verified,0.8740,0.8852,0,0.13980
>> "!SESSION!\pulled\BiometricLogs\scores_EMP-1001_enrol_dryrun.csv" echo 55.000,Warning,0.0320,0.1410,1,0.41200
mkdir "!SESSION!\pulled\BiometricTemplates" 2>nul
> "!SESSION!\pulled\BiometricTemplates\template_EMP-1001.json" echo {"userId":"EMP-1001","windowCount":116,"adaptationCount":4}
> "!SESSION!\pulled\BiometricTemplates\mimicry_EMP-1001.json" echo {"userId":"EMP-1001","hz":30.0,"frames":[]}
mkdir "!SESSION!\pulled\MFAEnrolment" 2>nul
> "!SESSION!\pulled\MFAEnrolment\gesture_EMP-1001.json" echo {"userId":"EMP-1001","samples":[]}
mkdir "!SESSION!\pulled\MFALogs" 2>nul
> "!SESSION!\pulled\MFALogs\session_dryrun.log" echo [dry run] session log placeholder
exit /b 0
