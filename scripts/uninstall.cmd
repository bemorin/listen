@echo off
setlocal EnableExtensions
set "APPDIR=%LOCALAPPDATA%\Programs\Listen"
set "SHORTCUT=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Listen.lnk"

if exist "%APPDIR%\Listen.exe" "%APPDIR%\Listen.exe" --unregister

taskkill /IM Listen.exe /F >nul 2>&1
del /Q "%SHORTCUT%" >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Listen" /f >nul 2>&1

start "" cmd.exe /c "timeout /t 1 /nobreak >nul & rmdir /s /q \"%APPDIR%\""
exit /b 0
