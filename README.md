# TinyGenerator

Applicazione web ASP.NET Core per generare storie e asset audio con una pipeline multi-agente LangChain su modelli locali Ollama.

## Panorama rapido

- **Multi-agente**: writer, evaluator e response checker con tool calling ReAct e retry automatici.
- **Multi-step orchestration**: chunking, guardrail sui tool, validazione semantica e coverage per TTS schema.
- **TTS schema**: trascrizione chunk-by-chunk in `tts_schema.json` con timeline, voci, ambience, FX e musica.
- **AudioCraft + TTS**: integrazione opzionale con AudioCraft (MusicGen/AudioGen) e server TTS HTTP.
- **UI Razor + SignalR**: progressi in tempo reale, dashboard admin con Bootstrap 5 e DataTables.
- **Persistenza unica**: SQLite (EF Core + Dapper) per agenti, modelli, storie, test, log e memoria vettoriale.
- **Testing completo**: suite xUnit per validazione ReAct loop, tool schemas, e orchestrazione.

## Requisiti

- .NET 10.0 SDK
- SQLite (file locale `data/storage.db`)
- Ollama in esecuzione locale con modelli LLM e embeddings (es. `phi3:mini-128k`, `llama3.1:8b`, `qwen2.5:7b`, `qwen2.5:3b`, `nomic-embed-text`)
- (Opzionale) Server TTS HTTP (default `http://127.0.0.1:8004`)
- (Opzionale) AudioCraft API (default `http://localhost:8003`)

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
2. Il sistema avvia un comando tramite `CommandDispatcher`.
3. Ogni agente writer attivo genera una bozza usando tool LangChain.
4. Gli evaluator assegnano un punteggio; la storia migliore sopra soglia viene salvata (stato "production").
5. SignalR aggiorna la UI in tempo reale.

### Pipeline TTS
- Lo schema TTS viene prodotto chunk-by-chunk con `MultiStepOrchestrationService`.
- La copertura testuale viene validata prima di procedere al chunk successivo.
- Alla fine viene salvato `tts_schema.json` nella cartella storia.

### Output e storage
- `stories_folder/<id>_*`: contiene `story.txt`, `tts_schema.json` e asset audio generati.
- `stories_folder` e' servita come static files su `/stories_folder`.
- Database unico in `data/storage.db`, con migrazioni EF Core e aggiornamenti Dapper all'avvio.

### Componenti chiave
- **Services/**: orchestrazione LangChain, multi-step pipeline, TTS, AudioCraft, persistenza, logging.
- **CommandDispatcher**: coda in background con parallelismo configurabile per generazione/valutazione/TTS.
- **Skills/**: tool richiamabili dagli agenti (text, memory, http, filesystem, tts, audiocraft, tts schema, evaluator, writer, ecc.).
- **Pages/**: Razor Pages per generazione (`Genera`), home (`Index`), amministrazione (`Admin`, `Agents`, `Models`, `Stories`, `Logs`, `Tests`, `Chat`).
- **Hubs/ProgressHub**: aggiornamenti real-time su stato comandi e log.
- **Models/**: entita' per agenti, modelli, storie, valutazioni, test, embedding.

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
  },
  "Memory": {
    "Embeddings": {
      "Model": "nomic-embed-text:latest"
    }
  }
}
```
Variabili ambiente utili: `TTS_HOST`, `TTS_PORT`, `TTS_TIMEOUT_SECONDS`.

### Database
- File: `data/storage.db` (EF Core + Dapper). Si crea/aggiorna all'avvio se mancano tabelle.
- Tabelle principali: `agents`, `models`, `stories`, `calls`, `logs`, `log_analysis`, `test_definitions`, `tts_voices`, memoria vettoriale.
- Script di popolamento in `populate_*.sql`; usa `sqlite3 data/storage.db < populate_agents.sql` per caricarli.

## Uso dell'app

- **Genera una storia**: pagina `Genera`, tema + modalita' (tutti gli agenti o singolo writer).
- **Test modelli**: pagina `Models` per test function calling e tool support.
- **Gestione agenti**: pagina `Agents` per abilitare/disabilitare agenti e modificare prompt/skill.
- **Log e storie**: pagine `Logs` e `Stories` per output, punteggi e versioni salvate.
- **Chat**: interfaccia chat diretta con un modello selezionato.

## Testing e qualita'

- Test automatizzati: `dotnet test` (xUnit).
- Monitoraggio: pagina `Logs` e `ProgressHub` per eventi live.

## Best practice di sviluppo

- Preferire LangChain per nuove feature; Semantic Kernel resta solo per retrocompatibilita'.
- Aggiungi nuovi tool in `Skills/` e registrali in `LangChainToolFactory`.
- Per nuove pipeline multi-step, usa `CommandDispatcher` e `MultiStepOrchestrationService`.
- Mantieni i tool prompt concisi e sfrutta function/tool calling quando possibile.
- Usa `ICustomLogger` per log consistenti e notifiche realtime.

## Script utili

- `start_ollama.sh` / `ollama_start.bat`: avvio rapido di Ollama.
- `populate_*.sql`: popolamento dati di esempio (agenti, modelli, step templates, voci TTS).
- `scripts/` e `execution_plans/`: piani e prompt base per agenti ReAct.

## Contribuire

1. Apri una branch feature (`feature/nome-feature`).
2. Aggiungi/aggiorna test rilevanti.
3. Apri una pull request descrivendo cambi, impatti e test eseguiti.

## Licenza

MIT
