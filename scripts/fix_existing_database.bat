@echo off
REM Script per correggere un database esistente con nomi colonne errati (Windows)

echo === Correzione Database Esistente ===
echo.
echo Questo script corregge i nomi delle colonne in un database esistente
echo che e' stato creato con la prima migrazione (nomi PascalCase errati).
echo.

REM 1. Verifica che il database esista
if not exist "data\storage.db" (
    echo [ERRORE] Database non trovato in data\storage.db
    echo            Questo script e' per database esistenti con nomi colonne errati.
    pause
    exit /b 1
)

REM 2. Backup automatico
for /f "tokens=2-4 delims=/ " %%a in ('date /t') do (set mydate=%%c%%b%%a)
for /f "tokens=1-2 delims=/: " %%a in ('time /t') do (set mytime=%%a%%b)
set BACKUP_FILE=data\storage.db.fix-backup-%mydate%-%mytime%

echo 1. Creazione backup --^> %BACKUP_FILE%
copy data\storage.db "%BACKUP_FILE%" >nul
if exist data\storage.db-shm copy data\storage.db-shm "%BACKUP_FILE%-shm" >nul 2>&1
if exist data\storage.db-wal copy data\storage.db-wal "%BACKUP_FILE%-wal" >nul 2>&1
echo    [OK] Backup completato
echo.

REM 3. Verifica migrazioni applicate
echo 2. Verifica migrazioni applicate...
sqlite3 data\storage.db "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;" 2>nul
if errorlevel 1 (
    echo    [WARN] Nessuna migrazione trovata nel database
    echo           Il database potrebbe essere stato creato manualmente.
)
echo.

REM 4. Verifica nomi colonne attuali
echo 3. Verifica nomi colonne attuali (tabella agents)...
sqlite3 data\storage.db "PRAGMA table_info(agents);" 2>nul | findstr /C:"Name" >nul
if %errorlevel%==0 (
    echo    [WARN] Trovate colonne PascalCase es. 'Name' invece di 'name'
    echo           Database necessita correzione!
    set NEEDS_FIX=1
) else (
    sqlite3 data\storage.db "PRAGMA table_info(agents);" 2>nul | findstr /C:"name" >nul
    if %errorlevel%==0 (
        echo    [OK] Colonne gia' corrette snake_case
        echo         Nessuna correzione necessaria.
        set NEEDS_FIX=0
    ) else (
        echo    [WARN] Struttura tabella non riconosciuta
        set NEEDS_FIX=0
    )
)
echo.

if %NEEDS_FIX%==1 (
    echo 4. CORREZIONE RICHIESTA
    echo.
    echo    SQLite richiede la ricreazione delle tabelle per rinominare le colonne.
    echo    STRATEGIA CONSIGLIATA:
    echo.
    echo    OPZIONE A - Mantenere i dati importanti:
    echo    ----------------------------------------
    echo    1. Esporta i dati ^(questo script puo' farlo^)
    echo    2. Elimina data\storage.db
    echo    3. Esegui: dotnet run ^(ricrea DB con struttura corretta^)
    echo    4. Reimporta i dati essenziali
    echo.
    echo    OPZIONE B - Database vuoto o dati di test:
    echo    ------------------------------------------
    echo    1. Elimina data\storage.db
    echo    2. Esegui: dotnet run ^(ricrea tutto automaticamente^)
    echo.
    
    set /p CHOICE="   Vuoi esportare i dati prima della correzione? (S/N): "
    if /i "%CHOICE%"=="S" (
        for /f "tokens=2-4 delims=/ " %%a in ('date /t') do (set mydate=%%c%%b%%a)
        for /f "tokens=1-2 delims=/: " %%a in ('time /t') do (set mytime=%%a%%b)
        set EXPORT_FILE=data\export-%mydate%-%mytime%.sql
        
        echo.
        echo    Generazione export SQL --^> !EXPORT_FILE!
        
        REM Export delle tabelle principali in formato SQL
        echo -- Export database TinyGenerator > "!EXPORT_FILE!"
        echo -- Data: %date% %time% >> "!EXPORT_FILE!"
        echo. >> "!EXPORT_FILE!"
        
        REM Nota: su Windows, l'export richiede sqlite3.exe nella PATH
        where sqlite3 >nul 2>&1
        if errorlevel 1 (
            echo    [ERRORE] sqlite3.exe non trovato nella PATH
            echo             Scaricalo da: https://www.sqlite.org/download.html
            echo             Oppure usa DB Browser for SQLite per esportare manualmente
            pause
            exit /b 1
        )
        
        echo    Esportazione tabelle in corso...
        sqlite3 data\storage.db ".dump agents" >> "!EXPORT_FILE!" 2>nul
        sqlite3 data\storage.db ".dump models" >> "!EXPORT_FILE!" 2>nul
        sqlite3 data\storage.db ".dump stories" >> "!EXPORT_FILE!" 2>nul
        sqlite3 data\storage.db ".dump tts_voices" >> "!EXPORT_FILE!" 2>nul
        sqlite3 data\storage.db ".dump test_definitions" >> "!EXPORT_FILE!" 2>nul
        
        echo    [OK] Export completato: !EXPORT_FILE!
        echo.
        echo    PROSSIMI PASSI MANUALI:
        echo    1. Verifica il contenuto di !EXPORT_FILE!
        echo    2. Elimina data\storage.db
        echo    3. Esegui: dotnet run ^(ricrea DB con struttura corretta^)
        echo    4. Modifica !EXPORT_FILE! per correggere i nomi delle colonne
        echo    5. Esegui: sqlite3 data\storage.db ^< !EXPORT_FILE!
        echo.
    ) else (
        echo.
        echo    ISTRUZIONI MANUALI:
        echo    1. Chiudi l'applicazione se e' in esecuzione
        echo    2. Elimina: data\storage.db ^(il backup e' in %BACKUP_FILE%^)
        echo    3. Esegui: dotnet run
        echo    4. L'applicazione ricreera' il database con la struttura corretta
        echo.
    )
) else (
    echo [OK] Database gia' corretto o non necessita modifiche
    echo.
)

echo Backup disponibile: %BACKUP_FILE%
echo Conservalo fino a quando non hai verificato che tutto funzioni correttamente.
echo.
pause
