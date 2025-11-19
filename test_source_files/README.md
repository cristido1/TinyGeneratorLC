# Test Source Files

Questa cartella contiene i file che possono essere copiati nelle cartelle di test durante l'esecuzione dei test.

## Come usare

1. **Aggiungi i tuoi file di test qui** - Ad esempio: `sample.txt`, `data.json`, `image.png`, ecc.

2. **Configura il test nel database** - Nel campo `files_to_copy` della tabella `test_definitions`, specifica i file da copiare (separati da virgola):
   ```sql
   UPDATE test_definitions 
   SET files_to_copy = 'sample.txt,data.json'
   WHERE id = 1;
   ```

3. **Usa il placeholder nel prompt** - Nel campo `prompt` del test, usa `[test_folder]` per riferirsi al path assoluto della cartella di test:
   ```
   Read the file located at [test_folder]/sample.txt and summarize its content
   ```

4. **Esegui il test** - Quando il test viene eseguito:
   - Viene creata una cartella in `test_run_folders/{model}_{group}_{yyyyMMdd_HHmmss}/`
   - I file specificati vengono copiati dalla cartella `test_source_files/` alla cartella di test
   - Il placeholder `[test_folder]` nel prompt viene sostituito con il path assoluto
   - Il path della cartella viene salvato nel campo `test_folder` della tabella `model_test_runs`

## Esempio completo

**File di test:** `test_source_files/sample.txt`
```
This is a sample text file for testing.
```

**Configurazione nel database:**
```sql
INSERT INTO test_definitions (
    test_type, 
    test_group, 
    prompt, 
    files_to_copy,
    allowed_plugins,
    timeout_ms
) VALUES (
    'functioncall',
    'file_reading',
    'Read the file at [test_folder]/sample.txt using the filesystem plugin and return its content',
    'sample.txt',
    'filesystem',
    30000
);
```

**Durante l'esecuzione:**
- Cartella creata: `test_run_folders/phi3_mini_file_reading_20251118_163045/`
- File copiato: `test_run_folders/phi3_mini_file_reading_20251118_163045/sample.txt`
- Prompt effettivo: `Read the file at /Users/.../test_run_folders/phi3_mini_file_reading_20251118_163045/sample.txt using the filesystem plugin and return its content`
