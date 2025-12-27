# Auto-Summarization - Implementazione Completa

## Componenti Implementati

### 1. Pulsante UI nella Pagina Stories
**File:** `Pages/Stories/Index.cshtml`

**Posizione:** Toolbar superiore, tra "Test TTS" e "Elimina storie scadenti"

**Funzionalità:**
- Bottone "🗒️ Riassunti Batch"
- Colore: outline-info (azzurro)
- Conferma utente prima dell'esecuzione
- Invoca handler `OnPostBatchSummarize`

### 2. Handler POST nel Code-Behind
**File:** `Pages/Stories/Index.cshtml.cs`

**Metodo:** `OnPostBatchSummarize()`

**Funzionalità:**
- Crea istanza di `BatchSummarizeStoriesCommand`
- Accoda nel dispatcher con priority 2
- Metadata: `triggeredBy: "manual_ui"`
- Mostra messaggio di successo in TempData

### 3. Servizio Background Automatico
**File:** `Services/AutoSummarizeService.cs`

**Classe:** `AutoSummarizeService : BackgroundService`

**Comportamento:**
1. **Startup (dopo 30 secondi):**
   - Attende 30s per dare tempo al sistema di inizializzarsi
   - Esegue prima batch summarization
   - Metadata: `triggeredBy: "auto_startup"`

2. **Ogni Ora:**
   - Timer con intervallo di 1 ora
   - Esegue batch summarization periodica
   - Metadata: `triggeredBy: "auto_scheduled"`

**Registrazione:** In `Program.cs` tramite `AddHostedService<AutoSummarizeService>()`

## Flusso di Esecuzione

### Trigger Manuale (UI)
```
1. Utente clicca "Riassunti Batch"
   ↓
2. Conferma dialog
   ↓
3. POST /Stories?handler=BatchSummarize
   ↓
4. OnPostBatchSummarize() accoda comando
   ↓
5. TempData: "Batch summarization avviato..."
   ↓
6. Redirect alla pagina Stories
   ↓
7. Batch command esegue in background
```

### Trigger Automatico (Startup)
```
1. App avviata
   ↓
2. AutoSummarizeService.ExecuteAsync() inizia
   ↓
3. Attesa 30 secondi
   ↓
4. RunBatchSummarizeAsync("startup")
   ↓
5. Batch command accodato con priority 2
   ↓
6. Log: "Batch summarization enqueued (runId: xxx, trigger: startup)"
```

### Trigger Automatico (Ogni Ora)
```
1. Timer scatta dopo 1 ora
   ↓
2. RunBatchSummarizeAsync("scheduled")
   ↓
3. Batch command accodato con priority 2
   ↓
4. Log: "Batch summarization enqueued (runId: xxx, trigger: scheduled)"
   ↓
5. Timer riparte per prossima ora
```

## Metadata per Tracking

### Trigger Manuale (UI)
```json
{
  "minScore": "60",
  "agentName": "batch_orchestrator",
  "operation": "batch_summarize",
  "triggeredBy": "manual_ui"
}
```

### Trigger Automatico (Startup)
```json
{
  "minScore": "60",
  "agentName": "batch_orchestrator",
  "operation": "batch_summarize",
  "triggeredBy": "auto_startup"
}
```

### Trigger Automatico (Scheduled)
```json
{
  "minScore": "60",
  "agentName": "batch_orchestrator",
  "operation": "batch_summarize",
  "triggeredBy": "auto_scheduled"
}
```

## Priorità dei Comandi

```
Priority 2: BatchSummarizeStories (orchestrator)
            ↓ accoda N comandi ↓
Priority 3: SummarizeStory (per ogni storia)
```

**Comportamento:**
- Orchestrator ha priority 2 (normale) → esegue subito quando sistema libero
- Singoli riassunti hanno priority 3 (bassa) → non interferiscono con operazioni critiche

## Log di Esempio

### Startup
```
[AutoSummarizeService] AutoSummarizeService started
[AutoSummarizeService] Running batch summarization (trigger: startup)
[AutoSummarizeService] Batch summarization enqueued (runId: abc123, trigger: startup)
[BatchSummarize] Starting batch summarization (min score: 60)
[BatchSummarize] Found 12 stories eligible for summarization
[BatchSummarize] Enqueued summarization for story 456 (score: 75.50)
[BatchSummarize] Enqueued summarization for story 789 (score: 82.30)
...
[BatchSummarize] Enqueued 12 summarization commands
```

