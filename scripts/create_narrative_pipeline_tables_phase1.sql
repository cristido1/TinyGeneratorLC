CREATE TABLE IF NOT EXISTS narrative_continuity_state (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    story_id INTEGER NOT NULL,
    series_id INTEGER NULL,
    episode_id INTEGER NULL,
    chapter_id INTEGER NULL,
    scene_id INTEGER NULL,
    timeline_index INTEGER NOT NULL DEFAULT 0,
    state_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_narrative_continuity_state_story ON narrative_continuity_state(story_id, id DESC);
CREATE INDEX IF NOT EXISTS ix_narrative_continuity_state_series_episode ON narrative_continuity_state(series_id, episode_id, id DESC);

-- Narrative story blocks are now handled in-memory; table creation removed
-- CREATE TABLE IF NOT EXISTS narrative_story_blocks (
--     id INTEGER PRIMARY KEY AUTOINCREMENT,
--     story_id INTEGER NOT NULL,
--     series_id INTEGER NULL,
--     episode_id INTEGER NULL,
--     chapter_id INTEGER NULL,
--     scene_id INTEGER NULL,
--     block_index INTEGER NOT NULL,
--     text_content TEXT NOT NULL,
--     continuity_state_id INTEGER NULL,
--     quality_score REAL NULL,
--     coherence_score REAL NULL,
--     created_at TEXT NOT NULL,
--     FOREIGN KEY (continuity_state_id) REFERENCES narrative_continuity_state(id)
-- );
-- CREATE INDEX IF NOT EXISTS ix_narrative_story_blocks_story ON narrative_story_blocks(story_id, block_index);
-- CREATE INDEX IF NOT EXISTS ix_narrative_story_blocks_cont_state ON narrative_story_blocks(continuity_state_id);

-- Narrative agent calls log is now handled in-memory; table creation removed
-- CREATE TABLE IF NOT EXISTS narrative_agent_calls_log (
--     id INTEGER PRIMARY KEY AUTOINCREMENT,
--     story_id INTEGER NOT NULL,
--     agent_name TEXT NOT NULL,
--     input_tokens INTEGER NULL,
--     output_tokens INTEGER NULL,
--     deterministic_checks_result TEXT NULL,
--     response_checker_result TEXT NULL,
--     retry_count INTEGER NOT NULL DEFAULT 0,
--     latency_ms INTEGER NULL,
--     created_at TEXT NOT NULL
-- );
-- CREATE INDEX IF NOT EXISTS ix_narrative_agent_calls_log_story ON narrative_agent_calls_log(story_id, created_at DESC);
-- CREATE INDEX IF NOT EXISTS ix_narrative_agent_calls_log_agent ON narrative_agent_calls_log(agent_name, created_at DESC);

-- Narrative planning state is now handled in-memory with DB fallback; table creation removed
-- CREATE TABLE IF NOT EXISTS narrative_planning_state (
--     id INTEGER PRIMARY KEY AUTOINCREMENT,
--     series_id INTEGER NOT NULL,
--     episode_id INTEGER NULL,
--     planning_json TEXT NOT NULL,
--     created_at TEXT NOT NULL
-- );
-- CREATE INDEX IF NOT EXISTS ix_narrative_planning_state_series_episode ON narrative_planning_state(series_id, episode_id, id DESC);
