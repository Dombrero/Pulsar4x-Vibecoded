@echo off
chcp 65001 >nul
title Pulsar4X starten
cd /d "%~dp0"

echo Pulsar4X wird gebaut und gestartet ...
echo (Beim ersten Mal kann das einige Minuten dauern.)
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-pulsar4x.ps1"
set EXITCODE=%ERRORLEVEL%

if not %EXITCODE%==0 (
    echo.
    echo Fehler beim Start ^(Code %EXITCODE%^). Siehe Meldungen oben.
    pause
    exit /b %EXITCODE%
)

exit /b 0
