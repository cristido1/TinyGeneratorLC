import sqlite3

conn = sqlite3.connect('data/storage.db')
cur = conn.cursor()

# Get logs for the last execution - look for step 2 prompt
cur.execute("""
    SELECT Id, Category, Message 
    FROM Log 
    WHERE Message LIKE '%Genera la lista completa dei PERSONAGGI%' 
    ORDER BY Id DESC LIMIT 5
""")
rows = cur.fetchall()
for r in rows:
    print(f"=== Log {r[0]} ({r[1]}) ===")
    print(r[2][:2000])
    print()

conn.close()
