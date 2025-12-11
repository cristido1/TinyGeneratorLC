-- Migrazione da schema Dapper legacy a schema EF Core
-- Per database agents con struttura vecchia (model TEXT invece di model_id INTEGER)

-- IMPORTANTE: Fai backup prima!
-- copy data\storage.db data\storage.db.pre-migration-backup

BEGIN TRANSACTION;

-- ============================================================================
-- STEP 1: Verifica e salva dati esistenti
-- ============================================================================

-- Crea tabella temporanea con dati attuali
CREATE TABLE agents_backup AS SELECT * FROM agents;

-- ============================================================================
-- STEP 2: Ricrea tabella agents con nuova struttura
-- ============================================================================

DROP TABLE agents;

CREATE TABLE agents (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    role TEXT NOT NULL,
    model_id INTEGER NULL,                    -- Cambiato da "model TEXT"
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
    multi_step_template_id INTEGER NULL,
    RowVersion BLOB NULL
);

-- ============================================================================
-- STEP 3: Ripristina dati convertendo model TEXT -> model_id INTEGER
-- ============================================================================

-- Inserisci dati convertendo il campo model in model_id
INSERT INTO agents (
    id, name, role, model_id, voice_rowid, skills, config, json_response_format,
    prompt, instructions, execution_plan, is_active, created_at, updated_at, notes,
    temperature, top_p, multi_step_template_id
)
SELECT 
    ab.id,
    ab.name,
    ab.role,
    -- Converti model TEXT in model_id INTEGER cercando nella tabella models
    (SELECT m.Id FROM models m WHERE m.Name = ab.model) as model_id,
    ab.voice_rowid,
    ab.skills,
    ab.config,
    ab.json_response_format,
    ab.prompt,
    ab.instructions,
    ab.execution_plan,
    ab.is_active,
    ab.created_at,
    ab.updated_at,
    ab.notes,
    ab.temperature,
    ab.top_p,
    ab.multi_step_template_id
FROM agents_backup ab;

-- ============================================================================
-- STEP 4: Verifica migrazione
-- ============================================================================

-- Conta record
SELECT 'Record originali:', COUNT(*) FROM agents_backup;
SELECT 'Record migrati:', COUNT(*) FROM agents;

-- Mostra agents con model_id NULL (modelli non trovati in tabella models)
SELECT 'Agents con model_id NULL:' as check_msg;
SELECT id, name, role FROM agents WHERE model_id IS NULL;

-- ============================================================================
-- STEP 5: Pulizia (decommentare solo DOPO aver verificato che tutto sia OK)
-- ============================================================================

-- DROP TABLE agents_backup;

COMMIT;

-- ============================================================================
-- VERIFICA FINALE
-- ============================================================================

SELECT 'Schema finale tabella agents:' as check_msg;
PRAGMA table_info(agents);

SELECT 'Sample data:' as check_msg;
SELECT id, name, role, model_id FROM agents LIMIT 5;
