# TinyGenerator

Applicazione web ASP.NET Core (Razor Pages) che orchestra agenti AI per generare storie e asset audio (TTS/ambience/FX/musica) con un loop tipo ReAct (Ollama/OpenAI).

Policy output LLM (runtime):
- Gli agenti devono rispondere con TAG tra parentesi quadre (es. `[TITLE]...[/TITLE]`).
- Non chiediamo JSON agli agenti.
- Tool/function calling lato modello e' disabilitato di default (config `ToolCalling:Enabled=false`).

## Documentazione (entry point)

- Database/tabelle: `readme_tables.md`
- Agenti/ruoli + fallback modelli: `readme_agent_roles.md`
- Risposte/TAG/validazione: `readme_responses.md`
- Config (`appsettings.json`): `readme_appsettings.md`
- CommandDispatcher + comandi: `readme_command_dispatcher.md`
- UI Razor Pages (pagine): `readme_pages.md`

## Requisiti

- .NET SDK (vedi `TinyGenerator.csproj`)
- SQLite (file locale `data/storage.db`)
- Ollama locale con modelli LLM + embeddings (es. `nomic-embed-text`)
- (Opzionale) server TTS HTTP (default `http://127.0.0.1:8004`)
- (Opzionale) AudioCraft API (default `http://localhost:8003`)

## Setup rapido

1) Ripristina pacchetti
```bash
dotnet restore
```

2) Avvia Ollama e scarica i modelli necessari
- Windows: `ollama_start.bat`
- Linux/macOS: `./start_ollama.sh`
- poi: `ollama pull <model>`

3) (Opzionale) configura `appsettings.secrets.json`

4) Avvia l’app
```bash
dotnet run
```
Apri `http://localhost:5077`.

## Note importanti

- **NO EF Core migrations**: lo schema SQLite è gestito via SQL/manual scripts (idempotenti) e codice di “ensure”; non usare `dotnet ef`.
- Le pagine *Index* (griglie/lista) devono seguire lo standard DataTables in `docs/index_page_rules.txt`.

## Mappa rapida del repo

- `Code/`: codice applicativo principale.
- `Code/Services/`: servizi applicativi (orchestrazione, pipeline, integrazioni Ollama/TTS/AudioCraft).
- `Code/Commands/`: comandi eseguibili dal dispatcher.
- `Code/Interfaces/`: contratti/interfacce (`I*`).
- `Code/Options/`: classi di configurazione `*Options`.
- `Skills/`: tool legacy/retrocompatibilita' (tool calling disabilitato di default via config).
- `Pages/`: UI Razor Pages.
- `data/`: SQLite + DbContext.
- `Tests/`: suite xUnit.
- `fx_expert`
- `music_expert`

## Uso dell'app

- **Genera una storia**: pagina `Genera`, tema + modalita' (tutti gli agenti o singolo writer).
- **Test modelli**: pagina `Models` (flussi legacy) per verifiche su tool support quando abilitati esplicitamente.
- **Gestione agenti**: pagina `Agents` per abilitare/disabilitare agenti e modificare prompt/skill.
- **Log e storie**: pagine `Logs` e `Stories` per output, punteggi e versioni salvate.
- **Chat**: interfaccia chat diretta con un modello selezionato.

## Testing e qualita'

- Test automatizzati: `dotnet test` (xUnit).
- Monitoraggio: pagina `Logs` e `ProgressHub` per eventi live.

## Best practice di sviluppo

- Preferire LangChain per nuove feature; Semantic Kernel resta solo per retrocompatibilita'.
- Preferisci contratti di output TAG-only e validazioni deterministiche.
- I tool in `Skills/` sono mantenuti per retrocompatibilita' e test; il runtime di default non li espone ai modelli.
- Per nuove pipeline multi-step, usa `CommandDispatcher` e `MultiStepOrchestrationService`.
- Usa `ICustomLogger` per log consistenti e notifiche realtime.
- Le risposte degli agenti devono avere `Result` valorizzato (`SUCCESS`/`FAILED`) nei log, escluso `response_checker`.

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
