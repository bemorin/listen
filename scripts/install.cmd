@echo off
setlocal EnableExtensions
set "APPDIR=%LOCALAPPDATA%\Programs\Listen"
set "STARTMENU=%APPDATA%\Microsoft\Windows\Start Menu\Programs"

rem Close an existing installed/portable Listen process so the binary can be replaced safely.
taskkill /IM Listen.exe /F >nul 2>&1

if not exist "%APPDIR%" mkdir "%APPDIR%" >nul 2>&1
copy /Y "%~dp0Listen.exe" "%APPDIR%\Listen.exe" >nul || exit /b 1
copy /Y "%~dp0LICENSE.md" "%APPDIR%\LICENSE.md" >nul 2>&1
copy /Y "%~dp0README.md" "%APPDIR%\README.md" >nul 2>&1
copy /Y "%~dp0CHANGELOG.md" "%APPDIR%\CHANGELOG.md" >nul 2>&1
copy /Y "%~dp0uninstall.cmd" "%APPDIR%\uninstall.cmd" >nul || exit /b 1

"%APPDIR%\Listen.exe" --register-only

powershell.exe -NoProfile -Command "$ws=New-Object -ComObject WScript.Shell; $s=$ws.CreateShortcut('%STARTMENU%\Listen.lnk'); $s.TargetPath='%APPDIR%\Listen.exe'; $s.WorkingDirectory='%APPDIR%'; $s.IconLocation='%APPDIR%\Listen.exe,0'; $s.Save()" >nul 2>&1

reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Listen" /v DisplayName /t REG_SZ /d "Listen" /f >nul
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Listen" /v DisplayVersion /t REG_SZ /d "__LISTEN_VERSION__" /f >nul
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Listen" /v Publisher /t REG_SZ /d "Minimalist Desktop Apps" /f >nul
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Listen" /v InstallLocation /t REG_SZ /d "%APPDIR%" /f >nul
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Listen" /v DisplayIcon /t REG_SZ /d "%APPDIR%\Listen.exe" /f >nul
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Listen" /v UninstallString /t REG_SZ /d "\"%APPDIR%\uninstall.cmd\"" /f >nul
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Listen" /v NoModify /t REG_DWORD /d 1 /f >nul
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Listen" /v NoRepair /t REG_DWORD /d 1 /f >nul

start "" "%APPDIR%\Listen.exe"
exit /b 0
