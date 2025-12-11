#!/bin/bash
# Script per correggere un database esistente con nomi colonne errati

echo "=== Correzione Database Esistente ==="
echo ""
echo "Questo script corregge i nomi delle colonne in un database esistente"
echo "che è stato creato con la prima migrazione (nomi PascalCase errati)."
echo ""

# 1. Verifica che il database esista
if [ ! -f "data/storage.db" ]; then
    echo "❌ Database non trovato in data/storage.db"
    echo "   Questo script è per database esistenti con nomi colonne errati."
    exit 1
fi

# 2. Backup automatico
BACKUP_FILE="data/storage.db.fix-backup-$(date +%Y%m%d-%H%M%S)"
echo "1. Creazione backup → $BACKUP_FILE"
cp data/storage.db "$BACKUP_FILE"
cp data/storage.db-shm "$BACKUP_FILE-shm" 2>/dev/null || true
cp data/storage.db-wal "$BACKUP_FILE-wal" 2>/dev/null || true
echo "   ✓ Backup completato"

# 3. Verifica che ci sia la migrazione InitialCreate vecchia
echo ""
echo "2. Verifica migrazioni applicate..."
APPLIED_MIGRATIONS=$(sqlite3 data/storage.db "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;" 2>/dev/null)

if [ -z "$APPLIED_MIGRATIONS" ]; then
    echo "   ⚠️  Nessuna migrazione trovata nel database"
    echo "   Il database potrebbe essere stato creato manualmente."
fi

echo "   Migrazioni applicate:"
echo "$APPLIED_MIGRATIONS" | while read mig; do
    echo "     - $mig"
done

# 4. Verifica nomi colonne attuali (test su tabella agents)
echo ""
echo "3. Verifica nomi colonne attuali (tabella agents)..."
CURRENT_COLUMNS=$(sqlite3 data/storage.db "PRAGMA table_info(agents);" 2>/dev/null | cut -d'|' -f2)

if echo "$CURRENT_COLUMNS" | grep -q "^Name$"; then
    echo "   ⚠️  Trovate colonne PascalCase (es. 'Name' invece di 'name')"
    echo "   Database necessita correzione!"
    NEEDS_FIX=true
elif echo "$CURRENT_COLUMNS" | grep -q "^name$"; then
    echo "   ✓ Colonne già corrette (snake_case)"
    echo "   Nessuna correzione necessaria."
    NEEDS_FIX=false
else
    echo "   ⚠️  Struttura tabella non riconosciuta"
    NEEDS_FIX=false
fi

# 5. Opzione di correzione
if [ "$NEEDS_FIX" = true ]; then
    echo ""
    echo "4. Applicazione correzione..."
    echo ""
    read -p "   Vuoi procedere con la correzione? (s/N) " -n 1 -r
    echo
    
    if [[ $REPLY =~ ^[Ss]$ ]]; then
        echo ""
        echo "   Correzione in corso..."
        echo ""
        
        # SQLite non supporta RENAME COLUMN direttamente prima della versione 3.25.0
        # Dobbiamo usare un approccio diverso: creare nuove tabelle e copiare i dati
        
        echo "   ⚠️  ATTENZIONE: SQLite richiede la ricreazione delle tabelle"
        echo "   per rinominare le colonne. Questo potrebbe richiedere tempo."
        echo ""
        read -p "   Confermi di voler procedere? (s/N) " -n 1 -r
        echo
        
        if [[ $REPLY =~ ^[Ss]$ ]]; then
            # Strategia: eliminare la vecchia migrazione e riapplicare quella corretta
            # Questo richiede di ricreare le tabelle
            
            echo ""
            echo "   STRATEGIA CONSIGLIATA:"
            echo "   1. Esporta i dati importanti (agents, models, stories, ecc.)"
            echo "   2. Elimina il database"
            echo "   3. Esegui l'applicazione per ricreare con struttura corretta"
            echo "   4. Reimporta i dati"
            echo ""
            echo "   Vuoi che lo script generi i comandi SQL per esportare i dati? (s/N) "
            read -n 1 -r
            echo
            
            if [[ $REPLY =~ ^[Ss]$ ]]; then
                EXPORT_FILE="data/export_$(date +%Y%m%d-%H%M%S).sql"
                echo "   Generazione export SQL → $EXPORT_FILE"
                
                # Export delle tabelle principali
                sqlite3 data/storage.db <<EOF > "$EXPORT_FILE"
.mode insert agents
SELECT * FROM agents;
.mode insert models
SELECT * FROM models;
.mode insert stories
SELECT * FROM stories;
.mode insert tts_voices
SELECT * FROM tts_voices;
.mode insert test_definitions
SELECT * FROM test_definitions;
EOF
                
                echo "   ✓ Export completato: $EXPORT_FILE"
                echo ""
                echo "   Prossimi passi manuali:"
                echo "   1. Verifica il contenuto di $EXPORT_FILE"
                echo "   2. Elimina data/storage.db"
                echo "   3. Esegui: dotnet run (ricrea DB con struttura corretta)"
                echo "   4. Esegui: sqlite3 data/storage.db < $EXPORT_FILE"
            fi
        else
            echo "   Operazione annullata."
        fi
    else
        echo "   Operazione annullata."
    fi
else
    echo ""
    echo "✅ Database già corretto o non necessita modifiche"
fi

echo ""
echo "Backup disponibile: $BACKUP_FILE"
echo "Conservalo fino a quando non hai verificato che tutto funzioni correttamente."
