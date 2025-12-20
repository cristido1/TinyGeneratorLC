import sqlite3
import os
import shutil
from datetime import datetime

DB_PATH = os.path.join(os.getcwd(), 'data', 'storage.db')
BASE_FOLDER = os.path.join(os.getcwd(), 'stories_folder')

if not os.path.exists(DB_PATH):
    print('Database not found:', DB_PATH)
    raise SystemExit(1)

conn = sqlite3.connect(DB_PATH)
cur = conn.cursor()
cur.execute("SELECT id, folder FROM stories")
rows = cur.fetchall()

updated = []
for row in rows:
    sid, folder = row
    if not folder:
        continue
    folder = str(folder)
    padded = f"{sid:05d}_"
    if folder.startswith(padded):
        continue

    # detect other prefixes
    numeric_prefix = f"{sid}_"
    story_prefix = f"story_{sid}_"
    rest = folder
    if folder.startswith(numeric_prefix):
        rest = folder[len(numeric_prefix):]
    elif folder.startswith(story_prefix):
        rest = folder[len(story_prefix):]

    if not rest:
        rest = datetime.utcnow().strftime('%Y%m%d_%H%M%S')

    newname = f"{sid:05d}_{rest}"
    oldpath = os.path.join(BASE_FOLDER, folder)
    newpath = os.path.join(BASE_FOLDER, newname)
    moved = False
    try:
        if os.path.exists(oldpath):
            if os.path.exists(newpath):
                # avoid conflict
                newname = f"{sid:05d}_{rest}_{datetime.utcnow().strftime('%Y%m%d%H%M%S')}"
                newpath = os.path.join(BASE_FOLDER, newname)
            shutil.move(oldpath, newpath)
            moved = True
        # update DB
        cur.execute('UPDATE stories SET folder = ? WHERE id = ?', (newname, sid))
        conn.commit()
        updated.append((sid, folder, newname, moved))
    except Exception as e:
        print('Error processing', sid, folder, e)

print('Updated:', len(updated))
for u in updated:
    print(u)

conn.close()
