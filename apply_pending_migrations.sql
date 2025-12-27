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

-- Update migrations history
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) 
VALUES ('20251220195450_AddStoryTitle', '10.0.0');

INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) 
VALUES ('20251221075622_AddDisabledToTtsVoices', '10.0.0');

INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) 
VALUES ('20251223064452_AddStepTemplateFields', '10.0.0');

INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) 
VALUES ('20251227074900_AddSerieFieldsToStories', '10.0.0');
