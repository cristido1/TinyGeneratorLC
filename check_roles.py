import sqlite3

conn = sqlite3.connect('data/storage.db')
cursor = conn.cursor()

cursor.execute('SELECT * FROM roles')
print('=== ROLES ===')
for row in cursor.fetchall():
    print(row)

cursor.execute('SELECT * FROM model_roles')
print('\n=== MODEL ROLES ===')
rows = cursor.fetchall()
if not rows:
    print('(empty)')
else:
    for row in rows:
        print(row)

conn.close()
