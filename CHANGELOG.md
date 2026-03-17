# Changelog

## 2026-03-13 - Runtime tables -> in-memory caches

- Converted the following runtime-only tables to process-local in-memory caches (no longer persisted by default):
  - `narrative_story_blocks` (now in `_narrativeBlocksCache`)
  - `narrative_agent_calls_log` (now in `_narrativeAgentCallsCache`)
  - `narrative_planning_state` (now in `_narrativePlanningStatesCache`, with DB fallback)
  - `chunk_facts` / `story_chunk_facts` (now in `_chunkFactsCache`)
  - `story_resource_states` was converted to in-memory and the *final* snapshot is persisted on story completion.

- Database changes:
  - Backups created under `data/` before destructive operations.
  - Dropped physical tables: `narrative_agent_calls_log`, `narrative_planning_state`, `chunk_facts`, `story_chunk_facts`, `narrative_story_blocks`.
  - Updated SQL scripts under `scripts/` to comment-out creation/indexes for the removed tables.

- Code changes:
  - `Code/Services/DatabaseService.cs`: added in-memory caches and replaced persistence methods for the above tables; added DB flush of final `StoryResourceState` in `CompleteStateDrivenStory`.
  - `data/TinyGeneratorDbContext.cs`: commented-out `DbSet<>` mappings for the converted tables.
  - Tests updated to be tolerant to schema changes; test suite passes locally (62/62).

- Notes & recommendations:
  - `StoryResourceState` final snapshots are persisted to allow story continuation across restarts.
  - If you prefer persisted planning states or agent call logs, we can add periodic flush or selective persistence.
