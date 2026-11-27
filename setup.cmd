@echo off
rem FluxGPU setup wizard. Double-click this file, or run: setup.cmd   (options: -Action Repair, -Action Uninstall, -DryRun)
rem It runs tools\Setup-FluxGPU.ps1 with "-ExecutionPolicy Bypass" for this one script only.
rem No system-wide policy is changed, and admin is not needed.
setlocal
set "PSEXE=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
rem From a 32-bit window, use the 64-bit PowerShell so System32 and Program Files are the real ones.
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "PSEXE=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PSEXE%" goto :nops
"%PSEXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Setup-FluxGPU.ps1" -RepoRoot "%~dp0." %*
set "RC=%ERRORLEVEL%"
echo.
pause
exit /b %RC%

:nops
echo Windows PowerShell was not found at "%PSEXE%".
echo It is part of Windows 10 and 11. Run setup again after repairing Windows, or build with build.cmd.
pause
exit /b 1
