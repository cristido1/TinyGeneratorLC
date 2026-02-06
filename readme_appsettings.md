# Configurazione: appsettings.json (mappa “da codice”)

Questo file descrive le sezioni principali di `appsettings.json` e dove vengono usate.

## Database
- `Database.EnableEfMigrations`: deve rimanere `false` (in questo repo **non** usiamo EF migrations).
- `Database.EnableManualMigrations`: abilita/disabilita i blocchi SQL idempotenti a startup (se presenti).

## CommandDispatcher
- `CommandDispatcher.MaxParallelCommands`: parallelismo worker (default nel codice: 3; override qui).

## Commands (nuovo)
Da Feb 2026 la configurazione relativa ai comandi è consolidata nella sezione `Commands`:

- `Commands:Tuning`: parametri “tunable” dei comandi (chunking, tentativi per chunk, soglie, ecc.).
- `Commands:Policies`: policy di esecuzione del `CommandDispatcher` (retry a livello di comando, backoff, ecc.).
- `Commands:ResponseValidation`: policy e regole del `response_checker` + retry di validazione.

Note utili:
- `Commands:Policies` risolve la policy per un work item usando prima `operationName` (primo argomento di `Enqueue`), poi fallback su `metadata["operation"]` se presente.
- `Commands:ResponseValidation.CommandPolicies` usa come chiave `LogScope.Current` (tipicamente lo `threadScope` passato al dispatcher), quindi le chiavi possono essere diverse dai codici in `metadata["operation"]`.

Nota: le chiavi legacy `CommandTuning` e `ResponseValidation` restano supportate come fallback se `Commands` non è presente.

## ResponseValidation (legacy)
Sezione critica per qualità/robustezza output LLM:
- `Enabled`: abilita il ciclo di validazione.
- `MaxRetries`: retry per validazione fallita.
- `AskFailureReasonOnFinalFailure`: se `true`, a retry esauriti chiede una diagnosi best-effort.
- `EnableFallback`: abilita fallback modelli via `model_roles`.
- `EnableCheckerByDefault`: abilita checker agent per default (consigliato: `true`).
- `SkipRoles`: ruoli esclusi.
- `CommandPolicies`: override per operazioni (es. `create_story`, `thread_chat`).
- Estensioni per-operation:
	- `CommandPolicies.<op>.MaxRetries`
	- `CommandPolicies.<op>.AskFailureReasonOnFinalFailure`
	- `CommandPolicies.<op>.RuleIds` (sottoinsieme regole per checker)
- `Rules`: regole testuali generiche.

Nota: con `EnableCheckerByDefault=true` il checker parte per tutte le operazioni **tranne** i ruoli in `SkipRoles` o le operazioni con `CommandPolicies.<op>.EnableChecker=false`.

Chiavi “check” interne già usate dal codice (utili per override espliciti):
- `response_checker_validation` (validazione writer/step via agent `response_checker`)
- `response_checker_tool_reminder` (reminder tool-use in `ReActLoopOrchestrator`)

### Test command policies (per group)
I comandi test enqueued da `LangChainTestService` usano spesso un `threadScope` del tipo:
- `tests/<group>/<model>`

Per permettere config per-singolo comando (es. `test_base`, `test_music`) la ResponseValidation normalizza quella chiave in:
- `test_<group>`

Esempio: scope `tests/base/llama3` → policy key `test_base`.

### JsonScoreTestService
Il comando enqueued da `JsonScoreTestService` usa `threadScope`/policy key:
- `json_score`

Punto di riferimento: `Services/LangChainChatBridge.cs`.

Nota (policy corrente): la validazione e i checker richiedono output strutturati tramite TAG tra parentesi quadre (non JSON). Vedi `Services/ResponseCheckerService.cs` e `Services/Text/BracketTagParser.cs`.

## ToolCalling
Kill-switch globale per disabilitare il tool/function calling lato modello:
- `ToolCalling.Enabled`: se `false` (default) non vengono registrati/esposti tool agli LLM e gli schemi tool non vengono inviati nelle chiamate.

Punto di riferimento: `Services/LangChainKernelFactory.cs`.

## Ollama / OpenAI
- `Ollama.endpoint`: endpoint OpenAI-compatible di Ollama.
- `OpenAI.endpoint`: endpoint OpenAI (Responses API).
- Liste `No*Models`: modelli per cui alcuni parametri non vanno inviati.

## Memory
- `Memory.Embeddings.Model`: modello embedding (es. `nomic-embed-text`).
- Parametri di backfill/timeout/dimension.

## TtsSchemaGeneration / AudioGeneration / AudioMix
- `TtsSchemaGeneration.*`: parametri di generazione schema e autolaunch.
- `AudioGeneration.*`: autolaunch per TTS/Ambience/Fx.
- `AudioMix.*`: volumi default nel mix.

## CommandTuning / StoryTaggingPipeline (legacy)
Sono parametri di “tuning” per chunking, retry e vincoli minimi per tag. Preferire `Commands:Tuning`.

## Serie
Sezioni dedicate a pipeline di generazione serie e validator (attempts, retry, diagnose).

## StateDrivenStoryGeneration
Flag/threshold per controlli anti-loop e qualità nella generazione state-driven.

## AutomaticOperations
Scheduler/auto-ops:
- `Enabled`: abilita/disabilita.
- Sotto-sezioni per operazioni automatiche (priorità, soglie, intervalli).

Nota: le opzioni effettive possono essere lette in vari servizi; quando aggiorni config o aggiungi nuove chiavi, aggiorna anche la documentazione e (se necessario) i binding Options.
