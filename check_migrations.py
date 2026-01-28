import sqlite3

conn = sqlite3.connect('data/storage.db')
cursor = conn.cursor()

cursor.execute('SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId')
print('=== MIGRATIONS APPLICATE ===')
for row in cursor.fetchall():
    print(row[0])

# Verifica se esiste il campo is_formatter
cursor.execute("PRAGMA table_info(models)")
print('\n=== COLONNE TABELLA MODELS ===')
for col in cursor.fetchall():
    print(f"{col[1]} ({col[2]})")

conn.close()
