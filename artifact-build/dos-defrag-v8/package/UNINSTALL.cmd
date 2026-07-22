@echo off
setlocal
set "INSTALL_DIR=%LOCALAPPDATA%\DOS Defrag Pixel"
set "INSTALLED=%INSTALL_DIR%\DOSDefragPixel.scr"

reg query "HKCU\Control Panel\Desktop" /v SCRNSAVE.EXE 2>nul | find /i "%INSTALLED%" >nul
if not errorlevel 1 (
  reg delete "HKCU\Control Panel\Desktop" /v SCRNSAVE.EXE /f >nul 2>&1
  reg add "HKCU\Control Panel\Desktop" /v ScreenSaveActive /t REG_SZ /d 0 /f >nul 2>&1
)

if exist "%INSTALLED%" del /q "%INSTALLED%" >nul 2>&1
rmdir "%INSTALL_DIR%" >nul 2>&1
rundll32.exe user32.dll,UpdatePerUserSystemParameters 1, True >nul 2>&1

echo DOS Defrag Pixel v8 was removed for this Windows account.
pause
