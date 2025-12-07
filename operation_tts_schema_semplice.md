# Operazione `tts_schema` — Descrizione sintetica

L'operazione tts_schema chiede ad un agente di tipo tts_schema di
leggere un testo e tradurlo in uno schema pronto per essere passato al servizio tts. L'agente delve leggere ogni frase della storie e aggiungerla allo schema usando le funzioni add_narration([phrase text]) e o add_phrase([character],[phrase text], [emotion]).

In teoria avrei voluto passare all'agente lo skill di scrivere su file,
la struttura finale da creare e la struttura del file json finale, ma
era una operazione troppo complessa per i modelli (max 8b) che uso e 
allora ho pensato:
1) astrarre la creazione del file json e condensarla in due funzioni. E' il programma che riceve le funzioni e compone una classe TssSchema con quello che viene passato dalle funzioni e poi alla fine genera il file json.
2) poichè le storie sono lunghe ho diviso il testo in chunk di circa 1500 caratteri facendo in modo che non spezzino frasi. Viene passato un chunk alla volta.
3) ho sviluppato una modalità di richiesta che ho chiamato step prompt, dove all'agente viene passato uno step (prompt) alla volta e tra uno step ed un altro NON viene mantenuta la memoria della conversazione. Nel prompt chi possono essere dei placeholder che ad esempio iniettano il riassunto degli step precedenti o un chunk di storia.

# Fase 3
Gli step prompt sono memorizzati nella tabella step_templates, nel caso dell'operazione tts_schema usiamo lo step prompt di nome tts_schema_chunk_fixed20, che nel campo con gli step continene questi 20 prompt:
1) {{CHUNK_1}}
2) {{CHUNK_2}}
3) {{CHUNK_3}}
4) {{CHUNK_4}}
5) {{CHUNK_5}}
6) {{CHUNK_6}}
7) {{CHUNK_7}}
8) {{CHUNK_8}}
9) {{CHUNK_9}}
10) {{CHUNK_10}}
11) {{CHUNK_11}}
12) {{CHUNK_12}}
13) {{CHUNK_13}}
14) {{CHUNK_14}}
15) {{CHUNK_15}}
16) {{CHUNK_16}}
17) {{CHUNK_17}}
18) {{CHUNK_18}}
19) {{CHUNK_19}}
20) {{CHUNK_20}}

Sul campo instructions dell'agente di tipo tts_schema ci sono queste istruzioni:
###########################################################
Leggi attentamente il testo di una storia fornito dall’utente.

Il tuo compito è TRASCRIVERE esattamente ogni singola frase della storia nell’ordine in cui appare,
senza saltare nulla, senza abbreviare, senza riassumere e senza modificare neanche una parola, senza cambiare lingua.

Per ogni frase:
- Se è NARRAZIONE → usa add_narration({ "text": "..." })
- Se è DIALOGO parlato da un personaggio → usa add_phrase({ "character": "...", "emotion": "neutral", "text": "..." })

REGOLE OBBLIGATORIE
1. NON indovinare personaggi, luoghi o emozioni.  
   Se non è chiaramente un dialogo, trattalo come narrazione.

2. NON combinare frasi diverse in una sola tool_call.

3. NON aggiungere testo fuori dalle tool_call.

4. Ripeti la trascrizione finché NON hai convertito tutto il testo dall’inizio alla fine.
   Non fermarti dopo 1 o 2 frasi.

5. NON restituire “done”: true finché non hai trascritto l’intero chunk.

6. NON aggiungere spiegazioni, commenti o testo esterno.
   L’output deve essere composto SOLO da tool_calls.
#############################################################

Il flusso del programma nel caso di tts_schema dovrebbe essere questo, ma attenzione che il multistep non viene usato solo da questa operazione
1) all'operazione vengono passati i parametri storia_id e agente_id
   da questi possiamo recuperare il testo della storia che nel caso di operazione tts_schema viene caricata e vengono preparati tutti i chunk stando attenti a non spezzare frasi.
2) viene inizializzato l'agente, che tra i suoi skill/tool ha il TtsSchemaTool che viene caricato
S1.1) parte lo step 1, nella richiesta vengono messe come messaggio system le instructions dell'agente. Nel prompt viene messo il prompt dello step 1 e quindi viene decodificato il placeholder dello step 1 ed in pratica nel prompt c'è il primo chunk di storie e niente altro.
S1.2) il modello dovrebbe rispondere con la sola risposta delle function call, con tutte un una risposta sola.
S1.3) il programma controlla che nella risposta ci sia almeno il 90% del testo originale.
S1 riuscito) Se c'è almeno il 90% del testo originale lo step viene considerato concluso positivamente, viene salvata la struttura parziale che è stata inviata dal modello e si parte con il prossimo step. Non servono altre request/response di conferma.
S1 fallito) se non c'è almeno il 90% del testo originale lo step è fallito, viene mandata una nuova request, che mantiene la storia della precedente richiesta e credo debba integrare anche le function-call della risposta dell'agente, ed in più viene aggiunto l'errore con un nuovo messaggio system (oppure assistant, non ho capito di solito cosa si usa e se si possa fare un solo messaggio iniziale da system) e viene mandata la nuova request all'agente. Se l'agente fallisce tre volte su un singolo step l'intera operazione è fallita.
Step 2) La richiesta dello step 2 non deve contenere riferimenti allo step precedente, la storia della conversazione precedente non viene mantenuta, quindi si fa una request con istruction dell'agente e prompt con il prompt dello step 2 che è il chunk 2 dela storia.

Si procede con gli step successivi, e si accodano i testi estratti dall'agente.

L'operazione termina positivamente quando non ci sono più chunk residui, quando succede il programma genera il file tts_schema.json nella cartella della storia.







