# CONTRIBUTING — Linee guida per sviluppatori

Questo progetto utilizza agenti costruiti con LangChain C# e una custom ReAct loop implementation. Prima di contribuire, leggere e rispettare le regole qui sotto.

## Principi obbligatori
- Output LLM runtime: usare TAG tra parentesi quadre (es. `[IS_VALID]true[/IS_VALID]`); non chiedere JSON agli agenti.
- Tool/function calling lato modello e' disabilitato di default (config `ToolCalling:Enabled=false`). Qualsiasi nuova feature deve funzionare senza tool-calls.
- Il supporto tool in `Skills/` resta per retrocompatibilita' e test: se si modifica/aggiunge un tool, mantenere schema OpenAI-compatible e comportamento fail-fast, ma non dipendere dai tool per i flussi standard.
- Non creare o usare "funzioni inventate" direttamente nei prompt degli agenti che possano alterare il comportamento degli agenti writer o evaluator. I prompt di produzione devono rimanere separati dai test e non devono contenere marker che generano side-effect non controllati.
- I test che verificano il tool calling (legacy) devono essere isolati e lanciati esplicitamente (es. `Pages/Models`) o tramite suite dedicata — non devono essere inclusi nelle pipeline di generazione normale.

## Dove registrare le funzioni
- Registra funzioni/servizi applicativi in modo deterministico (non via parsing libero di testo LLM) per:
  - Salvataggio/lettura memoria
  - Operazioni database
  - Skill di utilità (es. trasformazioni di testo)
- Evitare di chiamare endpoint esterni con payload non controllati dai wrapper delle skill.

## Testing
- Usa la pagina Admin `Models` per test legacy su tool support (solo se abilitati esplicitamente).
- I test devono:
  - Creare un Kernel ad-hoc legato al modello testato.
  - Registrare skill temporanee per il test (save/read/DB/toUpper).
  - Invocare il modello e validare output e/o comportamento atteso.
  - Valutare i risultati senza inserire dati di test nei prompt di agenti di produzione.

## Pull request
- Aggiungi descrizione chiara delle modifiche e dei motivi per cui è necessario registrare nuove function/skill.
- Includi test automatici ove possibile.

## Contatti
Per dubbi o eccezioni a queste regole, apri un issue e descrivi le motivazioni.
