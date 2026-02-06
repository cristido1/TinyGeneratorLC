# Agenti e ruoli (source-of-truth = codice)

Questa nota distingue due concetti che nel progetto convivono:

1) **`agents.role` (stringa)**: ruolo operativo di un agente (es. `writer`, `response_checker`, `log_analyzer`). È usato a runtime per scegliere comportamenti/validator/fallback.
2) **Tabella `roles` + `model_roles`**: catalogo ruoli “normalizzati” usato dal sistema di **fallback modelli** e tracking affidabilità.

Punti di riferimento:
- Modello agent: `Models/Agent.cs`
- Ruoli/fallback: `Models/Role.cs`, `Models/ModelRole.cs`, `Services/ModelFallbackService.cs`
- Seed ruoli + indice univoco: `data/TinyGeneratorDbContext.cs`

## Ruoli seedati in `roles`
Seed attuale (vedi DbContext):
- `writer`: generazione storia.
- `formatter`: formattazione/tagging (es. voice tags / markup).
- `evaluator`: scoring valutazione (qualità/coerenza/azione ecc.).
- `tts_expert`: generazione/validazione schema TTS o componenti TTS.
- `music_expert`: tagging/generazione musica.
- `fx_expert`: tagging/generazione FX.
- `summarizer`: riassunti.
- `canon_extractor`: estrazione canon/facts.
- `state_delta_builder`: costruzione delta di stato.
- `continuity_validator`: validazione continuità.
- `state_updater`: applicazione aggiornamenti allo stato.
- `state_compressor`: compressione stato.
- `recap_builder`: costruzione recap.

## `model_roles`: fallback modelli per ruolo
- Ogni riga associa un `model_id` a un `role_id`.
- `is_primary=true` viene usato per **tracciare il modello primario** (non viene considerato tra i fallback).
- I fallback sono `enabled=true` e `is_primary=false`, ordinati per success-rate.

### Tracking (success/fail)
- Contatori: `use_count`, `use_successed`, `use_failed`, `last_use`.
- Il tracking del **primario** può auto-creare la riga `model_roles` con `is_primary=1` se mancante.

## Come il codice decide il ruolo (pratico)
- Il dispatcher e i servizi spesso passano `metadata["agentRole"]` e `metadata["agentName"]` quando accodano comandi.
- `LangChainChatBridge` usa il ruolo per:
  - decidere se applicare `ResponseValidation`.
  - attivare fallback (`ModelFallbackService`) quando la validazione fallisce e `EnableFallback=true`.

## Ruoli speciali non seedati in `roles`
Alcuni ruoli “operativi” esistono come `agents.role` ma non necessariamente come record in `roles` (dipende dall’uso):
- `response_checker`: agente “checker” invocato da `ResponseCheckerService` per validare output writer.
- `log_analyzer`: tipicamente escluso da ResponseValidation (`SkipRoles`).

Se vuoi usare fallback per un ruolo operativo, assicurati che esista anche in `roles`.

## Ruoli `serie_*` (generazione serie)
La pipeline `generate_new_serie` usa ruoli operativi:
- `serie_bible_agent`
- `serie_character_agent`
- `serie_season_agent`
- `serie_episode_agent`
- `serie_validator_agent`

Per abilitare tracking + fallback su questi ruoli, l'inizializzazione DB (in `Services/DatabaseService.cs`) inserisce in modo idempotente anche i record `roles` corrispondenti (incluso `serie_audio_agent` come estensione opzionale).
