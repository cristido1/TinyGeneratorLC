import sqlite3

db = r"data/storage.db"

conn = sqlite3.connect(db)
cur = conn.cursor()

print("DB:", db)

# Ensure model_roles.is_primary exists
mr_cols = [r[1] for r in cur.execute("PRAGMA table_info('model_roles')")]
if "is_primary" not in mr_cols:
    print("Adding model_roles.is_primary...")
    cur.execute("ALTER TABLE model_roles ADD COLUMN is_primary INTEGER NOT NULL DEFAULT 0")

# Dedup roles and update FK in model_roles
print("Deduplicating roles...")
cur.executescript(
    """
    CREATE TEMP TABLE IF NOT EXISTS __roles_dedup_keep AS
    SELECT ruolo, MIN(id) AS keep_id
    FROM roles
    GROUP BY ruolo
    HAVING COUNT(*) > 1;

    CREATE TEMP TABLE IF NOT EXISTS __roles_dedup_map AS
    SELECT r.id AS old_id, k.keep_id AS keep_id
    FROM roles r
    JOIN __roles_dedup_keep k ON k.ruolo = r.ruolo
    WHERE r.id <> k.keep_id;

    UPDATE model_roles
    SET role_id = (SELECT keep_id FROM __roles_dedup_map WHERE old_id = model_roles.role_id)
    WHERE role_id IN (SELECT old_id FROM __roles_dedup_map);

    DELETE FROM roles
    WHERE id IN (SELECT old_id FROM __roles_dedup_map);

    DROP TABLE __roles_dedup_map;
    DROP TABLE __roles_dedup_keep;
    """
)

# Create unique index
print("Ensuring UNIQUE index on roles(ruolo)...")
cur.execute("CREATE UNIQUE INDEX IF NOT EXISTS IX_roles_ruolo ON roles(ruolo)")

conn.commit()
conn.close()

print("Done.")
