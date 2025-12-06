# Operazione `story_evaluator` — Descrizione dettagliata

Questo documento descrive in dettaglio cosa esegue il comando/servizio `story_evaluator` all'interno del progetto TinyGenerator. Fornisce una panoramica del flusso operativo, dei controlli effettuati, dei formati di output attesi, e dei punti di integrazione con altri componenti del sistema.

**Scopo dell'operazione**
- Valutare una o più versioni di una storia prodotte dai writer agents (A/B/C o singolo writer).
- Generare valutazioni strutturate (es. punteggi numerici, commenti qualitativi) in formato JSON coerente con gli schemi in `response_formats/`.
- Aggregare i risultati degli evaluator agents e produrre una scelta o una classifica delle versioni candidate.
- Salvare risultati di valutazione (= `StoryEvaluation`) e metadati nel database per auditing e per la logica di selezione finale.

**Entry point e trigger**
- L'operazione può essere invocata da:
  - Un endpoint API (es. `StoriesApiController` o `TestDefinitions`),
  - Un job interno (es. `StoryGeneratorService`) dopo che i writer hanno prodotto candidate,
  - Un task manuale dall'interfaccia admin.
- Parametri principali: identificatore della storia/esecuzione, lista di candidate text, eventuali criteri di valutazione (punteggio minimo, schema di risposta atteso), configurazione di retry/fallback.

