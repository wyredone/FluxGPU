@echo off
rem Builds FluxGPU.exe with the C# compiler that ships with Windows. No downloads needed.
setlocal
cd /d "%~dp0"
set "FW=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
set REFS=/lib:"%FW%\WPF" /r:PresentationCore.dll /r:PresentationFramework.dll /r:WindowsBase.dll /r:System.Xaml.dll /r:System.dll

echo Generating logo and icon files...
"%FW%\csc.exe" /nologo /nowarn:618 /target:exe /optimize+ /out:brand\BrandGen.exe %REFS% brand\BrandGen.cs || goto :fail
brand\BrandGen.exe brand || goto :fail

echo Building FluxGPU.exe...
"%FW%\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /out:FluxGPU.exe /win32icon:brand\FluxGPU.ico /resource:brand\FluxGPU.ico,FluxGPU.ico /resource:brand\mark.png,mark.png %REFS% FluxGPU.cs || goto :fail

echo Done. Run FluxGPU.exe
exit /b 0

:fail
echo Build failed. If FluxGPU is running, close it and try again.
exit /b 1
