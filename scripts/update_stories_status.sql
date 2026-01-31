BEGIN TRANSACTION;

DELETE FROM stories_status;

INSERT INTO stories_status (id, code, description, step, color, operation_type, agent_type, function_name, caption_to_execute) VALUES
(1, 'inserted', 'Story inserted into the system', 1, 'grey', 'none', 'none', NULL, NULL),
(2, 'revised', 'Story revised from story_raw to story_revised', 2, 'steelblue', 'agent_call', 'revisor', 'revise_story', 'Revisione'),
(3, 'evaluated', 'Story evaluated by at least two validators', 3, 'purple', 'function_call', 'none', 'evaluate_story', 'Valuta'),
(4, 'tagged_voice', 'Story tagged with voice and emotion metadata', 4, 'teal', 'function_call', 'none', 'add_voice_tags_to_story', 'Aggiungi TAG VOCE'),
(5, 'tagged_ambient', 'Ambient tags [RUMORI] added to story_tagged', 5, 'olive', 'function_call', 'none', 'add_ambient_tags_to_story', 'Aggiungi RUMORI'),
(6, 'tagged_fx', 'FX tags [FX] added to story_tagged', 6, 'orange', 'function_call', 'none', 'add_fx_tags_to_story', 'Aggiungi FX'),
(7, 'tagged', 'Tagging completed with MUSIC blocks and ready for audio sequencing', 7, 'purple', 'function_call', 'none', 'add_music_tags_to_story', 'Aggiungi MUSICA'),
(8, 'tts_schema_generated', 'TTS schema JSON generated from story_revised', 8, 'blue', 'function_call', 'none', 'prepare_tts_schema', 'Genera TTS schema'),
(9, 'tts_generated', 'TTS audio files generated and stored in tts_schema.json', 9, 'navy', 'function_call', 'none', 'generate_tts_audio', 'Genera TTS'),
(10, 'ambient_generated', 'Ambient audio files generated and stored in tts_schema.json', 10, 'wheat', 'function_call', 'none', 'generate_ambience_audio', 'Genera audio ambientale'),
(11, 'fx_generated', 'FX audio files generated and stored in tts_schema.json', 11, 'gold', 'function_call', 'none', 'generate_fx_audio', 'Genera effetti sonori'),
(12, 'music_generated', 'Music audio files generated and stored in tts_schema.json', 12, 'darkorange', 'function_call', 'none', 'generate_music_audio', 'Genera musica'),
(13, 'audio_master_generated', 'Final mixed audio generated', 13, 'green', 'function_call', 'none', 'generate_audio_master', 'Mix audio finale');

COMMIT;
