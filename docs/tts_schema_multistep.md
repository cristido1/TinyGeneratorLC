## Piano multistep per generazione `tts_schema`

Nota importante (stato attuale del progetto): questo piano e' **legacy** e presuppone tool-calls. Con `ToolCalling:Enabled=false` (default) i tool non vengono esposti ai modelli, quindi questo flusso non e' attivo finche' non viene migrato a output TAG-only e/o a un flusso deterministico senza tool-calls.

Obiettivo: costruire lo schema TTS lavorando chunk per chunk, assicurando copertura quasi completa del testo ad ogni step prima di procedere.

### Flusso proposto
1. **Leggi chunk** con `read_story_part(part_index=N)` (chunk boundaries già safe: non spezzano frasi).
2. **Produci tool_calls sul chunk corrente**:
   - `add_phrase` per ogni battuta (character, text identico, emotion opzionale)
   - `add_narration` per la narrazione (text identico)
   - Nessun JSON libero, solo tool_calls.
3. **Validazione coverage per chunk**:
   - Calcola copertura del chunk tramite `HasCoveredCurrentChunk` (>=90% richiesto).
   - Se copertura insufficiente, invia prompt di correzione e ripeti sullo stesso chunk.
   - Massimo 3 tentativi per chunk; se esauriti, aborta la pipeline.
4. **Prosegui al chunk successivo** solo se copertura OK.
5. **Quando `is_last=true`** e tutti i chunk sono coperti, chiama `confirm` esattamente una volta per salvare `tts_schema.json`.

### Note implementative
- Lo splitting dei chunk è gestito da `StoryChunkHelper.SplitIntoChunks(...)`, ora con target ~1800 caratteri, cercando boundary su punto o newline per evitare frasi spezzate.
- `ReActLoopOrchestrator` ha un guardrail che blocca il passaggio al chunk successivo se la copertura del chunk corrente è <90%.
- Per integrazione con `MultiStepOrchestrationService`, creare uno step template che richiami il flusso sopra (tool: `ttsschema`, `read_story_part`, `add_narration`, `add_phrase`, `confirm`) e includa un reminder dopo ogni step in caso di copertura insufficiente.

### Prompt suggerito per l’agente TTS
```
Sei un trascrittore TTS. Usa solo i tool.
1) read_story_part in ordine finché is_last=true.
2) Per ogni frase letta:
   - personaggio: add_phrase(character=<nome>, text=<frase identica>, emotion=<opzionale>);
   - narrazione: add_narration(text=<frase identica>);
   Non parafrasare né riassumere.
   Lavora chunk per chunk: completa le chiamate per il chunk corrente prima di passare al successivo.
3) Quando hai coperto tutta la storia, chiama ESATTAMENTE UNA volta confirm per salvare tts_schema.json.

Regole:
- Non generare JSON o markdown; non stampare schema a video.
- Non chiedere story_id (è gestito internamente).
- Se il personaggio non è certo, trattalo come narrazione.
- Usa solo i tool; niente testo libero in output.
```
