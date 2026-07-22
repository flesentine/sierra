@echo off
setlocal
cd /d "%~dp0"
if exist "LOGIC_TEST_RESULTS.txt" del /q "LOGIC_TEST_RESULTS.txt"
if not exist "DOSDefragPixel.scr" (
  echo DOSDefragPixel.scr is missing.
  pause
  exit /b 1
)
start "" /wait "%~dp0DOSDefragPixel.scr" /test
if exist "LOGIC_TEST_RESULTS.txt" (
  echo.
  type "LOGIC_TEST_RESULTS.txt"
)
echo.
pause
