# Operazione `writer` in MultiStep — Descrizione dettagliata

Questo documento descrive cosa avviene durante l'esecuzione di un `writer` all'interno del flusso multi-step dell'applicazione TinyGenerator.

**Scopo dell'operazione**
- Far generare al modello (o a una serie di writer agents A/B/C) porzioni coese di testo narrativo rispettando l'istruzione di step.
- Validare semanticamente che l'output corrisponda all'istruzione (per i writer-type agents) usando embeddings.
- Applicare regole di retry/fallback in caso di mancata adesione ai requisiti.
- Valutare e selezionare la miglior versione della storia (quando i writer vengono eseguiti in parallelo come ensemble).

**File di riferimento principali**
- `Services/ResponseCheckerService.cs`: metodo `ValidateWriterResponseAsync` — calcolo embedding, confronto semantico, invocazione opzionale del `response_checker` agent.
- `Services/MultiStepOrchestrationService.cs`: orchestrazione degli step, invocazione delle validazioni per passo, retry e fallback.
- `StoryGeneratorService` (se presente): orchestrazione ad alto livello di più writer agents (A, B, C), confronto e selezione.

**Flusso operativo (passo per passo)**
1. Preparazione dello step
   - L'orchestratore prende il `stepInstruction` e il chunk di contesto rilevante.
   - Costruisce il prompt per il writer agent (può includere system message, prompt templates, e memoria limitata).

2. Invocazione del modello (writer)
   - Il writer produce un output testuale (il contenuto narrativo per lo step).
   - Output ideale: testo narrativo coerente con l'istruzione.

3. Validazione deterministica di base
   - Il metodo `ValidateStepOutputAsync` esegue controlli elementari: output non vuoto, lunghezza minima, formattazione di base.
   - Importante: questa funzione NON chiama automaticamente il `response_checker` (LLM); è solo deterministica.

4. Validazione semantica per writer (`ValidateWriterResponseAsync`)
   - Applicata SOLO agli agenti di tipo writer (o quando il flusso richiede un controllo semantico).
   - Passi principali:
     a. Calcolo embedding per l'`instruction` e per l'`output` usando l'endpoint di embeddings (es. `nomic-embed-text` via `http://localhost:11434/api/embeddings`).
     b. Calcolo della similarità coseno tramite `CosineSimilarity(...)`.
     c. Se nella `validationCriteria` è presente `semantic_threshold`, viene applicata come soglia minima e, se non raggiunta, la validazione fallisce immediatamente con `NeedsRetry = true`.
     d. Se richiesto dagli sviluppatori, la validazione può delegare al `response_checker` agent per controlli aggiuntivi (es. stile, policy) — nel progetto corrente il `response_checker` viene invocato nella validazione writer per controlli avanzati, fornendo anche il punteggio semantico nel prompt.
   - Logging: vengono registrati punteggi e ragioni per facilità di debugging.

5. Comportamento in caso di fallimento
   - Se la validazione semantica o i controlli deterministici falliscono, il passo viene marcato `NeedsRetry = true`.
   - L'orchestratore può ritentare lo step con lo stesso modello oppure chiamare `TryFallbackModelAsync` per usare un modello alternativo configurato (fallback).
   - I messaggi di feedback sono costruiti per essere utili al modello: motivo sintetico, eventuali suggerimenti per correggere (es. "mantieni i personaggi X e Y, enfatizza il conflitto, non introdurre nuovi setting").

6. Ensemble di writer (A/B/C)
   - In scenari dove vengono eseguiti più writer in parallelo, il `StoryGeneratorService`:
     - Lancia più writer agents con la stessa istruzione e contesto (o con prompt variants).
     - Raccoglie le loro uscite e le valida (applica `ValidateWriterResponseAsync` a ciascuna).
     - Eventualmente esegue un passaggio di valutazione (evaluator agents) per assegnare punteggi qualitativi/quantitativi (es. formato JSON di valutazione) e seleziona la migliore versione.
     - Regole di selezione tipiche: score >= soglia (es. 7/10), preferire output senza errori di coerenza, combinare parti se utile.

7. Persistenza
   - I contenuti validati (o la versione selezionata) vengono salvati nel DB come `StoryRecord` o simile.
   - Il log delle chiamate e dei punteggi viene tracciato per auditing e debugging.

**Dettagli tecnici e decisioni di progetto**
- Dove viene calcolato l'embedding
  - `ResponseCheckerService.CalculateSemanticAlignmentAsync` chiama `GetEmbeddingAsync` due volte (istruzione e output) e usa `CosineSimilarity`.
  - L'endpoint di embedding è chiamato con payload `model = "nomic-embed-text", prompt = <text>`.

- Quando si usa il `response_checker` LLM
  - Per evitare falsi negativi causati da valutazioni LLM non deterministiche, il flusso principale di validazione writer applica prima la soglia semantica.
  - Il `response_checker` viene usato per controlli testuali avanzati o come delegato per produrre una valutazione strutturata (JSON) solo nel percorso writer — non viene usato come validator predefinito per tutti i task.

- Retry e fallback
  - Numero massimo di iterazioni e strategie di fallback sono controllati dall'orchestratore (`ReActLoopOrchestrator` e `MultiStepOrchestrationService`).
  - `TryFallbackModelAsync` ripete le stesse validazioni deterministiche e semantiche per l'output del modello di fallback.

**Esempi di condizioni che fanno fallire la validazione writer**
- Punteggio semantico inferiore al `semantic_threshold` (es. 0.70 se configurato).
- Output che contiene domande o richieste di chiarimento invece di produrre il testo richiesto.
- Output che anticipa futuri step o introduce contenuti estranei non richiesti.

**Possibili miglioramenti suggeriti**
- Rendere il `semantic_threshold` configurabile via `appsettings` o DB per ogni tipo di agent o progetto.
- Aggiungere una validazione token-aware (ad es. confronto basato su token/token overlap) per scenari dove il modello parafrasa molto ma mantiene il contenuto.
- Fornire esempi di buone risposte nel prompt del writer per ridurre necessità di retry.
- Aggiungere metriche aggregate per writer ensemble (mean/median semantic score, dispersione) per scegliere modelli più stabili.

**Punti di riferimento nel codice**
- `Services/ResponseCheckerService.cs` — `ValidateWriterResponseAsync`, `CalculateSemanticAlignmentAsync`, `GetEmbeddingAsync`, `ParseValidationResponse`.
- `Services/MultiStepOrchestrationService.cs` — dove gli step vengono eseguiti, validati e gestiti retry/fallback.
- `StoryGeneratorService` (o file equivalente) — orchestrazione di più writer agents e scelta della versione finale della storia.

---

Se vuoi, posso:
- Generare un esempio pratico: dato un `stepInstruction` e un `modelOutput`, mostrarti il calcolo del punteggio semantico e la decisione di validazione.
- Aggiungere un piccolo script di test che simuli chiamate a `ValidateWriterResponseAsync` con testi di esempio.

Dimmi quale preferisci e procedo.