### Scheduled (Ogni Ora)
```
[AutoSummarizeService] Running batch summarization (trigger: scheduled)
[AutoSummarizeService] Batch summarization enqueued (runId: def456, trigger: scheduled)
[BatchSummarize] Starting batch summarization (min score: 60)
[BatchSummarize] Found 3 stories eligible for summarization
[BatchSummarize] Enqueued 3 summarization commands
```

### UI Manual
```
[BatchSummarize] Starting batch summarization (min score: 60)
[BatchSummarize] Found 5 stories eligible for summarization
[BatchSummarize] Enqueued summarization for story 101 (score: 68.00)
...
[BatchSummarize] Enqueued 5 summarization commands
```

## Gestione Errori

### Errore in AutoSummarizeService
```csharp
try {
    // ... esecuzione ...
} catch (Exception ex) {
    _logger.LogError(ex, "Failed to enqueue batch summarization (trigger: {Trigger})", trigger);
}
// Il servizio continua comunque - prossimo tentativo tra 1 ora
```

### Errore in UI Handler
```csharp
try {
    // ... esecuzione ...
} catch (Exception ex) {
    TempData["ErrorMessage"] = "Errore durante avvio batch summarization: " + ex.Message;
}
return RedirectToPage();
```

## Configurazione

### Intervallo Timer (hardcoded)
```csharp
private readonly TimeSpan _interval = TimeSpan.FromHours(1);
```

**Per modificare:**
- Aprire `Services/AutoSummarizeService.cs`
- Modificare `_interval` con nuovo valore
- Esempi:
  - `TimeSpan.FromMinutes(30)` → ogni 30 minuti
  - `TimeSpan.FromHours(2)` → ogni 2 ore
  - `TimeSpan.FromDays(1)` → una volta al giorno

### Delay Startup (hardcoded)
```csharp
await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
```

**Per modificare:**
- Aumentare per dare più tempo all'inizializzazione
- Ridurre per esecuzione più rapida al startup

### MinScore (fisso a 60)
Attualmente tutti i trigger usano `minScore: 60`. Per personalizzare:
- Modificare parametro in ogni chiamata a `BatchSummarizeStoriesCommand`
- Oppure leggere da configurazione (appsettings.json)

## Test

### 1. Test Manuale UI
```
1. Avvia app: dotnet run
2. Vai a http://localhost:5000/Stories
3. Clicca "Riassunti Batch"
4. Conferma
5. Verifica messaggio: "Batch summarization avviato..."
6. Osserva popup "Comandi in esecuzione"
7. Verifica comandi SummarizeStory accodati
```

### 2. Test Startup Automatico
```
1. Assicurati di avere storie con score >= 60 senza summary
2. Avvia app: dotnet run
3. Attendi 30 secondi
4. Controlla log console:
   - "AutoSummarizeService started"
   - "Running batch summarization (trigger: startup)"
   - "Enqueued X summarization commands"
5. Verifica popup mostra comandi
```

### 3. Test Timer (Ogni Ora)
```
1. Avvia app
2. Attendi 1 ora e 30 secondi
3. Controlla log console:
   - "Running batch summarization (trigger: scheduled)"
4. Oppure modifica intervallo a 1 minuto per test veloce
```

### 4. Test con Python Script
```bash
# Batch manuale via API
python test_summarizer.py --batch

# Singola storia
python test_summarizer.py 123
```

## Vantaggi dell'Implementazione

✅ **Automazione Completa:** Riassunti generati automaticamente senza intervento
✅ **Flessibilità:** 3 modi per lanciare (UI, startup, scheduled)
✅ **Non Invasivo:** Priority 3 = non interferisce con operazioni critiche
✅ **Tracciabile:** Metadata `triggeredBy` permette di identificare origine
✅ **Resiliente:** Errori non bloccano servizio, prossimo tentativo continua
✅ **Configurabile:** Intervallo e minScore facilmente modificabili
✅ **User-Friendly:** Pulsante UI chiaro con conferma
