INSERT OR IGNORE INTO roles(name, linked_command, created_at, updated_at) VALUES ('state_extractor','NarrativePipeline','2026-02-26T15:35:42.5910417Z','2026-02-26T15:35:42.5910417Z');
INSERT OR IGNORE INTO roles(name, linked_command, created_at, updated_at) VALUES ('story_editor_non_creative','NarrativePipeline','2026-02-26T15:35:42.5910417Z','2026-02-26T15:35:42.5910417Z');
INSERT INTO agents(name, role, model_id, prompt, instructions, is_active, created_at, updated_at, notes)
SELECT 'State Extractor', 'state_extractor', m.Id, '', 'Aggiorna esclusivamente lo stato narrativo strutturato a partire dal blocco narrativo e dallo stato precedente. Non scrivere testo narrativo. Rispondi solo nel formato richiesto dalla request.', 1, '2026-02-26T15:35:42.5910417Z', '2026-02-26T15:35:42.5910417Z', 'Narrative pipeline phase1 placeholder'
FROM models m
WHERE m.Enabled=1 AND NOT EXISTS (SELECT 1 FROM agents a WHERE lower(a.name)=lower('State Extractor'))
ORDER BY m.Id LIMIT 1;
INSERT INTO agents(name, role, model_id, prompt, instructions, is_active, created_at, updated_at, notes)
SELECT 'Story Editor Non Creative', 'story_editor_non_creative', m.Id, '', 'Sei uno StoryEditor non creativo: migliori stile, fluidita e ridondanze senza cambiare eventi, outcome o continuity. Rispondi solo nel formato richiesto dalla request.', 1, '2026-02-26T15:35:42.5910417Z', '2026-02-26T15:35:42.5910417Z', 'Narrative pipeline phase1 placeholder'
FROM models m
WHERE m.Enabled=1 AND NOT EXISTS (SELECT 1 FROM agents a WHERE lower(a.name)=lower('Story Editor Non Creative'))
ORDER BY m.Id LIMIT 1;
