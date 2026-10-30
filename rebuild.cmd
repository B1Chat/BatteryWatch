@echo off
setlocal
cd /d "%~dp0"

rem One-shot update: kill running instance, rebuild, relaunch.
rem Kept pure ASCII because cmd.exe parses batch files with the OEM code page.

echo [1/3] Stopping running instance...
taskkill /IM BatteryWatch.exe /F >nul 2>&1
if errorlevel 1 (
    echo       no running instance
) else (
    echo       stopped
)

set "OUTDIR=%~dp0out"
set "SRCDIR=%~dp0src"
set "CSC="
if exist "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not defined CSC (
    echo [ERROR] csc.exe not found.
    pause
    exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo [2/3] Compiling...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:"%OUTDIR%\BatteryWatch.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "%SRCDIR%\HidDevice.cs" "%SRCDIR%\Providers.cs" "%SRCDIR%\IconRenderer.cs" "%SRCDIR%\NativeTray.cs" "%SRCDIR%\TooltipWindow.cs" "%SRCDIR%\TrayApp.cs" "%SRCDIR%\Program.cs"

if errorlevel 1 (
    echo.
    echo [FAILED] Compile error. Copy the messages above and send them back.
    pause
    exit /b 1
)

echo [3/3] Launching...
start "" "%OUTDIR%\BatteryWatch.exe"

echo.
echo Done. Icon is in the taskbar overflow ^^ if this is the first run.
timeout /t 3 >nul
