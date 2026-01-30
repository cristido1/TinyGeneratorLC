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

### Narrative Engine (state-driven, chunk continuo)
Il progetto include una modalita' **state-driven** per generare una storia a chunk continui guidata da uno stato runtime e da un profilo narrativo (genere/regole).

- **Profili e regole (DB)**: `narrative_profiles`, `narrative_resources`, `micro_objectives`, `failure_rules`, `consequence_rules`, `consequence_impacts`.
- **Stato runtime (DB)**: `story_runtime_states` + `story_resource_states`.
- **Persistenza chunk**: ogni chunk viene salvato come riga in `chapters` (numero 1-based).
- **Single active runtime**: all'avvio di una nuova storia state-driven, tutti i runtime precedenti vengono disattivati.

**API essenziali**

- `POST /api/commands/state-story/start`
  - Body: `{ "theme": "...", "title": "...", "narrativeProfileId": 1, "seriesId": null, "episodeId": null, "plannerMode": "Off|Assist|Auto" }`
  - Risposta: `storyId`
- `POST /api/commands/state-story/next-chunk`
  - Body: `{ "storyId": 123, "writerAgentId": 5 }`
  - Risposta: `runId` (CommandDispatcher), `storyId`

**Comportamento attuale (codice)**

- **Phase**: ciclo base su 5 chunk (`Action`, `Action`, `Action`, `Stall`, `Error`) con override numerici a `Consequence` quando `FailureCount >= 3` o quando una risorsa arriva al minimo.
- **POV**: ruota su `NarrativeProfile.pov_list_json` (array JSON) se valorizzato; altrimenti usa `ThirdPersonLimited` o mantiene il POV corrente.
- **Risorse**: drain deterministico per fase (`Stall/Error = -1`, `Consequence = -2`). In fase `Consequence` applica anche un set di impatti selezionato in modo deterministico.
- **Validator cliffhanger**: il chunk deve terminare con tensione aperta (es. `...`, `?`, `!`, `—`, `:`). Se fallisce, retry fino a 3 tentativi; se fallisce comunque incrementa `FailureCount` e salva comunque il chunk.

Nota: `base_system_prompt` e `style_prompt` sono persistiti nel profilo (e seedati), ma l'attuale `PromptBuilder` del comando usa soprattutto **tema** + **coda contesto**; POV/risorse/conseguenze sono invece gia' attive.

### Serie (tabelle `series`, `series_episodes`, `series_characters`)
Questa sezione descrive **tutti i campi configurabili** della serie e come influenzano la generazione.

#### Tabella `series`
Campi principali e effetto:

- `titolo` (obbligatorio): usato nei prompt di generazione episodio e nell'annuncio TTS iniziale (se presente `serie_id` sulla storia).
- `genere`, `sottogenere`, `periodo_narrativo`, `tono_base`, `target`, `lingua`: inseriti nel **contesto serie** passato al writer; influenzano stile e coerenza del testo.
- `ambientazione_base`: entra nel prompt come ambientazione persistente della serie.
- `premessa_serie`: entra nel prompt come premessa/incipit della serie.
- `arco_narrativo_serie`: entra nel prompt come arco narrativo globale.
- `stile_scrittura`: entra nel prompt come istruzioni di stile.
- `regole_narrative`: entra nel prompt come vincoli/regole.
- `serie_final_goal`: entra nel prompt come obiettivo finale della serie.
- `note_ai`: appendice libera al prompt (note operative).
- `images_style`: usato per **generazione immagini personaggi**; se presente viene prefissato alla descrizione del personaggio per il prompt immagini.
- `folder`: se impostato, abilita asset di serie su filesystem:
  - musica di serie in `series_folder/<folder>/music` (usata per assegnazione musica in `tts_schema.json`);
  - immagini personaggi in `series_folder/<folder>/images_characters`.
- `planner_method_id` (FK `planner_methods`): aggiunto al contesto serie e passato come config al multi-step writer (pianificazione strategica).
- `default_tipo_planning_id` (FK `tipo_planning`): usato come **planning tattico** per gli episodi (successione stati) quando non c'e' override a livello episodio.
- `default_narrative_profile_id` (FK `narrative_profiles`): usato nella **generazione state-driven** per scegliere il profilo narrativo.
- `default_planner_mode`: usato nella **generazione state-driven** e salvato in `stories.planner_mode` (`Off` | `Assist` | `Auto`). Attualmente e' persistito ma non modifica il prompt in modo diretto.
- `narrative_consistency_level`: presente a livello schema ma **al momento non e' usato** nella generazione.
- `episodi_generati`: contatore incrementato dopo generazione episodio classica (non state-driven).
- `data_inserimento`, `timestamp`: metadati e concurrency token.

