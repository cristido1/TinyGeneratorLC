TITOLO: Layout “Conversazione Operativa tra Agenti/Comandi” (Generico)

OBIETTIVO
Creare una vista log universale in formato conversazione tra attori del sistema (agenti AI, comandi, orchestratore, servizi, database, API), utilizzabile per QUALSIASI operazione del sistema, non solo per le storie NRE.

La vista deve rappresentare il flusso reale di esecuzione come dialogo tra componenti.

---

1. ATTORI DEL SISTEMA (RUOLI CONVERSAZIONE)

Ogni log deve essere mappato ad un attore:

* Agent AI → 🤖 (writer, checker, evaluator, formatter, ecc)
* Command (ICommand) → 🧩
* Orchestrator / CallCenter → 🧭
* DeterministicCheck / Validator → ⚙
* Database / Repository → 🗄
* External API / Tool → 🌐
* Sistema / runtime → 💻

Il tipo attore è derivato da:
Source, Operazione, Agent, CommandName

---

2. MODELLO DATI GENERICO

LogEntry {
id
timestamp
source (Request | Response | System | Error)
operation (nome comando o operazione)
actor_name
actor_type
model (se presente)
result (SUCCESS | FAILED | WARNING | INFO)
duration_seconds
message (fail_reason o descrizione)
correlation_id (run id / story id / command id)
thread_id
payload_json (opzionale)
}

---

3. STRUTTURA PAGINA

HEADER

* filtri
* selezione correlation_id (es. StoryId, CommandId, RunId)

LAYOUT PRINCIPALE:

---

## | Conversazione Operativa (timeline verticale)      |

Ogni evento è un messaggio.

PANNELLO DETTAGLIO (a destra o collapsabile sotto)

---

4. FORMATO MESSAGGI (BOLLE CHAT)

Struttura visiva:

## [ICONA] NomeAttore (sottotipo/modello)   [timestamp]

Contenuto sintetico dell’azione
[badge stato] [durata]

Esempi:

## 🧩 Command: AddAmbientTagsToStory   [08:34:50]

Avvio comando
[INFO] [1s]

## 🤖 Agent: AmbientExpert (qwen3:8b)

Tag ambientali generati (5 tag)
[SUCCESS] [12s]

## ⚙ Validator: CheckNoDuplicateSentences

❌ Frasi duplicate rilevate
[FAILED] [16s]

## 🌐 API: Freesound

Ricerca suoni con tag: “wind, cave, echo”
[SUCCESS] [3s]

---

5. GENERAZIONE CONTENUTO TESTUALE

Regole:

Se esiste message/fail_reason → usare quello

Altrimenti generare automaticamente:

REQUEST:
"Richiesta inviata"

SUCCESS:
"Operazione completata"

FAILED:
"Operazione fallita"

WARNING:
"Attenzione: condizione non ottimale"

Per i Command:
"Avvio comando X"
"Fine comando X"

Per Agent:
"Output generato"
"Valutazione completata"

Per Database:
"Lettura dati"
"Scrittura dati"

Per API:
"Chiamata API X"
"Risposta ricevuta"

---

6. COLORI E STILI

Agent AI → blu chiaro
Command → viola chiaro
Orchestrator → grigio
Validator → giallo
Database → marroncino chiaro
API → azzurro
Errori → rosso
Successo → verde

Badge stato:
SUCCESS → verde
FAILED → rosso
WARNING → arancio
INFO → grigio

---

7. RAGGRUPPAMENTO PER CORRELATION_ID

La conversazione deve essere raggruppata per:

* StoryId
* CommandId
* RunId
* oppure qualsiasi correlation_id disponibile

Visuale:

▶ Run 000123
🧭 Orchestrator
🧩 Command
🤖 Agent
⚙ Validator
🌐 API
🗄 DB

Espandibile/collassabile

---

8. ORDINAMENTO

Ordinamento per timestamp crescente

Eventi con stesso timestamp → ordine di inserimento

---

9. FILTRI UTENTE

Filtri disponibili:

[ ] Solo errori
[ ] Solo warning
[ ] Solo agent
[ ] Solo comandi
[ ] Solo validator
[ ] Solo API
[ ] Solo DB
[ ] Durata > X
[ ] Modello = X
[ ] Actor = X

---

10. DETTAGLIO TECNICO

Click su un messaggio apre dettaglio con:

* payload_json (request/response)
* prompt (se agente AI)
* response raw
* stack error (se errore)
* parametri comando
* SQL eseguito (se DB)
* endpoint chiamato (se API)

---

11. OTTIMIZZAZIONI UX

* testo troncato a 2 righe + “mostra altro”
* durata colorata
* icone riconoscibili
* gruppi collapsabili
* evidenziare automaticamente il primo errore

---

12. MODALITÀ DOPPIA

Toggle:

[Vista Conversazione] | [Vista Tabellare Tecnica]

La vista tabellare è quella esistente.

---

13. KPI IN HEADER (OPZIONALE)

Mostrare:

* numero eventi
* numero errori
* tempo totale run
* attore più lento
* attore con più errori

---

14. OBIETTIVO FINALE

La UI deve permettere di:

* seguire il flusso operativo come una conversazione tra componenti
* individuare errori in pochi secondi
* capire la sequenza di azioni senza leggere log tecnici

Il sistema deve sembrare un team che collabora, non un dump di log.

---

FINE SPECIFICA
