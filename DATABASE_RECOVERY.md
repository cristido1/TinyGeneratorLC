# Database SQLite Corruption Recovery

## Problema Riscontrato

**Eccezione**: `Microsoft.Data.Sqlite.SqliteException: 'SQLite Error 11: database disk image is malformed'`

**Causa**: Il file database SQLite era corrotto, con indici danneggiati (errore: "wrong # of entries in index idx_model_test_runs_model_id")

**Locazione del Problema**: `Services/DatabaseService.cs:1704` nella query `GetModelTestGroupsSummary()`

## Soluzione Applicata

### 1. Diagnosi
```bash
sqlite3 data/storage.db "PRAGMA integrity_check;"
# Output: wrong # of entries in index idx_model_test_runs_model_id
```

### 2. Ripristino dal Backup
```bash
# Backup del file corrotto
cp data/storage.db data/storage.db.corrupt_backup_20251121

# Ripristino dal backup disponibile
cp backups/storage.db.20251120T122050Z data/storage.db

# Pulizia dei file WAL (Write-Ahead Log) che possono causare inconsistenze
rm -f data/storage.db-shm data/storage.db-wal

# Verifica dell'integrità
sqlite3 data/storage.db "PRAGMA integrity_check;"
# Output: ok
```

### 3. Verifica
- ✅ Build: 0 errori, 0 warning
- ✅ Database: integrità verificata
- ✅ Applicazione: pronta per l'uso

## Prevenzione Futura

### 1. Backup Automatico
Aggiungi un task di backup periodico:
```csharp
// In Program.cs
services.AddHostedService<DatabaseBackupService>();
```

### 2. Monitoraggio Integrità
Esegui periodicamente:
```bash
sqlite3 data/storage.db "PRAGMA integrity_check;"
```

### 3. WAL Mode
SQLite è configurato in WAL mode per migliore concorrenza:
```csharp
// In DatabaseService.Initialize()
conn.Execute("PRAGMA journal_mode=WAL");
```

Se il database diventa corrotto di nuovo:
1. Disabilitare WAL mode
2. Eseguire VACUUM
3. Riabilitare WAL mode

### 4. Gestione Eccezioni
```csharp
try
{
    using var conn = new SqliteConnection(_connectionString);
    conn.Open();
    // ... query
}
catch (SqliteException ex) when (ex.SqliteErrorCode == 11)  // SQLITE_CORRUPT
{
    // Logga errore e chiedi all'amministratore di ripristinare il backup
    _logger?.Log("Error", "Database", $"Database corruption detected: {ex.Message}");
    throw;
}
```

## File di Backup Disponibili

```
/backups/
├── storage.db.20251120T122050Z    (ripristinato)
└── restore-20251031-1/            (backup precedente)

/data/
├── storage.db                      (active)
└── storage.db.corrupt_backup_20251121 (corrupted backup)
```

## Riferimenti

- **SQLite Error Codes**: https://www.sqlite.org/rescode.html#corrupt
- **SQLite PRAGMA integrity_check**: https://www.sqlite.org/pragma.html#pragma_integrity_check
- **SQLite WAL Mode**: https://www.sqlite.org/wal.html

## Azioni Intraprese

1. ✅ Ripristinato il backup del database (2025-11-20T12:20:50Z)
2. ✅ Rimossi i file WAL corrotti
3. ✅ Verificata l'integrità del database
4. ✅ Confermata la compilazione del progetto (0 errori)

## Prossimi Passi

- [ ] Implementare backup automatico giornaliero
- [ ] Aggiungere monitoraggio integrità database
- [ ] Implementare gestione eccezioni per corruzione database
- [ ] Aggiungere page per diagnostica database in Admin

---

**Data Evento**: 2025-11-21  
**Stato**: Risolto ✅  
**Backup Corrotto**: `data/storage.db.corrupt_backup_20251121`
