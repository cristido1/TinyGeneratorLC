import sqlite3

db = r"data/storage.db"

conn = sqlite3.connect(db)
cur = conn.cursor()

print("DB:", db)

cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE '__EFMigrations%'")
tables = [r[0] for r in cur.fetchall()]
print("EF tables found:", tables)

for t in tables:
    print("Dropping", t)
    cur.execute(f"DROP TABLE IF EXISTS {t}")

conn.commit()
conn.close()

print("Done.")

# Re-open and verify
conn = sqlite3.connect(db)
cur = conn.cursor()
cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE '__EFMigrations%'")
remaining = [r[0] for r in cur.fetchall()]
conn.close()
print("Remaining EF tables:", remaining)
