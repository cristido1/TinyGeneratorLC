BEGIN TRANSACTION;

DELETE FROM stories_status;

INSERT INTO stories_status (id, code, description, step, color, operation_type, agent_type, function_name, caption_to_execute) VALUES
(1, 'inserted', 'Story inserted into the system', 1, 'grey', 'none', 'none', NULL, NULL),
(2, 'revised', 'Story revised from story_raw to story_revised', 2, 'steelblue', 'agent_call', 'revisor', 'revise_story', 'Revisione'),
(3, 'evaluated', 'Story evaluated by at least two validators', 3, 'purple', 'function_call', 'none', 'evaluate_story', 'Valuta'),
(4, 'tagged', 'Story tagged from story_revised to story_tagged', 4, 'teal', 'agent_call', 'formatter', 'transform_story_raw_to_tagged', 'Aggiungi TAG'),
(5, 'tts_schema_generated', 'TTS schema JSON generated from story_revised', 5, 'blue', 'function_call', 'none', 'prepare_tts_schema', 'Genera TTS schema'),
(6, 'tts_generated', 'TTS audio files generated and stored in tts_schema.json', 6, 'navy', 'function_call', 'none', 'generate_tts_audio', 'Genera TTS'),
(7, 'ambient_generated', 'Ambient audio files generated and stored in tts_schema.json', 7, 'wheat', 'function_call', 'none', 'generate_ambience_audio', 'Genera audio ambientale'),
(8, 'fx_generated', 'FX audio files generated and stored in tts_schema.json', 8, 'gold', 'function_call', 'none', 'generate_fx_audio', 'Genera effetti sonori'),
(9, 'music_generated', 'Music audio files generated and stored in tts_schema.json', 9, 'darkorange', 'function_call', 'none', 'generate_music_audio', 'Genera musica'),
(10, 'audio_master_generated', 'Final mixed audio generated', 10, 'green', 'function_call', 'none', 'generate_audio_master', 'Mix audio finale');

COMMIT;
