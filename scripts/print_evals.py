import sqlite3, os, json, sys

db_path = os.path.join(os.path.dirname(__file__), '..', 'data', 'storage.db')
if not os.path.exists(db_path):
    print('DB not found:', db_path)
    sys.exit(1)

conn = sqlite3.connect(db_path)
cur = conn.cursor()
story_id = 121

query = '''SELECT id, story_id, agent_id, model_id, timestamp, raw_json
FROM stories_evaluations
WHERE story_id = ?
ORDER BY timestamp DESC
LIMIT 10'''

rows = cur.execute(query, (story_id,)).fetchall()
if not rows:
    print('No evaluations found for story', story_id)
else:
    for row in rows:
        id, sid, aid, mid, ts, raw = row
        print('--- eval id', id, 'story', sid, 'agent', aid, 'model', mid, 'ts', ts)
        if raw is None:
            print('(raw_json is NULL)')
        else:
            try:
                # pretty print if JSON
                parsed = json.loads(raw)
                print(json.dumps(parsed, indent=2, ensure_ascii=False))
            except Exception:
                print(raw)
        print()

conn.close()
