# CONTRIBUTING — Linee guida per sviluppatori

Questo progetto utilizza agenti costruiti con LangChain C# e una custom ReAct loop implementation. Prima di contribuire, leggere e rispettare le regole qui sotto.

## Principi obbligatori
- Tutte le integrazioni che richiedono "tool calling" devono registrare tools tramite `LangChainToolFactory` ereditando da `BaseLangChainTool`. Ogni tool deve esporre schema OpenAI-compatible e implementare `ExecuteAsync()`. Evitare soluzioni basate su parsing libero di testo prodotto dal modello per attivare azioni produttive in produzione.
- Non creare o usare "funzioni inventate" direttamente nei prompt degli agenti che possano alterare il comportamento degli agenti writer o evaluator. I prompt di produzione devono rimanere separati dai test e non devono contenere marker che generano side-effect non controllati.
- I test che verificano la capacità di tool-calling dei modelli devono essere isolati e lanciati esplicitamente dall'interfaccia di amministrazione (es. `Pages/Models`) o tramite test automatici — non devono essere inclusi nelle pipeline di generazione normale.

## Dove registrare le funzioni
- Registra skill native nel Kernel (es. `kernel.RegisterFunction(...)` o tramite gli helper del pacchetto SK) per:
  - Salvataggio/lettura memoria
  - Operazioni database
  - Skill di utilità (es. trasformazioni di testo)
- Evitare di chiamare endpoint esterni con payload non controllati dai wrapper delle skill.

## Testing
- Usa la pagina Admin `Models` per eseguire i test function-calling in isolamento.
- I test devono:
  - Creare un Kernel ad-hoc legato al modello testato.
  - Registrare skill temporanee per il test (save/read/DB/toUpper).
  - Invocare il modello e lasciare che SK gestisca la chiamata alla funzione.
  - Valutare i risultati senza inserire dati di test nei prompt di agenti di produzione.

## Pull request
- Aggiungi descrizione chiara delle modifiche e dei motivi per cui è necessario registrare nuove function/skill.
- Includi test automatici ove possibile.

## Contatti
Per dubbi o eccezioni a queste regole, apri un issue e descrivi le motivazioni.
