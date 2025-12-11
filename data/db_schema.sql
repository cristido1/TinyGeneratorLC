CREATE TABLE sqlite_sequence(name,seq);
CREATE TABLE chapters (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  memory_key TEXT,
  chapter_number INTEGER,
  content TEXT,
  ts TEXT
);
CREATE TABLE Log (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Ts TEXT,
    Level TEXT,
    Category TEXT,
    Message TEXT,
    Exception TEXT,
    State TEXT,
    ThreadId INTEGER DEFAULT 0,
    AgentName TEXT,
    Context TEXT
, analized boolean default 0, ThreadScope TEXT, chat_text text, Result TEXT);
CREATE TABLE model_test_steps (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL,
    step_number INTEGER,
    step_name TEXT,
    input_json TEXT,
    output_json TEXT,
    passed INTEGER DEFAULT 0,
    error TEXT,
    duration_ms INTEGER,
    FOREIGN KEY(run_id) REFERENCES model_test_runs(id) ON DELETE CASCADE
);
CREATE TABLE model_test_assets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    step_id INTEGER NOT NULL,
    file_type TEXT,
    file_path TEXT,
    description TEXT,
    duration_sec REAL,
    size_bytes INTEGER, story_id INTEGER NULL,
    FOREIGN KEY(step_id) REFERENCES model_test_steps(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS "model_test_runs" (
  id INTEGER PRIMARY KEY,
  model_id INTEGER,
  test_group TEXT NOT NULL,
  description TEXT,
  passed INTEGER DEFAULT 0,
  duration_ms INTEGER,
  notes TEXT,
  run_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP, test_folder TEXT,
  FOREIGN KEY(model_id) REFERENCES models(Id) ON DELETE SET NULL
);
CREATE TABLE IF NOT EXISTS "models" (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  Name TEXT UNIQUE,
  Provider TEXT,
  Endpoint TEXT,
  IsLocal INTEGER DEFAULT 1,
  MaxContext INTEGER DEFAULT 4096,
  ContextToUse INTEGER DEFAULT 4096,
  FunctionCallingScore INTEGER DEFAULT 0,
  CostInPerToken REAL DEFAULT 0,
  CostOutPerToken REAL DEFAULT 0,
  LimitTokensDay INTEGER DEFAULT 0,
  LimitTokensWeek INTEGER DEFAULT 0,
  LimitTokensMonth INTEGER DEFAULT 0,
  Metadata TEXT,
  Enabled INTEGER DEFAULT 1,
  CreatedAt TEXT,
  UpdatedAt TEXT,
  TestDurationSeconds REAL
, NoTools INTEGER DEFAULT 0, speed numeric, WriterScore REAL DEFAULT 0, BaseScore REAL DEFAULT 0.0, TextEvalScore REAL DEFAULT 0.0, TtsScore REAL DEFAULT 0.0, MusicScore REAL DEFAULT 0.0, FxScore REAL DEFAULT 0.0, AmbientScore REAL DEFAULT 0.0, TotalScore REAL DEFAULT 0.0, note TEXT);
CREATE TABLE test_definitions (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  test_group TEXT NOT NULL,
  library TEXT NOT NULL,
  function_name TEXT NOT NULL,
  prompt TEXT NOT NULL,
  expected_behavior TEXT,
  expected_asset TEXT,
  min_score INTEGER DEFAULT 0,
  priority INTEGER DEFAULT 1,
  active INTEGER DEFAULT 1,
  timeout_secs INTEGER DEFAULT 30000,
  created_at TEXT DEFAULT (datetime('now')),
  updated_at TEXT
, allowed_plugins TEXT, valid_score_range TEXT, test_type text default 'functioncall', expected_prompt_value text, execution_plan text, json_response_format text, files_to_copy TEXT, temperature real default 0.7, top_p real default 0.9);
CREATE TABLE tts_voices (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    voice_id TEXT UNIQUE,
    name TEXT,
    model TEXT,
    language TEXT,
    gender TEXT,
    age TEXT,
    confidence REAL,
    tags TEXT,
    sample_path TEXT,
    template_wav TEXT,
    metadata TEXT,
    created_at TEXT,
    updated_at TEXT
, archetype string, notes TEXT, score int);
CREATE TABLE IF NOT EXISTS "stories" (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    generation_id TEXT,
    memory_key TEXT,
    ts TEXT,
    prompt TEXT,
    story TEXT,
    eval TEXT,
    score REAL,
    approved INTEGER,
    model_id INTEGER NULL,
    agent_id INTEGER NULL
, folder TEXT NULL, char_count INTEGER DEFAULT 0, status_id INTEGER);
CREATE TABLE agents (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    voice_rowid INTEGER NULL,
    name TEXT NOT NULL,
    role TEXT NOT NULL,
    model_id INTEGER NULL,
    skills TEXT NULL,
    config TEXT NULL,
    prompt TEXT NULL,
    instructions TEXT NULL,
    execution_plan TEXT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL,
    updated_at TEXT NULL,
    notes TEXT NULL, json_response_format TEXT NULL, temperature real, top_p real, multi_step_template_id INTEGER NULL,
    FOREIGN KEY (voice_rowid) REFERENCES tts_voices(id)
);
CREATE TABLE IF NOT EXISTS "Memory" (Id TEXT PRIMARY KEY, Collection TEXT NOT NULL, TextValue TEXT NOT NULL, Metadata TEXT, model_id INTEGER NULL, agent_id INTEGER NULL, CreatedAt TEXT NOT NULL, Embedding BLOB NULL, FOREIGN KEY (agent_id) REFERENCES agents(id) ON DELETE SET NULL ON UPDATE CASCADE);
CREATE INDEX idx_model_test_steps_run_id ON model_test_steps(run_id);
CREATE INDEX idx_model_test_steps_run_step ON model_test_steps(run_id, step_number);
CREATE INDEX idx_model_test_assets_step_id ON model_test_assets(step_id);
CREATE INDEX idx_model_test_runs_model_id ON model_test_runs(model_id);
CREATE TABLE stories_status (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code VARCHAR(50) NOT NULL UNIQUE,
    description VARCHAR(255) NOT NULL,
    step INTEGER NOT NULL,
    color VARCHAR(20) NOT NULL,
    operation_type VARCHAR(20) NOT NULL,   -- none | agent_call | function_call
    agent_type VARCHAR(50),                -- evaluator | writer | tts | music | fx | ambient | none
    function_name VARCHAR(100)             -- only for function_call
, caption_to_execute text);
CREATE TABLE Log_analysis (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    threadId TEXT NOT NULL,
    model_id TEXT NOT NULL,
    run_scope TEXT NOT NULL,
    description TEXT NOT NULL,
    succeeded INTEGER NOT NULL CHECK (succeeded IN (0,1))
);
CREATE TABLE stories_evaluations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    story_id INTEGER NOT NULL,
    narrative_coherence_score INTEGER DEFAULT 0,
    narrative_coherence_defects TEXT,
    originality_score INTEGER DEFAULT 0,
    originality_defects TEXT,
    emotional_impact_score INTEGER DEFAULT 0,
    emotional_impact_defects TEXT,
    action_score INTEGER DEFAULT 0,
    action_defects TEXT,
    total_score REAL DEFAULT 0,
    raw_json TEXT,
    model_id INTEGER NULL,
    agent_id INTEGER NULL,
    ts TEXT,
    FOREIGN KEY (story_id) REFERENCES stories(id) ON DELETE CASCADE,
    FOREIGN KEY (model_id) REFERENCES models(Id) ON DELETE SET NULL,
    FOREIGN KEY (agent_id) REFERENCES agents(id) ON DELETE SET NULL
);
CREATE TABLE task_types (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT UNIQUE NOT NULL,
    description TEXT,
    default_executor_role TEXT NOT NULL,
    default_checker_role TEXT NOT NULL,
    output_merge_strategy TEXT NOT NULL,
    validation_criteria TEXT
);
CREATE TABLE task_executions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    task_type TEXT NOT NULL,
    entity_id INTEGER NULL,
    step_prompt TEXT NOT NULL,
    current_step INTEGER DEFAULT 1,
    max_step INTEGER NOT NULL,
    retry_count INTEGER DEFAULT 0,
    status TEXT DEFAULT 'pending' CHECK(status IN ('pending','in_progress','completed','failed','paused')),
    executor_agent_id INTEGER NULL,
    checker_agent_id INTEGER NULL,
    config TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
