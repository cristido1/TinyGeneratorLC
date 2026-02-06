# Operazione `tts_schema` — Descrizione dettagliata

Nota importante (stato attuale del progetto): questo documento descrive un flusso **legacy** basato su tool-calls. Con la configurazione di default `ToolCalling:Enabled=false`, i tool non vengono esposti ai modelli e quindi questa operazione, cosi' come descritta qui, non e' attiva. Se serve riabilitarla, va prima migrata a output TAG-only e/o a un flusso deterministico senza tool-calls.

Questo documento descrive in dettaglio cosa viene eseguito dall'applicazione durante un'operazione di tipo `tts_schema`. Si basa sull'implementazione presente in `Services/ResponseCheckerService.cs`, `Services/MultiStepOrchestrationService.cs` e `Skills/TtsSchemaTool.cs`.

**Scopo dell'operazione (legacy)**
- Convertire un segmento di storia (chunk) in una sequenza di chiamate strumentali (`tool_calls`) per la generazione TTS: principalmente `add_narration` e `add_phrase`.
- Assicurare che la maggior parte del testo sorgente sia coperta da queste chiamate (soglia predefinita 90%).
- Validare la correttezza strutturale dei `tool_calls` (parametri obbligatori come `text`, `character`) e segnalare errori/warning.
- Aggregare le chiamate di tutte le fasi e salvare un file finale `tts_schema.json` con lo schema completo.

**Flusso ad alto livello**
- L'orchestratore esegue più step per generare lo schema TTS a partire da chunk di testo.
- Per ogni step:
  - Il modello produce un output (idealmente un payload strutturato contenente `tool_calls`, oppure la chiamata diretta alle funzioni tramite il plugin `TtsSchemaTool`).
  - Viene eseguita una validazione deterministica del passo (senza chiamare automaticamente un `response_checker` LLM):
    - Se il task è `tts_schema`, viene invocata `ValidateTtsSchemaResponse(...)` per analizzare il risultato e calcolare la copertura testuale.
    - In altri task vengono applicate verifiche basiche (es. output non vuoto).
  - Se l'agente espone skill/tool ma il testo restituito non contiene effettive tool calls (heuristica `ContainsToolCalls`), l'orchestratore può chiedere al `ResponseChecker` di costruire un breve promemoria (funzione `BuildToolUseReminderAsync`) da inviare al modello come feedback, ma questo LLM serve solo a generare suggerimenti e non valuta il successo.
  - Se la validazione fallisce (es. coverage < soglia, mancano parametri obbligatori), il sistema marca il passo come `NeedsRetry = true` e può riprovare o usare fallback model.

**Validazione TTS per singolo step (funzione `ValidateTtsSchemaResponse`)**
- Obiettivo: verificare che il testo estratto dalle `tool_calls` copra almeno il `minimumCoverageThreshold` (di default 0.90).
- Passi applicati:
  1. Tentativo di deserializzare l'intera risposta in `ApiResponse` (struttura attesa che contiene `message.tool_calls`). Se la deserializzazione fallisce, si procede con parsing di fallback.
  2. Estrazione delle `tool_calls` in modo strutturato (se presente) oppure analisi testuale fallback con `ExtractToolCallsFromText` + `ParseToolCallsArray`.
  3. Per ogni `ParsedToolCall` estratta:
     - Verifica di presenza del campo `function` (es. `add_narration`, `add_phrase`).
     - Parsing degli `arguments` in `Dictionary<string, object>` ove possibile.
     - Controllo dei parametri obbligatori: per `add_narration` è obbligatorio `text`; per `add_phrase` sono obbligatori `text` e `character` (mentre `emotion` è opzionale ma genera un warning se mancante).
     - Raccolta del testo (valore del campo `text`) in una lista `allExtractedText`.
  4. Normalizzazione del testo sorgente e dei testi estratti (`NormalizeText`): rimozione punteggiatura comune, collasso degli spazi, lowercase.
  5. Calcolo della copertura: si rimuovono le porzioni corrispondenti dei testi estratti dal testo sorgente normalizzato; si calcolano `OriginalChars`, `CoveredChars`, `RemainingChars` e la percentuale `CoveragePercent`.
  6. Se `CoveragePercent >= minimumCoverageThreshold` la validazione passa (IsValid = true), altrimenti fallisce e viene popolato `FeedbackMessage` con istruzioni chiare su cosa manca (es. percentuale mancante).
- Errori e warning sono raccolti in `TtsValidationResult.Errors` e `.Warnings`.

**Heuristics e fallback parsing**
- Se l'output non è JSON valido, il validatore cerca comunque di parsare possibili array `tool_calls` nel testo usando `JsonDocument.Parse` o pattern JSON.
- In mancanza di struttura, vengono aggiunti warning e il validatore restituisce che non è stato possibile estrarre correttamente le chiamate, fornendo messaggi utili al modello per il retry.

