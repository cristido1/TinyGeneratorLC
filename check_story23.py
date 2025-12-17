import sqlite3

conn = sqlite3.connect('data/storage.db')
c = conn.cursor()

# Check tables
c.execute("SELECT name FROM sqlite_master WHERE type='table'")
tables = [r[0] for r in c.fetchall()]
print(f"Tables: {tables}")

# Check story 23
c.execute("SELECT id, characters, folder FROM stories WHERE id = 23")
r = c.fetchone()
if r:
    print(f"\nStory ID: {r[0]}")
    print(f"Folder: {r[2]}")
    print(f"Characters (raw): [{r[1]}]")
    print(f"Characters is None: {r[1] is None}")
    print(f"Characters is empty string: {r[1] == ''}")
    if r[1]:
        print(f"Characters length: {len(r[1])}")
else:
    print("Story 23 not found")

conn.close()
