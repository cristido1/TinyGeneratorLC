-- Aggiunge il flag enabled alla tabella sounds (default true).
-- SQLite non supporta IF NOT EXISTS su ADD COLUMN in tutte le versioni, quindi verificare prima.

ALTER TABLE sounds ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1;

-- Normalizza eventuali valori null (difensivo)
UPDATE sounds
SET enabled = 1
WHERE enabled IS NULL;
