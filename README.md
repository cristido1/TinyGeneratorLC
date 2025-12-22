# TinyGenerator

Applicazione web ASP.NET Core per generare storie tramite un pipeline multi-agente con modelli locali Ollama. L'orchestrazione è **LangChain-first**, con componenti legacy di Semantic Kernel mantenuti per compatibilità.

## Panorama rapido

- **Multi-agente**: writer ed evaluator con tool calling ReAct; selezione automatica della storia migliore.
- **Strumenti pronti**: testo, memoria persistente SQLite, HTTP, filesystem, math/time, TTS, AudioCraft e tool TTS schema.
- **UI Razor + SignalR**: progressi in tempo reale, dashboard admin con Bootstrap 5 e DataTables.
- **Persistenza unica**: SQLite per agenti, modelli, storie, log, test e memoria vettoriale.
- **Command dispatcher**: coda asincrona configurabile per generazione, valutazione e TTS.
- **Logging e notifiche**: logger su DB con regole `app_events` e broadcast via SignalR.

## Requisiti

- .NET 10.0 SDK
- SQLite (usato localmente tramite file `data/storage.db`)
- Ollama in esecuzione locale con modelli suggeriti: `phi3:mini-128k`, `llama3.1:8b`, `qwen2.5:7b`, `qwen2.5:3b`, `llama3.2:3b`
- (Opzionale) Server TTS esterno raggiungibile via HTTP

## Setup rapido

1) Clona il repo
```
git clone https://github.com/cristido1/TinyGeneratorLC.git
cd TinyGeneratorLC
```

2) Ripristina i pacchetti
```
dotnet restore
```

3) Avvia Ollama e scarica i modelli
- Windows: `ollama_start.bat`
- Linux/macOS: `./start_ollama.sh`
- Poi esegui `ollama pull` per i modelli indicati sopra.

4) Configura (opzionale) `appsettings.secrets.json` per endpoint personalizzati, chiavi o override TTS.

5) Avvia l'app
```
dotnet run
```
Apri http://localhost:5077.

## Come funziona

### Flusso di generazione
1. L'utente inserisce un tema in `Genera`.
2. Il sistema avvia un comando di generazione tramite `CommandDispatcher`.
3. Ogni agente writer (attivo nel DB) genera una bozza usando tool LangChain.
4. Gli evaluator attivi valutano le bozze e assegnano un punteggio.
5. La storia con punteggio migliore sopra soglia viene salvata (stato "production").
6. SignalR aggiorna in tempo reale UI e log.

### Componenti chiave
- **Services/**: orchestrazione (LangChainKernelFactory, LangChainToolFactory, LangChainAgentService), multi-step pipeline (MultiStepOrchestrationService, ResponseCheckerService), persistenza (DatabaseService), memoria (PersistentMemoryService), logging (CustomLogger), gestione modelli Ollama, TTS.
- **CommandDispatcher**: coda in background con parallelismo configurabile per comandi di generazione/valutazione/TTS.
- **Skills/**: tool richiamabili dagli agenti (text, math, memory, time, filesystem, http, tts api, audiocraft, evaluator, tts schema, story writer, ecc.).
- **Pages/**: Razor Pages per generazione (`Genera`), home (`Index`), amministrazione (`Admin`, `Agents`, `Models`, `Stories`, `Logs`, `Tests`, `Chat`).
- **Hubs/ProgressHub**: aggiornamenti real-time su stato comandi e log.
- **Models/**: entità per agenti, modelli, storie, valutazioni, test, embedding.

### Configurazione
Esempio minimale da `appsettings.json`:
```json
{
  "AppLog": {
    "LogRequestResponse": true,
    "OtherLogs": false
  },
  "CommandDispatcher": {
    "MaxParallelCommands": 2
  },
  "Ollama": {
    "endpoint": "http://localhost:11434"
  }
}
```
Variabili ambiente utili: `TTS_HOST`, `TTS_PORT`, `TTS_TIMEOUT_SECONDS`.

### Database
- File: `data/storage.db` (EF Core + Dapper). Si crea/aggiorna all'avvio se mancano tabelle.
- Tabelle principali: `agents`, `models`, `stories`, `calls`, `logs`, `log_analysis`, `test_definitions`, `tts_voices`, memoria vettoriale.
- Script di popolamento esempio in `populate_*.sql`; usa `sqlite3 data/storage.db < populate_agents.sql` per caricarli.

## Uso dell'app

- **Genera una storia**: vai su `Genera`, inserisci il tema, scegli generazione completa (tutti gli agenti) o un singolo writer, osserva il progresso in tempo reale.
- **Test modelli**: pagina `Models` per eseguire prompt di prova e verificare tool/function calling.
- **Gestione agenti**: pagina `Agents` per abilitare/disabilitare agenti, modificare prompt e skill (JSON nel DB).
- **Log e storie**: pagine `Logs` e `Stories` per consultare output, punteggi e versioni salvate.
- **Chat**: interfaccia chat diretta con un modello selezionato (usa gli stessi connettori).

## Testing e qualità

- Test automatizzati: `dotnet test` (xUnit). La suite include verifiche su tool LangChain, orchestrazione e persistenza.
- Monitoraggio: pagina `Logs` e `ProgressHub` per osservare eventi live; `app_events` governa cosa viene loggato/notificato.

## Best practice di sviluppo

- Preferire l'orchestrazione LangChain per nuove feature; il codice Semantic Kernel resta solo per retrocompatibilità.
- Aggiungi nuovi tool in `Skills/`, registra in `LangChainToolFactory` e, se necessario, esponi in `execution_plans/`.
- Per nuove pipeline multi-step, usa il `CommandDispatcher` e il `MultiStepOrchestrationService` per preservare tracciamento e ripartenza.
- Mantieni le descrizioni dei tool concise; evita parsing ad-hoc delle risposte modello quando puoi usare function/tool calling.
- Usa `ICustomLogger` per log consistenti e evita cicli DI (database logger è asincrono).

## Script utili

- `start_ollama.sh` / `ollama_start.bat`: avvio rapido di Ollama.
- `populate_*.sql`: popolamento dati di esempio (agenti, modelli, step templates, status, voci TTS).
- `scripts/` e `execution_plans/`: piani e prompt base per agenti ReAct.

## Contribuire

1. Apri una branch feature (`feature/nome-feature`).
2. Aggiungi/aggiorna test rilevanti.
3. Apri una pull request descrivendo cambi, impatti e test eseguiti.

## Licenza

MIT
