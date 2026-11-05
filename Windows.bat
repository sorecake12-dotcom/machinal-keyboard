@echo off
setlocal
cd /d "%~dp0"
if exist "AulaF75KeySound.exe" (
    start "" "AulaF75KeySound.exe"
) else (
    echo Error: AulaF75KeySound.exe not found.
    pause
)
