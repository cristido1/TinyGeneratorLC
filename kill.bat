@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "PORT=%~1"
if "%PORT%"=="" set "PORT=5077"

set /a COUNT=0

for /f "tokens=5" %%P in ('netstat -ano ^| findstr /R /C:":%PORT% .*LISTENING"') do (
    set "PID=%%P"
    if not defined SEEN_!PID! (
        set "SEEN_!PID!=1"
        echo [INFO] Termino PID !PID! in ascolto su porta %PORT%...
        taskkill /PID !PID! /F >nul 2>&1 && (
            echo [OK] PID !PID! terminato.
        ) || (
            echo [WARN] Impossibile terminare PID !PID!.
        )
        set /a COUNT+=1
    )
)

if %COUNT% EQU 0 (
    echo [INFO] Nessun processo LISTENING trovato sulla porta %PORT%.
    exit /b 0
)

echo [DONE] Processi gestiti: %COUNT%.
exit /b 0
