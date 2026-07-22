@echo off
setlocal
cd /d "%~dp0"
if not exist "DOSDefragPixel.scr" (
  echo DOSDefragPixel.scr is missing.
  pause
  exit /b 1
)
echo Starting DOS Defrag Pixel v8 full screen.
echo Move the mouse or press a key to exit.
start "" /wait "%~dp0DOSDefragPixel.scr" /s
