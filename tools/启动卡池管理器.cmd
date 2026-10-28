@echo off
where pythonw >nul 2>nul
if not errorlevel 1 (
  start "" pythonw "%~dp0gacha_manager.py" %*
  exit /b
)
where pyw >nul 2>nul
if not errorlevel 1 (
  start "" pyw -3 "%~dp0gacha_manager.py" %*
  exit /b
)
echo Python with Tk is required. Install Python and add it to PATH.
pause
