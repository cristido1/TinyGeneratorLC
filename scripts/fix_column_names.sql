-- Script SQL per correggere i nomi delle colonne errati (PascalCase -> snake_case)
-- Eseguire con: sqlite3 data/storage.db < scripts/fix_column_names.sql

BEGIN TRANSACTION;

-- ============================================================================
-- TABELLA: agents
-- ============================================================================
-- SQLite 3.25.0+ supporta ALTER TABLE RENAME COLUMN
-- Per versioni precedenti, devi ricreare la tabella

-- Verifica versione SQLite e rinomina colonne
ALTER TABLE agents RENAME COLUMN Name TO name;
ALTER TABLE agents RENAME COLUMN Role TO role;
ALTER TABLE agents RENAME COLUMN ModelId TO model_id;
ALTER TABLE agents RENAME COLUMN VoiceId TO voice_rowid;
ALTER TABLE agents RENAME COLUMN Skills TO skills;
ALTER TABLE agents RENAME COLUMN Config TO config;
ALTER TABLE agents RENAME COLUMN JsonResponseFormat TO json_response_format;
ALTER TABLE agents RENAME COLUMN Prompt TO prompt;
ALTER TABLE agents RENAME COLUMN Instructions TO instructions;
ALTER TABLE agents RENAME COLUMN ExecutionPlan TO execution_plan;
ALTER TABLE agents RENAME COLUMN IsActive TO is_active;
ALTER TABLE agents RENAME COLUMN CreatedAt TO created_at;
ALTER TABLE agents RENAME COLUMN UpdatedAt TO updated_at;
ALTER TABLE agents RENAME COLUMN Notes TO notes;
ALTER TABLE agents RENAME COLUMN Temperature TO temperature;
ALTER TABLE agents RENAME COLUMN TopP TO top_p;
ALTER TABLE agents RENAME COLUMN MultiStepTemplateId TO multi_step_template_id;

-- ============================================================================
-- TABELLA: stories
-- ============================================================================
ALTER TABLE stories RENAME COLUMN GenerationId TO generation_id;
ALTER TABLE stories RENAME COLUMN MemoryKey TO memory_key;
ALTER TABLE stories RENAME COLUMN Timestamp TO ts;
ALTER TABLE stories RENAME COLUMN Prompt TO prompt;
ALTER TABLE stories RENAME COLUMN Story TO story_raw;
ALTER TABLE stories RENAME COLUMN CharCount TO char_count;
ALTER TABLE stories RENAME COLUMN Eval TO eval;
ALTER TABLE stories RENAME COLUMN Score TO score;
ALTER TABLE stories RENAME COLUMN Approved TO approved;
ALTER TABLE stories RENAME COLUMN StatusId TO status_id;
ALTER TABLE stories RENAME COLUMN Folder TO folder;
ALTER TABLE stories RENAME COLUMN ModelId TO model_id;
ALTER TABLE stories RENAME COLUMN AgentId TO agent_id;

-- ============================================================================
-- TABELLA: tts_voices
-- ============================================================================
ALTER TABLE tts_voices RENAME COLUMN VoiceId TO voice_id;
ALTER TABLE tts_voices RENAME COLUMN Name TO name;
ALTER TABLE tts_voices RENAME COLUMN Model TO model;
ALTER TABLE tts_voices RENAME COLUMN Language TO language;
ALTER TABLE tts_voices RENAME COLUMN Gender TO gender;
ALTER TABLE tts_voices RENAME COLUMN Age TO age;
ALTER TABLE tts_voices RENAME COLUMN Confidence TO confidence;
ALTER TABLE tts_voices RENAME COLUMN Score TO score;
ALTER TABLE tts_voices RENAME COLUMN Tags TO tags;
ALTER TABLE tts_voices RENAME COLUMN TemplateWav TO template_wav;
ALTER TABLE tts_voices RENAME COLUMN Archetype TO archetype;
ALTER TABLE tts_voices RENAME COLUMN Notes TO notes;
ALTER TABLE tts_voices RENAME COLUMN CreatedAt TO created_at;
ALTER TABLE tts_voices RENAME COLUMN UpdatedAt TO updated_at;

