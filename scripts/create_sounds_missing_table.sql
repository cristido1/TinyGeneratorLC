-- Crea la tabella per tracciare richieste audio non soddisfatte dalla libreria sounds.
-- Eseguire su SQLite (es. data/storage.db) dopo backup del file DB se vuoi essere prudente.

CREATE TABLE IF NOT EXISTS sounds_missing (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    type          TEXT    NOT NULL, -- fx | amb | music
    prompt        TEXT    NOT NULL,
    tags          TEXT    NULL,
    story_id      INTEGER NULL,
    story_title   TEXT    NULL,
    source        TEXT    NULL,
    occurrences   INTEGER NOT NULL DEFAULT 1,
    status        TEXT    NOT NULL DEFAULT 'open', -- open | resolved | ignored
    first_seen_at TEXT    NOT NULL,
    last_seen_at  TEXT    NOT NULL,
    notes         TEXT    NULL
);

CREATE INDEX IF NOT EXISTS idx_sounds_missing_status_type
    ON sounds_missing(status, type);

CREATE INDEX IF NOT EXISTS idx_sounds_missing_last_seen
    ON sounds_missing(last_seen_at DESC);

CREATE INDEX IF NOT EXISTS idx_sounds_missing_story
    ON sounds_missing(story_id);
