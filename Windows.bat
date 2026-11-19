@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "EXE=MechanicalKeyboard.exe"
set "CS=MechanicalKeyboard.cs"
set "CSC="

:: Find Windows built-in .NET C# compiler (present on 100% of Windows 7/8/10/11)
if exist "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" (
    set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
) else if exist "%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe" (
    set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

:: Auto-compile if exe is missing or deleted
if not exist "%EXE%" (
    if defined CSC (
        echo [INFO] Preparing Mechanical Keyboard for Windows...
        if exist "app_icon.ico" (
            "%CSC%" /nologo /target:winexe /win32icon:app_icon.ico /out:"%EXE%" /optimize+ /platform:anycpu "%CS%"
        ) else (
            "%CSC%" /nologo /target:winexe /out:"%EXE%" /optimize+ /platform:anycpu "%CS%"
        )
    )
)

:: Launch application
if exist "%EXE%" (
    start "" "%EXE%"
) else (
    echo [ERROR] Could not build or find %EXE%.
    pause
)
