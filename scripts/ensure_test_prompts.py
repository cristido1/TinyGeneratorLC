import sqlite3
conn = sqlite3.connect('data/storage.db')
c = conn.cursor()
# Check if table exists
c.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='test_prompts'")
if not c.fetchone():
    print('Creating test_prompts table')
    c.execute('''
    CREATE TABLE test_prompts (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        group_name TEXT,
        library TEXT,
        prompt TEXT,
        active INTEGER DEFAULT 1,
        priority INTEGER DEFAULT 0
    )
    ''')
    conn.commit()
else:
    print('test_prompts already exists')
conn.close()
