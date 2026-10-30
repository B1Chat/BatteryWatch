@echo off
setlocal

rem Build script kept pure ASCII on purpose: cmd.exe parses batch files with the
rem OEM code page (GBK on zh-CN), so any UTF-8 Chinese text here turns into
rem garbled commands. Source files are read by csc with /codepage:65001 instead.

set "OUTDIR=%~dp0out"
set "SRCDIR=%~dp0src"

set "CSC="
if exist "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not defined CSC (
    echo [ERROR] csc.exe not found.
    echo         .NET Framework 4.5 or newer is required.
    pause
    exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo Compiling with:
echo   %CSC%
echo.

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:"%OUTDIR%\BatteryWatch.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "%SRCDIR%\HidDevice.cs" "%SRCDIR%\Providers.cs" "%SRCDIR%\IconRenderer.cs" "%SRCDIR%\NativeTray.cs" "%SRCDIR%\TooltipWindow.cs" "%SRCDIR%\TrayApp.cs" "%SRCDIR%\Program.cs"

if errorlevel 1 (
    echo.
    echo [FAILED] Compile error. Copy the messages above and send them back.
    pause
    exit /b 1
)

echo.
echo [OK] Output: %OUTDIR%\BatteryWatch.exe
echo.
echo Note: Windows 11 hides new tray icons by default. Click the ^^ arrow on the
echo       taskbar, then drag the icon onto the taskbar to pin it.
echo.
pause
