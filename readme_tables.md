dotnet# Database: tabelle e uso (source-of-truth = codice)

> Nota: in questo progetto **NON usiamo EF Core migrations**. Lo schema SQLite viene mantenuto con SQL/manual scripts (idempotenti) e “best-effort ensures” nel codice.
>
> Punti di riferimento:
> - EF mapping: `Models/*.cs` + `data/TinyGeneratorDbContext.cs`
> - SQL/manual ensures: `Services/DatabaseService.cs` e `Services/PersistentMemoryService.cs`

## Regola pratica
- Se devi aggiungere/alterare una tabella: aggiungi SQL idempotente (script o blocco manuale) e aggiorna la documentazione.

## Tabelle principali (EF-mappate)

### Core (agenti, modelli, ruoli)
- `agents`: configurazione agenti (prompt, skills JSON, model_id, role string, multi_step_template_id, ecc.).
- `models`: catalogo modelli (provider, nome, parametri, score, ecc.).
- `roles`: catalogo “ruoli” normalizzato usato per fallback (indice univoco su `ruolo`).
- `model_roles`: associazione (ruolo ↔ modello) per fallback e tracking; include `is_primary`, `enabled`, contatori uso.
- `tts_voices`: voci TTS disponibili/configurate.

### Storie e valutazioni
- `stories`: storia (raw/tagged/summary, status, folder, punteggi, riferimenti serie/episodio, ecc.).
- `stories_evaluations`: valutazioni per storia (score + spiegazioni + raw JSON).
- `stories_status`: stati macchina (code/function_name/ordine) per catene di status.
- `chapters`: chunk/capitoli per generazione state-driven.

### Orchestrazione multi-step
- `step_templates`: template multi-step (prompt, istruzioni, vincoli e validazioni per step).
- `task_types`: definisce comportamenti/validator/checker di default per tipo task.
- `task_executions`: esecuzioni multi-step (task_type, entityId, agent executor/checker, config overrides, ecc.).
- `task_execution_steps`: step eseguiti + output/diagnosi.

### Serie
- `series`: definizione serie (bible, tono, regole, folder assets, planning defaults, ecc.).
- `series_episodes`: episodi di serie.
- `series_characters`: personaggi ricorrenti (voice_id, descrizioni, immagini).
- `series_state`: stato serie (supporto a processi e auto-ops legate alla serie).

### Narrative Engine (state-driven)
- `narrative_profiles`: profili narrativi (system prompt + style prompt) e metadati.
- `narrative_resources`: risorse (energia, integrità, ecc.) legate al profilo.
- `micro_objectives`: micro-obiettivi (codice, difficoltà, descrizione).
- `failure_rules`: regole di fallimento.
- `consequence_rules`: regole di conseguenza.
- `consequence_impacts`: impatti su risorse per conseguenze.
- `story_runtime_states`: runtime state attivo per una storia.
- `story_resource_states`: stato delle singole risorse nel runtime.

### Coerenza e facts
- `chunk_facts`: estrazione facts per chunk.
- `coherence_scores`: score di coerenza.
- `global_coherence`: aggregazione/coerenza globale.

### Testing / benchmark modelli
- `test_definitions`: definizioni test.
- `test_prompts`: prompt test.
- `model_test_runs`: run aggregati.
- `model_test_steps`: step risultati.
- `model_test_assets`: asset collegati ai test.
- `stats_models`: statistiche/score per modello e operazione.
- `usage_state`: stato/uso (telemetria interna leggera).

### Logging / reporting
- `Log`: log applicativi (SignalR/UI + filtri + metadati).
- `app_events`: definizioni eventi applicativi (enable/logged/notified).
- `log_analysis`: risultati analisi log.
- `system_reports`: report/diagnostica (creata anche via SQL/manual ensures).

### Planning
- `planner_methods`: metodi planner (indice univoco su `code`).
- `tipo_planning`: configurazioni planning (indice univoco su `codice`).

### Supporto sentiment
- `mapped_sentiments`: mapping sentiment.
- `sentiment_embeddings`: embeddings per sentiment.

## Tabelle “manuali” (non EF-mappate o gestite con Dapper)
- `Memory`: memoria vettoriale/embedding (gestita in `Services/PersistentMemoryService.cs`, esclusa dal DbContext).
- `numerators_state`: stato contatori/ID (gestito in `Services/DatabaseService.cs`).

## Indici/constraint rilevanti
- `roles.ruolo` è **univoco** (dedup + indice univoco; necessario per fallback affidabile).
- Diverse tabelle hanno indici univoci (es. `planner_methods.code`, `tipo_planning.codice`).

## Dove guardare quando “manca una tabella”
- Se una tabella esiste nel DB ma non ha modello EF: probabilmente è gestita da SQL manuale o Dapper.
- Se una tabella è mappata in `Models/` ma non esiste fisicamente: va creata con SQL/manual script (non con EF migrations).
