-- Apply AddSummaryToStories migration manually
ALTER TABLE stories ADD COLUMN summary TEXT;

-- Register migration
INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion) 
VALUES ('20251227082500_AddSummaryToStories', '10.0.0');
