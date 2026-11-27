@echo off
rem FluxGPU 0.2.0 build script. Uses the C# 5 compiler that ships with Windows (.NET Framework 4.x). No downloads.
rem
rem   build.cmd          Build FluxGPU.exe. The committed logo and icon files in brand\ are used as they are.
rem   build.cmd test     Build and run the pure-logic tests (tests\FluxGPU.Tests.cs).
rem   build.cmd brand    Regenerate the logo and icon files in brand\ (overwrites them), then build FluxGPU.exe.
rem
rem Warnings are treated as errors. If the in-box compiler on a PC reports a warning that blocks the build,
rem run "set FLUXGPU_ALLOW_WARNINGS=1" first and report the warning.
setlocal
cd /d "%~dp0"
set "FW=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" goto :nocsc
set REFS=/lib:"%FW%\WPF" /r:PresentationCore.dll /r:PresentationFramework.dll /r:WindowsBase.dll /r:System.Xaml.dll /r:System.dll
set "WARN=/warnaserror+"
if "%FLUXGPU_ALLOW_WARNINGS%"=="1" set "WARN="

if /i "%~1"=="test" goto :test
if /i "%~1"=="brand" goto :brand
goto :app

:brand
echo Regenerating logo and icon files in brand\ ...
"%FW%\csc.exe" /nologo /nowarn:618 /target:exe /optimize+ /out:brand\BrandGen.exe %REFS% brand\BrandGen.cs || goto :fail
brand\BrandGen.exe brand || goto :fail

:app
echo Building FluxGPU.exe ...
"%FW%\csc.exe" /nologo %WARN% /target:winexe /platform:x64 /optimize+ /out:FluxGPU.exe /win32icon:brand\FluxGPU.ico /resource:brand\FluxGPU.ico,FluxGPU.ico /resource:brand\mark.png,mark.png %REFS% FluxGPU.cs || goto :fail
echo Done. Run FluxGPU.exe
exit /b 0

:test
echo Building tests\FluxGPU.Tests.exe ...
"%FW%\csc.exe" /nologo %WARN% /target:exe /platform:x64 /optimize+ /main:GpuMonitor.Tests.TestMain /out:tests\FluxGPU.Tests.exe %REFS% FluxGPU.cs tests\FluxGPU.Tests.cs || goto :fail
tests\FluxGPU.Tests.exe
if errorlevel 1 goto :testfail
exit /b 0

:testfail
echo One or more tests failed. See the FAIL lines above.
exit /b 1

:nocsc
echo The C# compiler was not found at "%FW%\csc.exe".
echo FluxGPU needs 64-bit Windows with .NET Framework 4.x, which is included in Windows 10 and 11.
exit /b 1

:fail
echo Build failed. If FluxGPU is running, close it and try again.
exit /b 1
