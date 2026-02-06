import sqlite3

db = r"data/storage.db"

conn = sqlite3.connect(db)
cur = conn.cursor()

print("DB:", db)

print("\n--- duplicates exact (ruolo) ---")
cur.execute(
    """
    SELECT ruolo, COUNT(*)
    FROM roles
    GROUP BY ruolo
    HAVING COUNT(*) > 1
    ORDER BY COUNT(*) DESC, ruolo
    LIMIT 100
    """
)
rows = cur.fetchall()
if not rows:
    print("(none)")
else:
    for ruolo, n in rows:
        print(f"{n:>3}  {ruolo!r}")

print("\n--- duplicates normalized (lower(trim(ruolo))) ---")
cur.execute(
    """
    SELECT lower(trim(ruolo)) AS k, COUNT(*)
    FROM roles
    GROUP BY k
    HAVING COUNT(*) > 1
    ORDER BY COUNT(*) DESC, k
    LIMIT 100
    """
)
rows = cur.fetchall()
if not rows:
    print("(none)")
else:
    for k, n in rows:
        print(f"{n:>3}  {k!r}")

print("\n--- index_list(roles) ---")
for row in cur.execute("PRAGMA index_list('roles')"):
    print(row)

print("\n--- schema checks ---")
stories_cols = [r[1] for r in cur.execute("PRAGMA table_info('stories')")]
print("stories has auto_tts_fail_count:", "auto_tts_fail_count" in stories_cols)
print("stories has deleted:", "deleted" in stories_cols)
model_roles_cols = [r[1] for r in cur.execute("PRAGMA table_info('model_roles')")]
print("model_roles has is_primary:", "is_primary" in model_roles_cols)

tables = {r[0] for r in cur.execute("SELECT name FROM sqlite_master WHERE type='table'")}
print("has table series_state:", "series_state" in tables)
print("has table stats_models:", "stats_models" in tables)
log_cols = [r[1] for r in cur.execute("PRAGMA table_info('Log')")]
print("Log has Examined:", "Examined" in log_cols)
print("Log has ResultFailReason:", "ResultFailReason" in log_cols)

print("\n--- migrations history ---")
has_history = cur.execute(
    "SELECT 1 FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'"
).fetchone()
print("has __EFMigrationsHistory:", bool(has_history))
if has_history:
    migs = [r[0] for r in cur.execute("SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId")]
    print("migrations count:", len(migs))
    for m in migs[-20:]:
        print(m)

conn.close()