#### Tabella `series_episodes`
Ogni episodio della serie (1..N).

- `serie_id` (FK): collega l'episodio alla serie.
- `number`: numero episodio; usato nei prompt e nel titolo di default.
- `title`: se presente, viene usato nel titolo episodio e nel contesto.
- `trama`: usata nel prompt come trama specifica dell'episodio.
- `episode_goal`: inserito nel prompt come obiettivo episodio.
- `start_situation`: inserito nel prompt come situazione iniziale.
- `initial_phase`: se valorizzato, passato come config override al multi-step (valori ammessi: `AZIONE`, `STASI`, `ERRORE`, `EFFETTO`).
- `tipo_planning_id` (FK `tipo_planning`): override del planning tattico per questo episodio (ha priorita' su `series.default_tipo_planning_id`).

#### Tabella `series_characters`
Personaggi ricorrenti della serie.

- `name`, `gender`, `description`, `eta`, `formazione`, `specializzazione`, `profilo`, `conflitto_interno`:
  - vengono inseriti nel **contesto serie** per la generazione episodio;
  - se la storia appartiene a una serie, questi personaggi **sostituiscono** la lista personaggi della storia nelle fasi TTS.
- `episode_in`, `episode_out`: informativi nel prompt (presenza episodio).
- `voice_id` (FK `tts_voices`): usato per **assegnare voci fisse** ai personaggi nel `tts_schema.json`.
  - se esiste un personaggio chiamato `Narratore`/`Narrator` con `voice_id`, quella voce viene usata come **voce narratore**.
- `image`: nome file immagine (generato o caricato) usato dalla UI.
- `aspect`: descrizione alternativa usata per il prompt immagini (ha priorita' su `description`).

#### Effetti indiretti sulle storie generate
- Se una storia ha `serie_id` e `serie_episode`, vengono usati per:
  - annuncio TTS iniziale: `"Titolo Serie. Episodio N. Titolo storia."`;
  - assegnazione musica dalla libreria serie (`series_folder/<folder>/music`).
- Se una serie non ha `folder` impostato, la musica viene presa dal fallback `data/music_stories`.

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
    "MaxParallelCommands": 1
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
- Tabelle Narrative Engine: `narrative_profiles`, `narrative_resources`, `micro_objectives`, `failure_rules`, `consequence_rules`, `consequence_impacts`, `story_runtime_states`, `story_resource_states`, `chapters`.
- Script di popolamento in `populate_*.sql`; usa `sqlite3 data/storage.db < populate_agents.sql` per caricarli.

## AGENTI DI FALLBACK

Questa sezione definisce lo **standard desiderato** per la gestione dei **modelli di fallback** per ruolo.
Nota: alcune parti possono essere in evoluzione; questa sezione va considerata come riferimento funzionale per l'implementazione.

### Contesto
1. Ogni agente ha un **modello principale** configurato (campo `agents.model_id`).
2. Le tabelle `roles` e `model_roles` permettono di associare a ogni **ruolo** (es. `formatter`, `fx_expert`) uno o piu' **modelli di fallback**.
3. Ogni riga `model_roles` traccia un punteggio basato sull'esito storico tramite i campi `use_count`, `use_successed`, `use_failed`.
  - Punteggio operativo: `success_rate = use_successed / use_count` (se `use_count > 0`, altrimenti 0).

### Comportamento (fallback “in-place”)
Quando un agente fallisce una operazione con il **modello principale** (prima N retry, poi richiesta di spiegazioni), il sistema:
- legge il **ruolo** dell'agente corrente;
- cerca in `model_roles` i modelli di fallback per quel ruolo;
- seleziona il modello con **punteggio migliore**;
- sostituisce **al volo** il modello usato dall'agente corrente con quello selezionato;
- resetta il numero di retry;
- prosegue **nello stesso comando** e **allo stesso step** in cui era (es. se eravamo allo step 3, si continua dallo step 3).

Non devono essere lanciati nuovi comandi: il fallback e' una prosecuzione della stessa esecuzione.

### Vincoli
4. All'interno di uno **stesso comando** non va provato **piu' di una volta lo stesso modello** (primario o fallback).
5. Il punteggio (contatori in `model_roles`) va aggiornato **step per step**:
  - prima si fanno i retry previsti per quello step;
  - poi si registra l'esito (success/fail) aggiornando `use_count` e `use_successed`/`use_failed`.
6. Se tutti i modelli di fallback disponibili per quel ruolo falliscono, il comando fallisce definitivamente.

### Ruoli abilitati (fase attuale)
Per ora questo comportamento e' richiesto per i ruoli/comandi:
- `formatter`
- `ambient_expert`
- `fx_expert`
- `music_expert`

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
