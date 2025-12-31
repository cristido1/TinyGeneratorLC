-- Script per ricreare TUTTE le tabelle EF Core con struttura corretta
-- Per database con struttura legacy completamente diversa

-- IMPORTANTE: Questo script ELIMINA E RICREA tutte le tabelle EF Core
-- Fai BACKUP prima: copy data\storage.db data\storage.db.full-backup

BEGIN TRANSACTION;

-- ============================================================================
-- BACKUP DATI (opzionale - decommentare se vuoi salvare i dati)
-- ============================================================================

-- CREATE TABLE agents_backup AS SELECT * FROM agents WHERE 1=0;
-- CREATE TABLE models_backup AS SELECT * FROM models WHERE 1=0;
-- CREATE TABLE stories_backup AS SELECT * FROM stories WHERE 1=0;
-- CREATE TABLE tts_voices_backup AS SELECT * FROM tts_voices WHERE 1=0;

-- ============================================================================
-- DROP TABELLE ESISTENTI
-- ============================================================================

DROP TABLE IF EXISTS agents;
DROP TABLE IF EXISTS models;
DROP TABLE IF EXISTS stories;
DROP TABLE IF EXISTS stories_evaluations;
DROP TABLE IF EXISTS stories_status;
DROP TABLE IF EXISTS test_definitions;
DROP TABLE IF EXISTS tts_voices;
DROP TABLE IF EXISTS task_types;
DROP TABLE IF EXISTS step_templates;
DROP TABLE IF EXISTS Log;

-- ============================================================================
-- CREATE TABELLE CON STRUTTURA EF CORE CORRETTA
-- ============================================================================

-- agents
CREATE TABLE agents (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    role TEXT NOT NULL,
    model_id INTEGER NULL,
    voice_rowid INTEGER NULL,
    skills TEXT NULL,
    config TEXT NULL,
    json_response_format TEXT NULL,
    prompt TEXT NULL,
    instructions TEXT NULL,
    execution_plan TEXT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL,
    updated_at TEXT NULL,
    notes TEXT NULL,
    temperature REAL NULL,
    top_p REAL NULL,
    repeat_penalty REAL NULL,
    top_k INTEGER NULL,
    repeat_last_n INTEGER NULL,
    num_predict INTEGER NULL,
    multi_step_template_id INTEGER NULL,
    RowVersion BLOB NULL
);

-- models (usa PascalCase come da schema originale)
CREATE TABLE models (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Provider TEXT NOT NULL,
    Endpoint TEXT NULL,
    IsLocal INTEGER NOT NULL DEFAULT 1,
    MaxContext INTEGER NOT NULL DEFAULT 4096,
    ContextToUse INTEGER NOT NULL DEFAULT 4096,
    FunctionCallingScore INTEGER NOT NULL DEFAULT 0,
    CostInPerToken REAL NOT NULL DEFAULT 0,
    CostOutPerToken REAL NOT NULL DEFAULT 0,
    LimitTokensDay INTEGER NOT NULL DEFAULT 0,
    LimitTokensWeek INTEGER NOT NULL DEFAULT 0,
    LimitTokensMonth INTEGER NOT NULL DEFAULT 0,
    Metadata TEXT NOT NULL DEFAULT '',
    Enabled INTEGER NOT NULL DEFAULT 1,
    CreatedAt TEXT NULL,
    UpdatedAt TEXT NULL,
    TestDurationSeconds REAL NULL,
    NoTools INTEGER NOT NULL DEFAULT 0,
    WriterScore REAL NOT NULL DEFAULT 0,
    BaseScore REAL NOT NULL DEFAULT 0,
    TextEvalScore REAL NOT NULL DEFAULT 0,
    TtsScore REAL NOT NULL DEFAULT 0,
    MusicScore REAL NOT NULL DEFAULT 0,
    FxScore REAL NOT NULL DEFAULT 0,
    AmbientScore REAL NOT NULL DEFAULT 0,
    TotalScore REAL NOT NULL DEFAULT 0,
    Note TEXT NULL,
    LastTestResults TEXT NULL,
    LastMusicTestFile TEXT NULL,
    LastSoundTestFile TEXT NULL,
    LastTtsTestFile TEXT NULL,
    LastScore_Base INTEGER NULL,
    LastScore_Tts INTEGER NULL,
    LastScore_Music INTEGER NULL,
    LastScore_Write INTEGER NULL,
    LastResults_BaseJson TEXT NULL,
    LastResults_TtsJson TEXT NULL,
    LastResults_MusicJson TEXT NULL,
    LastResults_WriteJson TEXT NULL,
    RowVersion BLOB NULL
);

