import sqlite3
import re

# Model to table mapping with expected columns
MODEL_MAPPINGS = {
    'agents': [
        'id', 'name', 'role', 'model_id', 'voice_rowid', 'skills', 'config', 
        'json_response_format', 'prompt', 'instructions', 'execution_plan', 
        'is_active', 'created_at', 'updated_at', 'notes', 'temperature', 'top_p',
        'multi_step_template_id', 'RowVersion'
    ],
    'stories': [
        'id', 'generation_id', 'memory_key', 'ts', 'prompt', 'title', 'story',
        'char_count', 'eval', 'score', 'approved', 'status_id', 'folder',
        'generated_tts_json', 'generated_tts', 'generated_ambient', 'generated_music',
        'generated_effects', 'generated_mixed_audio', 'model_id', 'agent_id',
        'RowVersion', 'characters', 'serie_id', 'serie_episode', 'summary'
    ],
    'series': [
        'id', 'titolo', 'genere', 'sottogenere', 'periodo_narrativo', 'tono_base',
        'target', 'lingua', 'ambientazione_base', 'premessa_serie', 'arco_narrativo_serie',
        'stile_scrittura', 'regole_narrative', 'note_ai', 'episodi_generati',
        'data_inserimento', 'timestamp'
    ],
    'step_templates': [
        'id', 'name', 'task_type', 'step_prompt', 'instructions', 'description',
        'created_at', 'updated_at', 'RowVersion', 'characters_step', 'evaluation_steps',
        'trama_steps', 'agent_id', 'voice_id', 'min_chars_trama', 'min_chars_story',
        'full_story_step'
    ],
    'models': [
        'Id', 'Name', 'Provider', 'Endpoint', 'IsLocal', 'MaxContext', 'ContextToUse',
        'FunctionCallingScore', 'CostInPerToken', 'CostOutPerToken', 'LimitTokensDay',
        'LimitTokensWeek', 'LimitTokensMonth', 'Metadata', 'Enabled', 'CreatedAt',
        'UpdatedAt', 'TestDurationSeconds', 'NoTools', 'WriterScore', 'BaseScore',
        'TextEvalScore', 'TtsScore', 'MusicScore', 'FxScore', 'AmbientScore',
        'TotalScore', 'Note', 'LastTestResults', 'LastMusicTestFile', 'LastSoundTestFile',
        'LastTtsTestFile', 'LastScore_Base', 'LastScore_Tts', 'LastScore_Music',
        'LastScore_Write', 'LastResults_BaseJson', 'LastResults_TtsJson',
        'LastResults_MusicJson', 'LastResults_WriteJson', 'RowVersion', 'speed'
    ],
    'tts_voices': [
        'id', 'voice_id', 'name', 'model', 'language', 'gender', 'age', 'confidence',
        'score', 'tags', 'template_wav', 'archetype', 'notes', 'created_at', 'updated_at',
        'RowVersion', 'sample_path', 'metadata', 'disabled'
    ],
    'test_definitions': [
        'id', 'test_group', 'library', 'allowed_plugins', 'function_name', 'prompt',
        'expected_behavior', 'expected_asset', 'test_type', 'expected_prompt_value',
        'valid_score_range', 'timeout_secs', 'priority', 'execution_plan', 'active',
        'json_response_format', 'files_to_copy', 'temperature', 'top_p', 'min_score',
        'created_at', 'updated_at'
    ]
}

conn = sqlite3.connect('data/storage.db')
cursor = conn.cursor()

print("=== SCHEMA VALIDATION ===\n")

all_missing = []

for table_name, expected_columns in MODEL_MAPPINGS.items():
    # Get actual columns
    cursor.execute(f"PRAGMA table_info({table_name})")
    actual_columns = [row[1] for row in cursor.fetchall()]
    
    # Find missing columns
    missing = [col for col in expected_columns if col not in actual_columns]
    
    if missing:
        print(f"❌ {table_name}:")
        for col in missing:
            print(f"   MISSING: {col}")
            all_missing.append((table_name, col))
    else:
        print(f"✓ {table_name}: OK")

conn.close()

print(f"\n=== SUMMARY ===")
print(f"Total missing columns: {len(all_missing)}")

if all_missing:
    print("\n=== REQUIRED MIGRATIONS ===")
    for table, column in all_missing:
        # Try to infer type (simplified)
        col_type = "INTEGER" if any(x in column.lower() for x in ['id', 'score', 'count', 'step']) else "TEXT"
        print(f"ALTER TABLE {table} ADD COLUMN {column} {col_type};")