, initial_context TEXT NULL);
CREATE UNIQUE INDEX idx_task_executions_active ON task_executions(entity_id, task_type) WHERE status IN ('pending','in_progress');
CREATE TABLE task_execution_steps (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    execution_id INTEGER NOT NULL,
    step_number INTEGER NOT NULL,
    step_instruction TEXT NOT NULL,
    step_output TEXT NULL,
    validation_result TEXT NULL,
    attempt_count INTEGER DEFAULT 1,
    started_at TEXT,
    completed_at TEXT,
    FOREIGN KEY(execution_id) REFERENCES task_executions(id) ON DELETE CASCADE
);
CREATE TABLE step_templates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT UNIQUE NOT NULL,
    task_type TEXT NOT NULL,
    step_prompt TEXT NOT NULL,
    description TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
, instructions text);
CREATE TABLE story_chunk_facts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    story_id INTEGER NOT NULL,
    chunk_number INTEGER NOT NULL,
    facts_json TEXT NOT NULL,
    ts TEXT NOT NULL,
    FOREIGN KEY (story_id) REFERENCES stories(id) ON DELETE CASCADE
);
CREATE INDEX idx_story_chunk_facts_story ON story_chunk_facts(story_id, chunk_number);
CREATE TABLE story_coherence_scores (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    story_id INTEGER NOT NULL,
    chunk_number INTEGER NOT NULL,
    local_coherence REAL NOT NULL,
    global_coherence REAL NOT NULL,
    errors TEXT,
    ts TEXT NOT NULL,
    FOREIGN KEY (story_id) REFERENCES stories(id) ON DELETE CASCADE
);
CREATE INDEX idx_story_coherence_scores_story ON story_coherence_scores(story_id, chunk_number);
CREATE TABLE story_global_coherence (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    story_id INTEGER NOT NULL UNIQUE,
    global_coherence REAL NOT NULL,
    chunk_count INTEGER NOT NULL,
    notes TEXT,
    ts TEXT NOT NULL,
    FOREIGN KEY (story_id) REFERENCES stories(id) ON DELETE CASCADE
);
CREATE TABLE app_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    event_type TEXT UNIQUE NOT NULL,
    description TEXT,
    enabled INTEGER NOT NULL DEFAULT 1,
    logged INTEGER NOT NULL DEFAULT 1,
    notified INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
