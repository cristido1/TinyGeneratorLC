#!/bin/bash
# Script per testare la prima esecuzione del database su un computer pulito

echo "=== Test Prima Esecuzione Database ==="
echo ""

# 1. Backup del database esistente (se presente)
if [ -f "data/storage.db" ]; then
    BACKUP_FILE="data/storage.db.test-backup-$(date +%Y%m%d-%H%M%S)"
    echo "1. Backup database esistente → $BACKUP_FILE"
    cp data/storage.db "$BACKUP_FILE"
else
    echo "1. Nessun database esistente da backuppare"
fi

# 2. Rimuovi il database per simulare prima esecuzione
echo "2. Rimozione database per simulare prima esecuzione..."
rm -f data/storage.db
rm -f data/storage.db-shm
rm -f data/storage.db-wal

# 3. Esegui build
echo ""
echo "3. Build applicazione..."
dotnet build --nologo -v quiet
if [ $? -ne 0 ]; then
    echo "   ❌ Build fallita!"
    exit 1
fi
echo "   ✓ Build completata"

# 4. Verifica che le migrazioni siano pronte
echo ""
echo "4. Verifica migrazioni EF Core..."
dotnet ef migrations list --no-build --no-color 2>&1 | grep -q "InitialCreate"
if [ $? -eq 0 ]; then
    echo "   ✓ Migrazione InitialCreate trovata"
else
    echo "   ❌ Migrazione InitialCreate non trovata!"
    exit 1
fi

# 5. Applica migrazioni manualmente (simula startup)
echo ""
echo "5. Applicazione migrazioni (simula Program.cs)..."
dotnet ef database update --no-build --no-color
if [ $? -ne 0 ]; then
    echo "   ❌ Applicazione migrazioni fallita!"
    exit 1
fi
echo "   ✓ Migrazioni applicate"

# 6. Verifica che il database sia stato creato
echo ""
echo "6. Verifica creazione database..."
if [ -f "data/storage.db" ]; then
    echo "   ✓ Database creato: data/storage.db"
    DB_SIZE=$(ls -lh data/storage.db | awk '{print $5}')
    echo "   Dimensione: $DB_SIZE"
else
    echo "   ❌ Database non creato!"
    exit 1
fi

# 7. Verifica schema tabelle
echo ""
echo "7. Verifica schema tabelle create..."
TABLES=$(sqlite3 data/storage.db ".tables")
echo "   Tabelle trovate: $TABLES"

# Verifica tabelle EF Core
REQUIRED_TABLES=("agents" "models" "stories" "stories_evaluations" "stories_status" "test_definitions" "tts_voices" "task_types" "step_templates" "Log" "__EFMigrationsHistory")
MISSING_TABLES=()

for table in "${REQUIRED_TABLES[@]}"; do
    if echo "$TABLES" | grep -q "$table"; then
        echo "   ✓ Tabella $table presente"
    else
        echo "   ❌ Tabella $table MANCANTE"
        MISSING_TABLES+=("$table")
    fi
done

if [ ${#MISSING_TABLES[@]} -gt 0 ]; then
    echo ""
    echo "   ❌ Tabelle mancanti: ${MISSING_TABLES[*]}"
    exit 1
fi

# 8. Verifica nomi colonne tabella agents (test critico)
echo ""
echo "8. Verifica nomi colonne tabella 'agents' (test snake_case)..."
AGENT_COLUMNS=$(sqlite3 data/storage.db "PRAGMA table_info(agents);" | cut -d'|' -f2)
EXPECTED_COLUMNS=("id" "name" "role" "model_id" "voice_rowid" "skills" "config" "json_response_format" "prompt" "instructions" "execution_plan" "is_active" "created_at" "updated_at" "notes" "temperature" "top_p" "multi_step_template_id" "RowVersion")

echo "   Colonne trovate:"
echo "$AGENT_COLUMNS" | while read col; do
    echo "     - $col"
done

COLUMN_ERRORS=0
for expected_col in "${EXPECTED_COLUMNS[@]}"; do
    if echo "$AGENT_COLUMNS" | grep -q "^$expected_col$"; then
        echo "   ✓ Colonna '$expected_col' presente"
    else
        echo "   ❌ Colonna '$expected_col' MANCANTE"
        COLUMN_ERRORS=$((COLUMN_ERRORS + 1))
    fi
done

if [ $COLUMN_ERRORS -gt 0 ]; then
    echo ""
    echo "   ❌ $COLUMN_ERRORS colonne mancanti o con nomi errati!"
    exit 1
fi

# 9. Verifica nomi colonne tabella stories (altro test critico)
echo ""
echo "9. Verifica nomi colonne tabella 'stories'..."
STORY_COLUMNS=$(sqlite3 data/storage.db "PRAGMA table_info(stories);" | cut -d'|' -f2)
EXPECTED_STORY_COLS=("id" "generation_id" "memory_key" "ts" "prompt" "story" "char_count" "eval" "score" "approved" "status_id" "folder" "model_id" "agent_id" "RowVersion")

STORY_ERRORS=0
for expected_col in "${EXPECTED_STORY_COLS[@]}"; do
    if echo "$STORY_COLUMNS" | grep -q "^$expected_col$"; then
        echo "   ✓ Colonna '$expected_col' presente"
    else
        echo "   ❌ Colonna '$expected_col' MANCANTE"
        STORY_ERRORS=$((STORY_ERRORS + 1))
    fi
done

if [ $STORY_ERRORS -gt 0 ]; then
    echo ""
    echo "   ❌ $STORY_ERRORS colonne mancanti o con nomi errati in stories!"
    exit 1
fi

# 10. Risultato finale
echo ""
echo "=========================================="
echo "✅ TUTTI I TEST SUPERATI!"
echo "=========================================="
echo ""
echo "Il database è stato creato correttamente con:"
echo "  - Tutte le tabelle EF Core presenti"
echo "  - Nomi colonne snake_case corretti (agents, stories)"
echo "  - Migrazioni applicate"
echo ""
echo "La prima esecuzione su un computer pulito funzionerà correttamente."
echo ""

# Opzionale: ripristina backup se richiesto
read -p "Vuoi ripristinare il backup del database? (s/N) " -n 1 -r
echo
if [[ $REPLY =~ ^[Ss]$ ]]; then
    if [ -f "$BACKUP_FILE" ]; then
        echo "Ripristino backup..."
        cp "$BACKUP_FILE" data/storage.db
        echo "✓ Backup ripristinato"
    fi
fi
