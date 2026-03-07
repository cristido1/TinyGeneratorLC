-- Add image column to models table to store small model-family image reference
PRAGMA foreign_keys=off;
BEGIN TRANSACTION;
-- Add column (SQLite supports ADD COLUMN)
ALTER TABLE models ADD COLUMN image TEXT;
COMMIT;
PRAGMA foreign_keys=on;

-- Note: run this script against your SQLite DB file (e.g. data/storage.db)
-- Example:
-- sqlite3 data/storage.db < scripts/add_models_image.sql
