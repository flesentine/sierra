@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title DOS Defrag Pixel v8 Installer

set "SCR=%~dp0DOSDefragPixel.scr"
set "INSTALL_DIR=%LOCALAPPDATA%\DOS Defrag Pixel"
set "INSTALLED=%INSTALL_DIR%\DOSDefragPixel.scr"
set "LOG=%~dp0INSTALL_LOG.txt"

>"%LOG%" echo DOS Defrag Pixel v8 installer
>>"%LOG%" echo Started: %DATE% %TIME%
>>"%LOG%" echo Source: %SCR%
>>"%LOG%" echo Destination: %INSTALLED%

if not exist "%SCR%" (
  echo ERROR: DOSDefragPixel.scr is missing.
  >>"%LOG%" echo ERROR: prebuilt screen saver missing.
  goto :failed
)

if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%" >>"%LOG%" 2>&1
copy /y "%SCR%" "%INSTALLED%" >>"%LOG%" 2>&1
if errorlevel 1 (
  echo ERROR: Could not copy the screen saver.
  goto :failed
)

reg add "HKCU\Control Panel\Desktop" /v SCRNSAVE.EXE /t REG_SZ /d "%INSTALLED%" /f >>"%LOG%" 2>&1
if errorlevel 1 goto :failed
reg add "HKCU\Control Panel\Desktop" /v ScreenSaveActive /t REG_SZ /d 1 /f >>"%LOG%" 2>&1
reg query "HKCU\Control Panel\Desktop" /v ScreenSaveTimeOut >>"%LOG%" 2>&1
if errorlevel 1 reg add "HKCU\Control Panel\Desktop" /v ScreenSaveTimeOut /t REG_SZ /d 600 /f >>"%LOG%" 2>&1

rem Ask the classic Screen Saver control panel to refresh the selected module.
rundll32.exe user32.dll,UpdatePerUserSystemParameters 1, True >>"%LOG%" 2>&1

echo.
echo DOS Defrag Pixel v8 installed successfully.
echo.
echo Installed to:
echo   %INSTALLED%
echo.
echo Opening Screen Saver Settings...
start "" control.exe desk.cpl,,1
echo.
echo Use TEST_FULL_SCREEN.cmd for an immediate full-screen test.
echo.
pause
exit /b 0

:failed
echo.
echo Installation failed.
echo Open INSTALL_LOG.txt in this folder for details.
echo.
pause
exit /b 1
