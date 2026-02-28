-- Backup DB file before running this script.
-- Purpose: unify resource persistence into a single table for all story generation flows.

BEGIN TRANSACTION;

ALTER TABLE story_resource_states RENAME TO story_resource_states_legacy_20260227;

CREATE TABLE story_resource_states (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    story_id INTEGER NOT NULL,
    series_id INTEGER NULL,
    episode_number INTEGER NULL,
    chunk_index INTEGER NOT NULL,
    is_initial INTEGER NOT NULL DEFAULT 0,
    is_final INTEGER NOT NULL DEFAULT 0,
    source_engine TEXT NOT NULL DEFAULT 'state_driven',
    canon_state_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY (story_id) REFERENCES stories(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_story_resource_states_story_chunk
    ON story_resource_states(story_id, chunk_index);

CREATE INDEX IF NOT EXISTS IX_story_resource_states_series
    ON story_resource_states(series_id);

DROP TABLE IF EXISTS story_runtime_states;

INSERT INTO roles (ruolo, comando_collegato, created_at, updated_at)
SELECT 'resource_manager', 'ResourceManager', datetime('now'), datetime('now')
WHERE NOT EXISTS (SELECT 1 FROM roles WHERE lower(ruolo) = lower('resource_manager'));

UPDATE agents
SET json_response_format = 'resource_manager_state.json'
WHERE lower(role) = lower('resource_manager');

DROP TABLE IF EXISTS story_resource_states_legacy_20260227;

COMMIT;
