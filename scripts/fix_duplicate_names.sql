-- Fix duplicate names by copying voice_id to name field for voices with 'Claribel Dervia'
UPDATE tts_voices 
SET name = voice_id 
WHERE name = 'Claribel Dervia';

SELECT 'Fixed ' || changes() || ' records with duplicate names.' AS result;
