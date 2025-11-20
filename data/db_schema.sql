CREATE TABLE usage_state (
  month TEXT PRIMARY KEY,
  tokens_this_run INTEGER DEFAULT 0,
  tokens_this_month INTEGER DEFAULT 0,
  cost_this_month REAL DEFAULT 0
);
CREATE TABLE calls (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts TEXT,
  model TEXT,
  tokens INTEGER,
  cost REAL,
  request TEXT,
  response TEXT
);
CREATE TABLE sqlite_sequence(name,seq);
CREATE TABLE logs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts TEXT NOT NULL,
    level TEXT NOT NULL,
    category TEXT NOT NULL,
    message TEXT,
    exception TEXT,
    state TEXT
);
CREATE TABLE chapters (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  memory_key TEXT,
  chapter_number INTEGER,
  content TEXT,
  ts TEXT
);
CREATE TABLE prompts (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  model TEXT,
  prompt TEXT,
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
);
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
CREATE INDEX idx_model_test_steps_run_id ON model_test_steps(run_id);
CREATE INDEX idx_model_test_steps_run_step ON model_test_steps(run_id, step_number);
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
CREATE INDEX idx_model_test_assets_step_id ON model_test_assets(step_id);
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
CREATE INDEX idx_model_test_runs_model_id ON model_test_runs(model_id);
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
, NoTools INTEGER DEFAULT 0, speed numeric, WriterScore REAL DEFAULT 0);
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
  timeout_ms INTEGER DEFAULT 30000,
  created_at TEXT DEFAULT (datetime('now')),
  updated_at TEXT
, allowed_plugins TEXT, valid_score_range TEXT, test_type text default 'functioncall', expected_prompt_value text, execution_plan text, json_response_format text, files_to_copy TEXT);
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
);
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
    status TEXT,
    model_id INTEGER NULL,
    agent_id INTEGER NULL
, folder TEXT NULL, char_count INTEGER DEFAULT 0);
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
    notes TEXT NULL, json_response_format TEXT NULL,
    FOREIGN KEY (voice_rowid) REFERENCES tts_voices(id)
);
CREATE TABLE IF NOT EXISTS "Memory" (Id TEXT PRIMARY KEY, Collection TEXT NOT NULL, TextValue TEXT NOT NULL, Metadata TEXT, model_id INTEGER NULL, agent_id INTEGER NULL, CreatedAt TEXT NOT NULL, FOREIGN KEY (agent_id) REFERENCES agents(id) ON DELETE SET NULL ON UPDATE CASCADE);
CREATE TABLE stories_evaluations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    story_id INTEGER NOT NULL,
    narrative_coherence_score INTEGER DEFAULT 0,
    narrative_coherence_defects TEXT,
    structure_score INTEGER DEFAULT 0,
    structure_defects TEXT,
    characterization_score INTEGER DEFAULT 0,
    characterization_defects TEXT,
    dialogues_score INTEGER DEFAULT 0,
    dialogues_defects TEXT,
    pacing_score INTEGER DEFAULT 0,
    pacing_defects TEXT,
    originality_score INTEGER DEFAULT 0,
    originality_defects TEXT,
    style_score INTEGER DEFAULT 0,
    style_defects TEXT,
    worldbuilding_score INTEGER DEFAULT 0,
    worldbuilding_defects TEXT,
    thematic_coherence_score INTEGER DEFAULT 0,
    thematic_coherence_defects TEXT,
    emotional_impact_score INTEGER DEFAULT 0,
    emotional_impact_defects TEXT,
    total_score REAL DEFAULT 0,
    overall_evaluation TEXT,
    raw_json TEXT,
    model_id INTEGER NULL,
    agent_id INTEGER NULL,
    ts TEXT,
    FOREIGN KEY (story_id) REFERENCES stories(id) ON DELETE CASCADE,
    FOREIGN KEY (model_id) REFERENCES models(Id) ON DELETE SET NULL,
    FOREIGN KEY (agent_id) REFERENCES agents(id) ON DELETE SET NULL
);
