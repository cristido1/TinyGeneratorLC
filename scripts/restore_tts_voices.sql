-- Script to restore TTS voices from backup database
-- This will update existing records with data from the backup, preserving voice_id as primary key

-- Attach backup database
ATTACH DATABASE 'data/storage - Copia (2).db' AS backup;

-- Update existing voices with backup data (including model field)
UPDATE tts_voices
SET 
    name = COALESCE((SELECT name FROM backup.tts_voices WHERE voice_id = tts_voices.voice_id), name),
    model = COALESCE((SELECT model FROM backup.tts_voices WHERE voice_id = tts_voices.voice_id), model),
    language = COALESCE((SELECT language FROM backup.tts_voices WHERE voice_id = tts_voices.voice_id), language),
    gender = COALESCE((SELECT gender FROM backup.tts_voices WHERE voice_id = tts_voices.voice_id), gender),
    age = COALESCE((SELECT age FROM backup.tts_voices WHERE voice_id = tts_voices.voice_id), age),
    confidence = COALESCE((SELECT confidence FROM backup.tts_voices WHERE voice_id = tts_voices.voice_id), confidence),
    score = COALESCE((SELECT score FROM backup.tts_voices WHERE voice_id = tts_voices.voice_id), score),
    tags = COALESCE((SELECT tags FROM backup.tts_voices WHERE voice_id = tts_voices.voice_id), tags),
    template_wav = COALESCE((SELECT template_wav FROM backup.tts_voices WHERE voice_id = tts_voices.voice_id), template_wav),
    archetype = COALESCE((SELECT archetype FROM backup.tts_voices WHERE voice_id = tts_voices.voice_id), archetype),
    notes = COALESCE((SELECT notes FROM backup.tts_voices WHERE voice_id = tts_voices.voice_id), notes),
    disabled = COALESCE((SELECT disabled FROM backup.tts_voices WHERE voice_id = tts_voices.voice_id), disabled)
WHERE voice_id IN (SELECT voice_id FROM backup.tts_voices);

-- Detach backup database
DETACH DATABASE backup;

SELECT 'Restore completed. Updated ' || changes() || ' records.' AS result;