-- stories
CREATE TABLE stories (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    generation_id TEXT NOT NULL DEFAULT '',
    memory_key TEXT NOT NULL DEFAULT '',
    ts TEXT NOT NULL DEFAULT '',
    prompt TEXT NOT NULL DEFAULT '',
    story TEXT NOT NULL DEFAULT '',
    char_count INTEGER NOT NULL DEFAULT 0,
    eval TEXT NOT NULL DEFAULT '',
    score REAL NOT NULL DEFAULT 0,
    approved INTEGER NOT NULL DEFAULT 0,
    status_id INTEGER NULL,
    folder TEXT NULL,
    model_id INTEGER NULL,
    agent_id INTEGER NULL,
    RowVersion BLOB NULL
);

-- stories_evaluations
CREATE TABLE stories_evaluations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    story_id INTEGER NOT NULL,
    narrative_coherence_score INTEGER NOT NULL,
    narrative_coherence_defects TEXT NOT NULL,
    originality_score INTEGER NOT NULL,
    originality_defects TEXT NOT NULL,
    emotional_impact_score INTEGER NOT NULL,
    emotional_impact_defects TEXT NOT NULL,
    action_score INTEGER NOT NULL,
    action_defects TEXT NOT NULL,
    total_score REAL NOT NULL,
    raw_json TEXT NOT NULL,
    model_id INTEGER NULL,
    agent_id INTEGER NULL,
    ts TEXT NOT NULL,
    RowVersion BLOB NULL
);

-- stories_status
CREATE TABLE stories_status (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NULL,
    description TEXT NULL,
    step INTEGER NOT NULL,
    color TEXT NULL,
    operation_type TEXT NULL,
    agent_type TEXT NULL,
    function_name TEXT NULL,
    caption_to_execute TEXT NULL,
    RowVersion BLOB NULL
);

-- test_definitions
CREATE TABLE test_definitions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    test_group TEXT NULL,
    library TEXT NULL,
    allowed_plugins TEXT NULL,
    function_name TEXT NULL,
    prompt TEXT NULL,
    expected_behavior TEXT NULL,
    expected_asset TEXT NULL,
    test_type TEXT NULL,
    expected_prompt_value TEXT NULL,
    valid_score_range TEXT NULL,
    timeout_secs INTEGER NOT NULL DEFAULT 30000,
    priority INTEGER NOT NULL DEFAULT 1,
    execution_plan TEXT NULL,
    active INTEGER NOT NULL DEFAULT 1,
    json_response_format TEXT NULL,
    files_to_copy TEXT NULL,
    temperature REAL NULL,
    top_p REAL NULL,
    RowVersion BLOB NULL
);

-- tts_voices
CREATE TABLE tts_voices (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    voice_id TEXT NOT NULL,
    name TEXT NOT NULL,
    model TEXT NULL,
    language TEXT NULL,
    gender TEXT NULL,
    age TEXT NULL,
    confidence REAL NULL,
    score REAL NULL,
    tags TEXT NULL,
    template_wav TEXT NULL,
    archetype TEXT NULL,
    notes TEXT NULL,
    created_at TEXT NULL,
    updated_at TEXT NULL,
    RowVersion BLOB NULL
);

-- task_types
CREATE TABLE task_types (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL,
    description TEXT NULL,
    default_executor_role TEXT NOT NULL,
    default_checker_role TEXT NOT NULL,
    output_merge_strategy TEXT NOT NULL,
    validation_criteria TEXT NULL,
    RowVersion BLOB NULL
);

-- step_templates
CREATE TABLE step_templates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    task_type TEXT NOT NULL,
    step_prompt TEXT NOT NULL,
    instructions TEXT NULL,
    description TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    RowVersion BLOB NULL
);

-- Log (usa PascalCase come da schema originale)
CREATE TABLE Log (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Ts TEXT NOT NULL,
    Level TEXT NOT NULL,
    Category TEXT NOT NULL,
    Message TEXT NOT NULL,
    Exception TEXT NULL,
    State TEXT NULL,
    ThreadId INTEGER NOT NULL DEFAULT 0,
    ThreadScope TEXT NULL,
    AgentName TEXT NULL,
    Context TEXT NULL,
    analized INTEGER NOT NULL DEFAULT 0,
    chat_text TEXT NULL,
    Result TEXT NULL,
    RowVersion BLOB NULL
);

-- ============================================================================
-- REGISTRA MIGRAZIONE IN EF CORE
-- ============================================================================

-- Crea tabella EF Migrations History se non esiste
CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (
    MigrationId TEXT PRIMARY KEY,
    ProductVersion TEXT NOT NULL
);

-- Rimuovi vecchie migrazioni se presenti
DELETE FROM __EFMigrationsHistory;

-- Registra migrazione corrente (usa il nome della migrazione attuale nel progetto)
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
VALUES ('20251211202926_InitialCreate', '10.0.0');

COMMIT;

-- ============================================================================
-- VERIFICA
-- ============================================================================

SELECT 'Tabelle create:' as status;
SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;

SELECT 'Schema agents:' as status;
PRAGMA table_info(agents);

SELECT 'Schema stories:' as status;
PRAGMA table_info(stories);

SELECT 'Migrazioni registrate:' as status;
SELECT * FROM __EFMigrationsHistory;
