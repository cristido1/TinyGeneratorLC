# Risposte, TAG e validazione

Questa nota descrive come TinyGenerator gestisce **output LLM** e **validazione**.

Policy di progetto (runtime):
- Le risposte degli agenti devono essere strutturate con TAG tra parentesi quadre (es. `[IS_VALID]true[/IS_VALID]`).
- Non richiediamo JSON agli agenti.
- Tool calling / function calling lato modello è disabilitato di default (kill-switch in config).

Punti di riferimento:
- Validazione + fallback: `Services/LangChainChatBridge.cs`, `Services/ModelFallbackService.cs`
- Checker multi-step writer: `Services/ResponseCheckerService.cs`
- Normalizzazione output provider: `Services/ProviderResponseParser.cs`
- Parser TAG: `Services/Text/BracketTagParser.cs`
- Response formats (legacy/test): `Services/LangChainTestService.cs` + cartella `response_formats/`

## 1) Tool calling (supporto presente, ma disabilitato di default)
Il codice mantiene supporto ai tool in `Skills/`, ma per policy e configurazione predefinita i tool **non vengono esposti ai modelli**.

Configurazione: sezione `ToolCalling` in `appsettings.json`.
- Se `ToolCalling:Enabled=false` l'orchestrator viene creato senza tool e i relativi schema non vengono inviati al modello.

## 2) Normalizzazione risposta provider
`ProviderResponseParser.ExtractText(rawJson)` prova a estrarre il testo da forme diverse:
- top-level `Content` string contenente JSON di chat.
- `Content` come object con `Items[].Text`.
- fallback su campi comuni (`response`, `output.response`).

## 3) ResponseValidation (runtime)
`LangChainChatBridge.CallModelWithToolsAsync(...)` implementa:
- retry controllato (`MaxRetries`).
- applicazione regole generiche (`Rules`) e policy per operazione (`CommandPolicies`).
- opzionale “checker” su alcune operazioni (`EnableCheckerByDefault` + override per comando).
- fallback modelli (se `EnableFallback=true` e ruolo disponibile).
- per ogni verifica crea un oggetto `ResponseValidation` con `LogId`, `Successed` e lista errori, usato per aggiornare la voce di log della response.

Configurazione: sezione `ResponseValidation` in `appsettings.json`.

### SkipRoles
Alcuni ruoli agenti vengono esclusi dalla validazione (es. `response_checker`, `log_analyzer`) per evitare loop.

## 4) Checker per multi-step writer
`ResponseCheckerService.ValidateWriterResponseAsync(...)`:
- fa pre-check locali (lunghezza minima, semantica/embedding, regole per “trama”).
- se serve, invoca un agente DB con `agents.role == response_checker`.
- richiede output con TAG tra parentesi quadre (TAG-first):
	- `[IS_VALID]true|false[/IS_VALID]`
	- `[NEEDS_RETRY]true|false[/NEEDS_RETRY]`
	- `[REASON]...[/REASON]`
	- `[VIOLATED_RULES]...[/VIOLATED_RULES]` (opzionale)

Nota: per retrocompatibilita' il parser mantiene fallback su JSON/forma libera, ma il contratto preferito e' TAG-only.

## 5) Response formats (folder `response_formats/`) (legacy/test)
La cartella `response_formats/` contiene schemi JSON usati in alcuni test/fixture o flussi legacy.

Nel runtime standard del progetto preferiamo TAG tra parentesi quadre e non chiediamo JSON agli agenti.

## 6) Suggerimenti pratici
- Se un output deve essere strutturato: definisci un set di TAG e valida deterministically (TAG presence + regole).
- Se attivi fallback: assicurati che `roles`/`model_roles` siano coerenti con i ruoli usati dagli agenti.