-- ============================================================================
-- TABELLA: test_definitions
-- ============================================================================
ALTER TABLE test_definitions RENAME COLUMN GroupName TO test_group;
ALTER TABLE test_definitions RENAME COLUMN Library TO library;
ALTER TABLE test_definitions RENAME COLUMN AllowedPlugins TO allowed_plugins;
ALTER TABLE test_definitions RENAME COLUMN FunctionName TO function_name;
ALTER TABLE test_definitions RENAME COLUMN Prompt TO prompt;
ALTER TABLE test_definitions RENAME COLUMN ExpectedBehavior TO expected_behavior;
ALTER TABLE test_definitions RENAME COLUMN ExpectedAsset TO expected_asset;
ALTER TABLE test_definitions RENAME COLUMN TestType TO test_type;
ALTER TABLE test_definitions RENAME COLUMN ExpectedPromptValue TO expected_prompt_value;
ALTER TABLE test_definitions RENAME COLUMN ValidScoreRange TO valid_score_range;
ALTER TABLE test_definitions RENAME COLUMN TimeoutSecs TO timeout_secs;
ALTER TABLE test_definitions RENAME COLUMN Priority TO priority;
ALTER TABLE test_definitions RENAME COLUMN ExecutionPlan TO execution_plan;
ALTER TABLE test_definitions RENAME COLUMN Active TO active;
ALTER TABLE test_definitions RENAME COLUMN JsonResponseFormat TO json_response_format;
ALTER TABLE test_definitions RENAME COLUMN FilesToCopy TO files_to_copy;
ALTER TABLE test_definitions RENAME COLUMN Temperature TO temperature;
ALTER TABLE test_definitions RENAME COLUMN TopP TO top_p;

-- ============================================================================
-- TABELLA: stories_evaluations
-- ============================================================================
ALTER TABLE stories_evaluations RENAME COLUMN StoryId TO story_id;
ALTER TABLE stories_evaluations RENAME COLUMN NarrativeCoherenceScore TO narrative_coherence_score;
ALTER TABLE stories_evaluations RENAME COLUMN NarrativeCoherenceDefects TO narrative_coherence_defects;
ALTER TABLE stories_evaluations RENAME COLUMN OriginalityScore TO originality_score;
ALTER TABLE stories_evaluations RENAME COLUMN OriginalityDefects TO originality_defects;
ALTER TABLE stories_evaluations RENAME COLUMN EmotionalImpactScore TO emotional_impact_score;
ALTER TABLE stories_evaluations RENAME COLUMN EmotionalImpactDefects TO emotional_impact_defects;
ALTER TABLE stories_evaluations RENAME COLUMN ActionScore TO action_score;
ALTER TABLE stories_evaluations RENAME COLUMN ActionDefects TO action_defects;
ALTER TABLE stories_evaluations RENAME COLUMN TotalScore TO total_score;
ALTER TABLE stories_evaluations RENAME COLUMN RawJson TO raw_json;
ALTER TABLE stories_evaluations RENAME COLUMN ModelId TO model_id;
ALTER TABLE stories_evaluations RENAME COLUMN AgentId TO agent_id;
ALTER TABLE stories_evaluations RENAME COLUMN Timestamp TO ts;

-- ============================================================================
-- TABELLA: stories_status
-- ============================================================================
ALTER TABLE stories_status RENAME COLUMN Code TO code;
ALTER TABLE stories_status RENAME COLUMN Description TO description;
ALTER TABLE stories_status RENAME COLUMN Step TO step;
ALTER TABLE stories_status RENAME COLUMN Color TO color;
ALTER TABLE stories_status RENAME COLUMN OperationType TO operation_type;
ALTER TABLE stories_status RENAME COLUMN AgentType TO agent_type;
ALTER TABLE stories_status RENAME COLUMN FunctionName TO function_name;
ALTER TABLE stories_status RENAME COLUMN CaptionToExecute TO caption_to_execute;

-- ============================================================================
-- TABELLA: step_templates
-- ============================================================================
ALTER TABLE step_templates RENAME COLUMN Name TO name;
ALTER TABLE step_templates RENAME COLUMN TaskType TO task_type;
ALTER TABLE step_templates RENAME COLUMN StepPrompt TO step_prompt;
ALTER TABLE step_templates RENAME COLUMN Instructions TO instructions;
ALTER TABLE step_templates RENAME COLUMN Description TO description;
ALTER TABLE step_templates RENAME COLUMN CreatedAt TO created_at;
ALTER TABLE step_templates RENAME COLUMN UpdatedAt TO updated_at;

-- ============================================================================
-- TABELLA: task_types
-- ============================================================================
ALTER TABLE task_types RENAME COLUMN Code TO code;
ALTER TABLE task_types RENAME COLUMN Description TO description;
ALTER TABLE task_types RENAME COLUMN DefaultExecutorRole TO default_executor_role;
ALTER TABLE task_types RENAME COLUMN DefaultCheckerRole TO default_checker_role;
ALTER TABLE task_types RENAME COLUMN OutputMergeStrategy TO output_merge_strategy;
ALTER TABLE task_types RENAME COLUMN ValidationCriteria TO validation_criteria;

-- ============================================================================
-- NOTA: La tabella 'models' usa già PascalCase nello schema originale
-- NOTA: La tabella 'Log' usa già PascalCase nello schema originale
-- Non vanno modificate!
-- ============================================================================

COMMIT;

-- Verifica le modifiche
SELECT 'Verifica tabella agents:' as check_msg;
PRAGMA table_info(agents);

SELECT 'Verifica tabella stories:' as check_msg;
PRAGMA table_info(stories);
