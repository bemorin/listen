@echo off
"%~dp0Listen.exe" --unregister
if errorlevel 1 (
  echo Impossible de retirer les integrations Explorer.
  pause
  exit /b 1
)
echo Integrations Explorer retirees.
timeout /t 2 >nul
