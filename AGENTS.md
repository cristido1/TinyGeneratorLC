# Codex Project Instructions

## Index Pages
- Before modifying or creating any `Index` page, read and follow the rules in `docs/index_page_rules.txt`.

## Lingua
- Rispondi sempre in italiano.

## Database
- In questo progetto **non si usano migrazioni applicative**.
- Per modifiche schema/dati usare **comandi SQL diretti** sul database.
- Prima di operazioni potenzialmente distruttive (UPDATE/DELETE massivi, ALTER TABLE, ricalcoli globali), creare una **copia di backup** del file DB.
- Se non sei sicuro dell'impatto di una modifica DB, fai prima un **backup del database** e poi procedi con SQL diretto.

## CallCenter
- Non modificare il funzionamento del `CallCenter` senza prima chiedere conferma esplicita all'utente.
- Prima di ogni intervento, valuta sempre se la modifica richiesta va implementata a livello strutturale nel `CallCenter`.
- Se la modifica pu√≤ impattare struttura/responsabilit√†/flusso del `CallCenter`, chiedi conferma esplicita all'utente prima di procedere.
- Tutte le chiamate agli agenti devono passare obbligatoriamente dal CallCenter.
- Tutti i controlli di validit√† (deterministici o di altra natura) devono essere passati al CallCenter: non implementare validazioni direttamente nei comandi.

## Build
- Non creare cartelle di output custom che iniziano con `artifact` (es. `artifacts_build`, `artifacts_obj`, ecc.).
- Se l'applicazione `TinyGenerator` Ë in esecuzione, **non lanciare build** (`dotnet build`) finchÈ non viene fermata esplicitamente.

## Architettura Comandi (obbligatoria)
- CallCenter e CommandDispatcher sono i punti centrali del sistema: le nuove funzionalita devono essere implementate quasi sempre li, non nei singoli comandi.
- Tutti i nuovi comandi devono implementare obbligatoriamente l'interfaccia ICommand.
- Tutti i nuovi comandi devono passare obbligatoriamente dal CommandDispatcher (no percorsi alternativi, no esecuzioni dirette fuori dispatcher).
- Tutte le chiamate agli agenti LLM dei comandi devono passare obbligatoriamente dal CallCenter (no bypass diretti verso servizi modello/bridge).
- Se un comando nuovo richiede eccezioni a queste regole, fermati e chiedi conferma esplicita all'utente prima di procedere.