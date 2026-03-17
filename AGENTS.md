# Codex Project Instructions

## Index Pages
- Before modifying or creating any `Index` page, read and follow the rules in `docs/index_page_rules.txt`.
- Standard obbligatorio UI dati:
  - Tutte le nuove pagine lista/griglia devono usare l'API CRUD generica esistente (`/api/crud/...`) e la griglia VuePrime.
  - Per tabelle semplici (CRUD standard senza logica speciale) usare obbligatoriamente `Pages/Shared/Index`.
  - Creare una pagina Index dedicata solo quando serve comportamento non coperto da `Shared/Index`.
  - `Pages/Shared/Index` non deve contenere cablature specifiche per singole tabelle/campi.
  - La logica della pagina shared deve essere guidata solo da:
    - interfacce implementate dalle entità
    - metadati relazionali/FK (integrità referenziale)
    - endpoint generici del `BaseCrudController`
  - Evitare hardcode di nomi tabella/campo per casi specifici: se serve estendere, farlo in modo generico e riusabile.

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
- Se la modifica può impattare struttura/responsabilità/flusso del `CallCenter`, chiedi conferma esplicita all'utente prima di procedere.
- Tutte le chiamate agli agenti devono passare obbligatoriamente dal CallCenter.
- Tutti i controlli di validità (deterministici o di altra natura) devono essere passati al CallCenter: non implementare validazioni direttamente nei comandi.
- Error attribution obbligatorio: quando un controllo fallisce (checker/deterministico/validazione), scrivere il `fail_reason` sul record di log della request/agente controllato, non sul record del controllo stesso.
- Obiettivo diagnostico primario: identificare quale response/request e' fallita; non e' rilevante chi ha segnalato l'errore.
- Promemoria vincolante: qualsiasi modifica al `CallCenter` richiede sempre conferma esplicita dell'utente prima di procedere.

## Build
- Non creare cartelle di output custom che iniziano con `artifact` (es. `artifacts_build`, `artifacts_obj`, ecc.).
- Se l'applicazione `TinyGenerator` � in esecuzione, **non lanciare build** (`dotnet build`) finch� non viene fermata esplicitamente.

## Architettura Comandi (obbligatoria)
- CallCenter e CommandDispatcher sono i punti centrali del sistema: le nuove funzionalita devono essere implementate quasi sempre li, non nei singoli comandi.
- Tutti i nuovi comandi devono implementare obbligatoriamente l'interfaccia ICommand.
- Tutti i nuovi comandi devono passare obbligatoriamente dal CommandDispatcher (no percorsi alternativi, no esecuzioni dirette fuori dispatcher).
- Tutte le chiamate agli agenti LLM dei comandi devono passare obbligatoriamente dal CallCenter (no bypass diretti verso servizi modello/bridge).
- Se un comando nuovo richiede eccezioni a queste regole, fermati e chiedi conferma esplicita all'utente prima di procedere.
## Interfacce
- Qualsiasi modifica a qualunque interfaccia (aggiunta/rimozione/modifica di metodi, proprieta, firme o contratti) richiede SEMPRE il permesso esplicito dell'utente prima di procedere.
- Prima di creare una nuova funzione, verificare sempre se esiste gia una funzione/metodo simile riutilizzabile nel codice; creare nuovo codice solo se necessario.
- Standard entita/tabelle:
  - Tutte le entita mappate a tabella devono implementare `IEntity` (chiave primaria intera) e `IActiveFlag`.

## Controlli Deterministici (Lingua Italiana)
- Non sono accettati controlli deterministici basati sulla presenza/assenza di specifiche parole o singoli termini.
- Motivazione: l'italiano ha elevata variabilit� lessicale, morfologica e sintattica; lo stesso concetto pu� essere espresso con molte forme diverse.
- Se serve una validazione semantica, usare valutazione contestuale tramite agente (via CallCenter), non matching lessicale rigido.

## MCP
- Se per velocizzare o migliorare la qualit� delle risposte serve installare un server MCP, chiedi sempre prima conferma esplicita all'utente per installarlo.

## CallCenter - Modalita Conversazione (obbligatoria)
- Il `CallCenter` deve inviare ai modelli una conversazione reale (lista messaggi con ruolo `system` / `user` / `assistant`).
- E' vietato appiattire (flatten) la cronologia in un unico prompt testuale con prefissi tipo `[user]` / `[assistant]`.
- Nei retry NON reiniettare l'intera risposta fallita: aggiungere solo un nuovo messaggio con i soli errori dell'ultima risposta.
- Queste regole valgono sempre per chiamate primarie, retry e fallback.
- Errori storici modello nel `system`:
  - La lista degli errori piu commessi del modello/ruolo/agente va inserita solo nel primo messaggio `system` per quello specifico modello durante la singola chiamata `CallCenter`.
  - Non rigenerare la stessa lista a ogni retry sullo stesso modello.
  - Se subentra un modello di fallback diverso, costruire il `system` iniziale per quel modello una sola volta.
- Modifica comportamento retry/fallback:
  - Qualsiasi modifica a queste regole (history, `system`, errori aggiunti in conversazione, fallback) richiede SEMPRE permesso esplicito dell'utente prima di procedere.

## Collaborazione e Spirito Critico
- L'obiettivo principale e' la qualita' del progetto, non "avere ragione".
- Se la richiesta dell'utente contiene incongruenze, rischi tecnici o controindicazioni, devi segnalarli in modo chiaro e concreto prima di eseguire.
- Se esiste un approccio migliore, proponilo con pro/contro e impatto pratico; poi attendi la decisione finale dell'utente.
- La decisione finale resta dell'utente: una volta presa, eseguila senza discussioni ulteriori.
- Evita esecuzione cieca degli ordini quando emergono rischi evidenti, regressioni o incoerenze con i vincoli del progetto.
- Non usare soluzioni di ripiego "facili" per aggirare il problema tecnico richiesto. Se la soluzione corretta e' bloccata, incerta o comporta tradeoff rilevanti, fermati e chiedi esplicitamente all'utente come procedere.
- Non modificare la logica del programma per iniziativa autonoma "perche sembra sbagliata": limitarsi alle modifiche richieste esplicitamente dall'utente.