**Comportamento dell'orchestratore per retry e fallback**
- Dopo la validazione di step:
  - Se `IsValid == true`: si procede allo step successivo.
  - Se `IsValid == false` e `NeedsRetry == true`: il sistema può ritentare lo stesso step (stesso o con altro modello), fino al numero di tentativi consentiti.
  - Se l'agente espone tool ma non li ha usati: l'orchestratore chiama `BuildToolUseReminderAsync` per generare un breve messaggio di assistente che ricordi al modello di usare i tool. Questo è l'unico caso in cui viene usato un `response_checker` LLM per costruire un feedback, non per giudicare in modo sostitutivo la correctness.
  - `TryFallbackModelAsync` esegue un tentativo con un modello di fallback e applica la stessa validazione deterministica (inclusa la validazione TTS se `taskType == "tts_schema"`).

**Aggregazione e persistenza finale (`CompleteExecutionAsync`)**
- Una volta completata l'esecuzione (o quando lo stato raggiunge completion), se il `execution.TaskType` è `tts_schema` il sistema:
  - Itera tutti gli step eseguiti e richiama `ValidateTtsSchemaResponse` su ciascun `stepOutput` per estrarre `ParsedToolCall` con robustezza.
  - Converte le `ParsedToolCall` in oggetti del dominio (`TtsPhrase`, `TtsCharacter`, timeline, ecc.) e costruisce un oggetto `TtsSchema` aggregato.
  - Serializza e salva lo schema finale come `tts_schema.json` in una cartella di lavoro associata all'esecuzione (di default `data/tts/<executionId>` o come definito nella configurazione di esecuzione).
  - In caso di errori nella costruzione del file finale, vengono loggati messaggi utili e la persistenza può fallire in modo controllato (il risultato dell'esecuzione contiene i dettagli).

**Interazione con `TtsSchemaTool` (plugin/skill)**
- L'agente può chiamare direttamente le funzioni del tool `TtsSchemaTool` durante la generazione (es. `add_narration`, `add_phrase`).
- `TtsSchemaTool` mantiene internamente `_schema` e supporta snapshot persistenti ogni volta che viene invocata una funzione (metodi `TrySaveSnapshot` / `PersistSchemaSnapshot`).
- Questo approccio permette di avere salvataggi incrementali durante la generazione e riduce la necessità di parsing fallback successivi.

**Messaggi e feedback per il modello**
- Se la copertura è insufficiente, il validatore produce un `FeedbackMessage` chiaro: es. "Text coverage only 78.4% (missing 21.6%). You must include ALL text from the original story. Please add the missing narration or dialogue phrases to cover at least 90% of the source text.".
- Se mancano parametri obbligatori nelle `tool_calls`, vengono prodotti `Errors` che spiegano quale parametro manca (es. "Tool call add_phrase missing required parameter 'character'").

**Comportamenti importanti e decisioni di progetto**
- Le chiamate LLM del `response_checker` non vengono usate automaticamente per approvare o respingere uno step: la validazione è deterministica e basata su parsing + coverage.
- LLM viene usato solo per generare un breve promemoria quando il modello risponde con testo libero invece di usare i tool.
- I controlli semantici basati su embeddings (similarità) sono applicati solo ai writer-type agents tramite `ValidateWriterResponseAsync`, non al flusso `tts_schema`.

**Configurabilità e possibili miglioramenti**
- Rende configurabile la soglia di coverage (attualmente 90%) tramite impostazioni o DB.
- Migliorare l'heuristica `ContainsToolCalls` passando gli schemi reali dei tool al generatore di reminder (`BuildToolUseReminderAsync`) per suggerimenti più mirati.
- Aggiungere un log più dettagliato delle rimozioni parziali durante il calcolo della coverage per aiutare il debug di casi borderline (soprattutto con normalizzazione/accorciamenti).
- Supportare matching fuzzy o basato su token invece che su substring per scenari con riformulazioni significative.

**Punti di riferimento nel codice**
- Validazione e parsing TTS: `Services/ResponseCheckerService.cs` (metodi `ValidateTtsSchemaResponse`, `ExtractToolCallsFromText`, `ParseToolCallsArray`, `NormalizeText`).
- Orchestrazione step / retry / persistenza finale: `Services/MultiStepOrchestrationService.cs` (esecuzione step, invocazione validator per `tts_schema`, `TryFallbackModelAsync`, `CompleteExecutionAsync` che costruisce e salva `tts_schema.json`).
- Implementazione del tool che i modelli possono chiamare: `Skills/TtsSchemaTool.cs` (metodi `AddNarration`, `AddPhraseAutoCreate`, snapshot persistence).

---

Se vuoi, posso:
- Generare un esempio reale di `tts_schema.json` a partire da un breve chunk di testo di prova.
- Aggiornare il codice per rendere la soglia di coverage configurabile.
- Modificare `BuildToolUseReminderAsync` per passare gli schemi dei tool registrati e produrre promemoria più precisi.

Vuoi che proceda con uno di questi passaggi?