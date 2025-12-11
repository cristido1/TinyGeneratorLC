-- Copia i record dalla tabella `test_definitions` del database corrente
-- alla tabella `test_definitions` di un altro database SQLite.
-- Uso: sostituire 'path/to/other.db' con il percorso reale del database di destinazione.

ATTACH DATABASE 'path/to/other.db' AS otherdb;

-- Se vuoi preservare gli id originali (incluso l'AUTOINCREMENT), usa questa INSERT
INSERT INTO otherdb.test_definitions(
    id,
    test_group,
    library,
    allowed_plugins,
    function_name,
    prompt,
    expected_behavior,
    expected_asset,
    test_type,
    expected_prompt_value,
    valid_score_range,
    timeout_secs,
    priority,
    execution_plan,
    active,
    json_response_format,
    files_to_copy,
    temperature,
    top_p,
    RowVersion
)
SELECT
    id,
    test_group,
    library,
    allowed_plugins,
    function_name,
    prompt,
    expected_behavior,
    expected_asset,
    test_type,
    expected_prompt_value,
    valid_score_range,
    timeout_secs,
    priority,
    execution_plan,
    active,
    json_response_format,
    files_to_copy,
    temperature,
    top_p,
    RowVersion
FROM test_definitions
-- Filtra i record che vuoi copiare, ad es. solo attivi:
WHERE active = 1;

-- Se preferisci lasciare che il DB di destinazione riassegni nuovi id, commenta l'INSERT sopra
-- e usa questa variante (NOTA: non include id nella lista delle colonne):
-- INSERT INTO otherdb.test_definitions(
--     test_group, library, allowed_plugins, function_name, prompt, expected_behavior, expected_asset,
--     test_type, expected_prompt_value, valid_score_range, timeout_secs, priority, execution_plan,
--     active, json_response_format, files_to_copy, temperature, top_p, RowVersion
-- )
-- SELECT
--     test_group, library, allowed_plugins, function_name, prompt, expected_behavior, expected_asset,
--     test_type, expected_prompt_value, valid_score_range, timeout_secs, priority, execution_plan,
--     active, json_response_format, files_to_copy, temperature, top_p, RowVersion
-- FROM test_definitions
-- WHERE active = 1;

DETACH DATABASE otherdb;