```markdown
# Operazione `story_evaluator` — Descrizione dettagliata (versione aggiornata)

Questo documento descrive l'algoritmo aggiornato dell'operazione `story_evaluator` nel progetto TinyGenerator, con attenzione alle modifiche recenti nell'orchestrazione ReAct e nelle regole di validazione deterministica.

**Obiettivi principali**
- Valutare candidate testuali prodotte dai writer agents e produrre valutazioni strutturate (JSON) compatibili con gli schemi in `response_formats/`.
- Applicare valide regole deterministiche per accettare o rigettare output (es. coverage TTS, campi obbligatori).
- Mantenere l'orchestrazione stateless per ciascuna iterazione del ReAct loop (minimizzare il carry-over di messaggi) e fornire tracciamento diagnostico.

**Concetti chiave introdotti**
- Stateless per iterazione: ogni chiamata al modello ricostruisce la conversazione partendo solo dal `system` message e da un singolo `user` message (`currentPrompt`). Non si preservano i messaggi `assistant` o `tool` nella storia persistente tra le iterazioni.
- `currentPrompt`: stringa mutabile che rappresenta il prompt utente ricostruito tra le iterazioni; gli output degli strumenti vengono iniettati nel `currentPrompt` tramite placeholder o, in assenza di placeholder, tramite una sezione `-- Tool results --` aggiunta in coda.
- Placeholder injection: supporto per placeholder nel prompt come `{{TOOL_toolName}}`, `{{tool:toolName}}`, `{{toolName}}`, `{{toolName_result}}` (case-insensitive). Se un placeholder è presente, il risultato dello strumento lo sostituisce, altrimenti i risultati vengono allegati al prompt.
- Validazione post-tool (TTS): quando la risposta del modello include chiamate TTS (es. `add_narration`, `add_phrase`) il sistema esegue gli strumenti e poi riesegue una validazione deterministica della copertura del testo. Se la validazione è passata (IsValid) oppure la copertura è >= 90% la step viene considerata completa e il loop si interrompe senza fare una seconda richiesta di chiusura al modello.
- Fallback chunk extraction: per ricavare la porzione sorgente da validare si cerca prima un marcatore `{{CHUNK_n}}: ...`. Se non è presente, la validazione usa il `currentPrompt` intero come fallback.
- Nessuna reiniezione di JSON grezzo: l'algoritmo evita di scrivere nei messaggi assistant il JSON grezzo restituito dal modello; questo riduce il rischio di confusione e di parsing errato in iterazioni successive.

**Flusso operativo aggiornato (sintesi)**
1. Preparazione
  - Recupera candidate ed evaluator agents attivi.
  - Carica schema di valutazione e costruisce il `userPrompt` iniziale (contiene candidate text, rubriche, schema atteso e placeholder `{{CHUNK_n}}` quando rilevante).
  - `currentPrompt` = `userPrompt`.

2. Loop ReAct (per ogni iterazione)
  - Ricostruisce la history per la chiamata: include solo il `system` (se presente) e il `user` con il `currentPrompt` (stateless).
  - Chiama il modello tramite `CallModelAsync` (che usa `LangChainChatBridge`), riceve un payload normalizzato con `content` e opzionali `tool_calls`.
  - Parsing: estrae `tool_calls` usando i parser robusti (gestione di wrapper `message.content`, JSON embedded, argomenti escape-encoded).
  - Se non ci sono `tool_calls`:
    - Se il modello doveva chiamare una funzione finale (es. `evaluate_full_story`) e non l'ha fatto, l'algoritmo inietta una richiesta di retry nel `currentPrompt` (fino a 3 volte) invece di accodare messaggi assistant persistenti.
    - Altrimenti il sistema accetta la risposta (estratta con `ExtractPlainTextResponse`) se l'agente è autorizzato al free-text (es. writer) oppure, se necessario, richiede al `response_checker` un promemoria conciso (non una validazione automatica completa) per spingere l'uso degli strumenti.

3. Esecuzione strumenti
  - Per ogni `tool_call` rilevato, esegue lo strumento via `_tools.ExecuteToolAsync` e conserva i risultati in memoria (lista `toolResults`).
  - Non vengono aggiunti messaggi `assistant`/`tool` alla storia persistente: i risultati vengono iniettati nel `currentPrompt` tramite `ReplacePlaceholdersInPrompt` o appesi come `-- Tool results --`.

4. Validazione deterministica post-tool (TTS specifico)
  - Se la risposta conteneva chiamate TTS, dopo aver eseguito gli strumenti il sistema chiama `ResponseCheckerService.ValidateTtsSchemaResponse` usando la porzione sorgente (marker `{{CHUNK_n}}` se presente, altrimenti `currentPrompt`).
  - Se la validazione ritorna `IsValid == true` o `CoveragePercent >= 0.90`, il passo è considerato completo: il metodo ritorna immediatamente un `FinalResponse` che include il testo estratto e la lista delle `tool_calls` eseguite. Questo evita la seconda chiamata di chiusura al modello.
  - Se la copertura è insufficiente (< 90%), il sistema inietta un breve feedback (es. richiesta di aumentare la narrazione per superare il 90%) dentro `currentPrompt` e ripete il loop.

5. Controlli di integrità e retry per tools
  - Il codice controlla errori specifici restituiti dagli strumenti (es. `evaluate_full_story` che segnala campi mancanti) e mantiene contatori di retry per tool; i messaggi di richiesta di retry vengono iniettati in `currentPrompt` (stateless) fino a limiti prefissati, poi il passo fallisce con log e diagnostica.

6. Aggregazione e persistenza
  - Le valutazioni finali (score, reason, dettagli) vengono aggregate se più evaluator hanno partecipato e poi persistite in DB (`StoryEvaluation`, `StoryRecord`).
  - Il sistema mantiene anche la persistenza degli output degli strumenti e, se applicabile, dell'aggregato `tts_schema.json` costruito dalle `tool_calls` (questo file viene salvato al termine dell'esecuzione quando richiesto per auditing/riuso TTS).

**Logging e diagnostica**
- Vengono loggati: payload grezzo della risposta del modello (sia nella forma originale sia nella forma normalizzata), branch di parsing usato, numero di `tool_calls`, risultati degli strumenti e outcome della validazione TTS (CoveragePercent, IsValid, eventuali Errors/Warnings).
- Raccomandazione operativa: abilitare log persistenti per i run di debug (salvare raw response + parsed shape) per diagnosticare casi di JSON doppio-escaped o embedding inatteso.

**Implicazioni e vantaggi della nuova logica**
- Riduce i falsi positivi/duplicate calls dovuti alla reiniezione di messaggi `assistant` contenenti JSON grezzo.
- Controlli deterministici (soprattutto TTS coverage >=90%) riducono la dipendenza da verifiche LLM-centrate; il modello viene usato per generare promemoria quando scrive testo libero, ma non per giudicare automaticamente la conformità quando non richiesto.
- Il pattern stateless per iterazione mantiene i prompt più puliti e riproducibili, migliorando la robustezza dei test e la gestione del contesto.

**Punti di riferimento nel codice (aggiornati)**
- Orchestrazione e loop LLM: `Services/ReActLoopOrchestrator.cs` (stateless per-iterazione, `currentPrompt`, `ReplacePlaceholdersInPrompt`, post-tool TTS validation).
- Bridge/Parsing: `Services/LangChainChatBridge.cs` (ParseChatResponse e fallback parsing dei tool_calls).
- Validazione: `Services/ResponseCheckerService.cs` (metodi `ValidateTtsSchemaResponse`, `BuildToolUseReminderAsync`).
- Persistenza e logging: `Services/DatabaseService.cs`, `ICustomLogger` implementazioni.

---

Se vuoi, posso:
- Eseguire la simulazione della run problematiche (threadId=2) usando il payload che mi hai fornito e catturare i log grezzi e il branch di parsing.
- Aggiungere log persistenti (file/DB) per ogni raw response e branch di parsing.
- Aggiornare il documento con esempi di `FinalResponse` serializzato (incluso `tool_calls`) e con snippet di prompt che mostrano i placeholder `{{CHUNK_n}}` e `{{TOOL_name}}`.

Dimmi quale preferisci e procedo.
``` 