INSERT INTO step_templates (id, name, task_type, step_prompt, description, created_at, updated_at, instructions) VALUES (1, 'story_9_chapters', 'story', '1. Scrivi la trama dettagliata (minimo 1500 parole) divisa in 6 capitoli, solo la trama NON la storia. {{PROMPT}}
2. Genera la lista completa dei PERSONAGGI con nome, sesso, età approssimativa, ruolo e carattere di questa trama che hai scritto tu nello step precedente: {{STEP_1}}.
3. Genera la STRUTTURA dettagliata di ogni capitolo (scene, eventi, dialoghi previsti).
4. Scrivi il CAPITOLO 1 (minimo 2500 parole). Prima di scrivere ciascuna frase scrivi chi la sta pronunciando [NARRATORE] o [NOME PERSONAGGIO, EMOZIONE]. Contesto: {{STEP_1}}, {{STEP_2}}, {{STEP_3_EXTRACT:Capitolo 1}}
5. Scrivi il CAPITOLO 2 (minimo 2500 parole).Prima di scrivere ciascuna frase scrivi chi la sta pronunciando [NARRATORE] o [NOME PERSONAGGIO, EMOZIONE]. Contesto: {{STEP_1}}, {{STEP_2}}, {{STEP_3_EXTRACT:Capitolo 2}}, {{STEP_4_SUMMARY}}
6. Scrivi il CAPITOLO 3 (minimo 2500 parole).Prima di scrivere ciascuna frase scrivi chi la sta pronunciando [NARRATORE] o [NOME PERSONAGGIO, EMOZIONE]. Contesto: {{STEP_1}}, {{STEP_2}}, {{STEP_3_EXTRACT:Capitolo 3}}, {{STEPS_4-5_SUMMARY}}
7. Scrivi il CAPITOLO 4 (minimo 2500 parole).Prima di scrivere ciascuna frase scrivi chi la sta pronunciando [NARRATORE] o [NOME PERSONAGGIO, EMOZIONE]. Contesto: {{STEP_1}}, {{STEP_2}}, {{STEP_3_EXTRACT:Capitolo 4}}, {{STEPS_4-6_SUMMARY}}
8. Scrivi il CAPITOLO 5 (minimo 2500 parole).Prima di scrivere ciascuna frase scrivi chi la sta pronunciando [NARRATORE] o [NOME PERSONAGGIO, EMOZIONE]. Contesto: {{STEP_1}}, {{STEP_2}}, {{STEP_3_EXTRACT:Capitolo 5}}, {{STEPS_4-7_SUMMARY}}
9. Scrivi il CAPITOLO 6 (minimo 2500 parole).Prima di scrivere ciascuna frase scrivi chi la sta pronunciando [NARRATORE] o [NOME PERSONAGGIO, EMOZIONE]. Contesto: {{STEP_1}}, {{STEP_2}}, {{STEP_3_EXTRACT:Capitolo 6}}, {{STEPS_4-8_SUMMARY}}', 'Standard 9-step story generation with 6 chapters', '2025-11-30 11:50:45', '2025-12-08T11:04:07.8543430Z', NULL);
INSERT INTO step_templates (id, name, task_type, step_prompt, description, created_at, updated_at, instructions) VALUES (2, 'coherence_evaluation', 'evaluation', '1. Inizia da part_index=0 e leggi il chunk con read_story_part(part_index=N).
2. Per ogni chunk N: estrai i FATTI OGGETTIVI (personaggi, luoghi, eventi, timeline) con extract_chunk_facts(chunk_number=N,...).
3. Recupera i fatti dei chunk precedenti con get_all_previous_facts(up_to_chunk=N).
4. Confronta i fatti del chunk corrente con quelli precedenti e calcola:
   - LOCAL_COHERENCE (0.0-1.0): coerenza con il chunk precedente
   - GLOBAL_COHERENCE (0.0-1.0): coerenza con tutta la storia fin qui
   Salva gli score con calculate_coherence(chunk_number=N, local_coherence=X, global_coherence=Y, errors="eventuali incoerenze").
5. Incrementa N e ripeti i passi 1-4 finché read_story_part restituisce is_last=true (non inventare chunk).
6. Alla fine chiama finalize_global_coherence(global_coherence=X, notes="sintesi analisi") per salvare il punteggio globale.', 'Template per valutazione coerenza chunk-by-chunk di storie lunghe', '2025-12-02 06:08:26', '2025-12-02 06:08:26', NULL);
INSERT INTO step_templates (id, name, task_type, step_prompt, description, created_at, updated_at, instructions) VALUES (3, 'action_pacing_evaluation', 'evaluation', '1. Inizia da part_index=0 e leggi il chunk con read_story_part(part_index=N).
2. Per ogni chunk N:
   - Estrai il profilo azione/ritmo con extract_action_profile(chunk_number=N, chunk_text=...).
   - Calcola:
     • ACTION_SCORE (0.0–1.0): quantità/qualità dell’azione
     • PACING_SCORE (0.0–1.0): ritmo narrativo del chunk
   - Registra i punteggi con calculate_action_pacing(chunk_number=N, action_score=X, pacing_score=Y, notes="osservazioni brevi").
3. Incrementa N e ripeti i passi finché read_story_part restituisce is_last=true (non inventare chunk).
4. Alla fine chiama finalize_global_pacing(pacing_score=Z, notes="sintesi analisi") per salvare il punteggio globale del capitolo.', 'Template valutazione azione/ritmo (pacing) chunk-by-chunk', '2025-12-02 20:37:37', '2025-12-02 20:37:37', NULL);
INSERT INTO step_templates (id, name, task_type, step_prompt, description, created_at, updated_at, instructions) VALUES (4, 'tts_schema_chunkwise', 'tts_schema', '1) Leggi il chunk corrente con read_story_part(part_index=N) partendo da 0.
2) Per ogni frase nel chunk usa i tool: add_phrase(character=<nome>, text=<frase identica>, emotion=<opzionale>) per battute e add_narration(text=<frase identica>) per la narrazione. Non parafrasare né riassumere.
3) Verifica che la timeline copra almeno il 90% del chunk. Se la copertura è insufficiente, correggi e ripeti (max 3 tentativi) prima di leggere il chunk successivo.
4) Incrementa part_index e ripeti finché is_last=true. Non passare al chunk successivo finché il chunk corrente non è coperto.
5) Dopo l''ultimo chunk, chiama ESATTAMENTE UNA volta confirm per salvare tts_schema.json.', 'Pipeline chunk-per-chunk per generare tts_schema con copertura minima per chunk e confirm finale.', '2025-12-04 18:19:18', '2025-12-04T18:22:13.0959990Z', NULL);
INSERT INTO step_templates (id, name, task_type, step_prompt, description, created_at, updated_at, instructions) VALUES (5, 'tts_schema_chunk_fixed20', 'tts_schema', '1) {{CHUNK_1}}
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

', 'Template 20-step: ogni step contiene il testo del chunk (CHUNK_n) fornito dal sistema; l agente lavora solo sul chunk corrente.
', '2025-12-04 18:29:07', '2025-12-07T18:05:11.7557230Z', 'Leggi attentamente il testo e trascrivilo integralmente nel formato seguente, senza riassumere o saltare frasi, senza aggiungere note o testo extra.

Usa SOLO queste sezioni ripetute nell’ordine del testo:

[NARRATORE]
Testo narrativo così come appare nel testo

[PERSONAGGIO: NomePersonaggio | EMOZIONE: emotion]
Battuta di dialogo così come appare nel testo

Regole:
- NON cambiare lingua, NON abbreviare, NON riassumere.
- Se non è chiaramente un dialogo, usa NARRATORE.
- EMOZIONE: usa una tra neutral, happy, sad, angry, fearful, disgusted, surprised (default neutral se non indicata).
- Non aggiungere spiegazioni o altro testo fuori dai blocchi.
- Copri tutto il testo, più blocchi uno dopo l’altro finché il testo è esaurito.
');
