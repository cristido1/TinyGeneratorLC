# Setup TinyGenerator su un nuovo PC

## Prima esecuzione

Quando cloni il progetto per la prima volta, l'applicazione si occupa automaticamente di creare e configurare il database:

### Processo automatico di inizializzazione

1. **Creazione database**: Se `data/storage.db` non esiste:
   - EF Core lo crea automaticamente
   - Viene applicato lo schema da `data/db_schema.sql` (se presente)
   - In alternativa, `InitializeSchema()` crea tutte le tabelle necessarie

2. **Applicazione migrazioni EF Core**: All'avvio in `Program.cs`
   ```csharp
   dbContext.Database.Migrate();
   ```
   - Applica automaticamente tutte le migrazioni pending
   - Registra le migrazioni applicate in `__EFMigrationsHistory`
   - Non modifica dati esistenti, solo lo schema

3. **Inizializzazione schema Dapper**: `DatabaseService.InitializeSchema()`
   - Crea tabelle non mappate in EF Core (`chapters`, `model_test_runs`, `usage_state`, ecc.)
   - Esegue migrazioni incrementali per colonne mancanti
   - Seed di dati iniziali (task_types, ecc.)

4. **Popolazione dati iniziali**: `StartupTasks`
   - Scoperta modelli Ollama locali (se disponibili)
   - Seed voci TTS dal servizio esterno
   - Normalizzazione test prompts

### Requisiti

- .NET 10.0 SDK
- SQLite (incluso)
- (Opzionale) Ollama installato localmente per i modelli AI
- (Opzionale) Servizio TTS esterno per le voci

### Primo avvio

```bash
dotnet restore
dotnet build
dotnet run
```

L'applicazione sarà disponibile su `https://localhost:5001` (o la porta configurata).

### Verifica inizializzazione

Controlla i log all'avvio:
- `[Startup] Applying EF Core migrations...` → Migrazioni EF Core
- `[DB] Initialize() called` → Inizializzazione schema Dapper
- `[DB] Creating <table> table` → Tabelle create
- `[Startup] Models table empty — attempting to populate...` → Seed modelli

### Database esistente

Se il database esiste già (es. dopo un aggiornamento):
- Le migrazioni EF Core vengono applicate solo se necessario
- Lo schema Dapper viene verificato e aggiornato incrementalmente
- Nessun dato viene perso (le migrazioni sono additive)

### Backup automatico

Prima di modifiche importanti, l'applicazione crea backup:
- `data/storage.db.backup-YYYYMMDD-HHMMSS`

### Troubleshooting

**Database corrotto:**
```bash
# Elimina e ricrea
rm data/storage.db*
dotnet run
```

**Migrazioni non applicate:**
```bash
# Verifica migrazioni
dotnet ef migrations list

# Applica manualmente
dotnet ef database update
```

**Schema mancante:**
Se `db_schema.sql` è assente, l'app crea comunque tutte le tabelle via codice in `InitializeSchema()`.
