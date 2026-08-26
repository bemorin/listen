@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set "LISTEN_BUILD_SCRIPT=%~dp0scripts\build.ps1"
set "LISTEN_INSTALLER_SCRIPT=%~dp0scripts\build-installer.ps1"
set "LISTEN_BUILD_LOG=%~dp0build-windows.log"

> "%LISTEN_BUILD_LOG%" echo Listen Windows build preflight - %date% %time%

rem Parse both PowerShell build scripts before executing either one. This makes
rem syntax/parser failures visible in build-windows.log even when build.ps1
rem itself cannot start far enough to create the log.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$files = @($env:LISTEN_BUILD_SCRIPT, $env:LISTEN_INSTALLER_SCRIPT); $failed = $false; foreach ($file in $files) { $tokens = $null; $errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors); foreach ($err in $errors) { Write-Output ('ParserError {0}:{1}: {2}' -f $file, $err.Extent.StartLineNumber, $err.Message); $failed = $true } }; if ($failed) { exit 1 }" >> "%LISTEN_BUILD_LOG%" 2>&1

if errorlevel 1 (
  echo.
  echo ERREUR : un script de build contient une erreur de syntaxe.
  echo Log : %LISTEN_BUILD_LOG%
  echo.
  type "%LISTEN_BUILD_LOG%"
  echo.
  pause
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%LISTEN_BUILD_SCRIPT%"
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" (
  echo.
  echo Le build a echoue. Ouvre build-windows.log dans ce dossier.
  if exist "%LISTEN_BUILD_LOG%" echo Log : %LISTEN_BUILD_LOG%
  pause
)
exit /b %ERR%
