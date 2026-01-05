-- Apply pending migrations manually to SQLite database

-- 20251220195450_AddStoryTitle
ALTER TABLE stories ADD COLUMN title TEXT;

-- 20251221075622_AddDisabledToTtsVoices  
ALTER TABLE tts_voices ADD COLUMN disabled INTEGER NOT NULL DEFAULT 0;

-- 20251223064452_AddStepTemplateFields
ALTER TABLE step_templates ADD COLUMN agent_id INTEGER;
ALTER TABLE step_templates ADD COLUMN voice_id INTEGER;

-- 20251227074900_AddSerieFieldsToStories
ALTER TABLE stories ADD COLUMN serie_id INTEGER;
ALTER TABLE stories ADD COLUMN serie_episode INTEGER;
CREATE INDEX IX_stories_serie_id ON stories(serie_id);

-- 20251231180000_RenameStoryToStoryRawAddTaggedFields
ALTER TABLE stories RENAME COLUMN story TO story_raw;
ALTER TABLE stories ADD COLUMN story_tagged TEXT;
ALTER TABLE stories ADD COLUMN story_tagged_version INTEGER;
ALTER TABLE stories ADD COLUMN formatter_model INTEGER;
ALTER TABLE stories ADD COLUMN formatter_prompt_hash TEXT;

-- 20260101183000_AddSeriesCharacters
CREATE TABLE series_characters (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    serie_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    gender TEXT NOT NULL,
    description TEXT,
    voice_id INTEGER,
    episode_in INTEGER,
    episode_out INTEGER,
    image TEXT,
    aspect TEXT
);
CREATE INDEX IX_series_characters_serie_id ON series_characters(serie_id);
CREATE INDEX IX_series_characters_voice_id ON series_characters(voice_id);

-- Update migrations history
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) 
VALUES ('20251220195450_AddStoryTitle', '10.0.0');

INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) 
VALUES ('20251221075622_AddDisabledToTtsVoices', '10.0.0');

INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) 
VALUES ('20251223064452_AddStepTemplateFields', '10.0.0');

INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) 
VALUES ('20251227074900_AddSerieFieldsToStories', '10.0.0');

INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) 
VALUES ('20251231180000_RenameStoryToStoryRawAddTaggedFields', '10.0.0');

INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) 
VALUES ('20260101183000_AddSeriesCharacters', '10.0.0');
