import sqlite3
import os

db = os.path.join(os.path.dirname(__file__), '..', 'data', 'storage.db')
conn = sqlite3.connect(db)
cur = conn.cursor()

query = '''
SELECT s.id, s.model_id as story_model_id, s.agent_id, a.model_id as agent_model_id, m.name as story_model_name, am.name as agent_model_name, a.name as agent_name
FROM stories s
LEFT JOIN agents a ON a.id = s.agent_id
LEFT JOIN models m ON m.id = s.model_id
LEFT JOIN models am ON am.id = a.model_id
ORDER BY s.id;
'''

rows = cur.execute(query).fetchall()

mismatches = []
for r in rows:
    sid, story_mid, aid, agent_mid, story_mname, agent_mname, agent_name = r
    if aid is not None and agent_mid is not None:
        if story_mid != agent_mid:
            mismatches.append((sid, story_mid, story_mname, aid, agent_mid, agent_mname, agent_name))

print(f"Total stories: {len(rows)}")
print(f"Mismatches (story.model_id != agent.model_id): {len(mismatches)}")
if mismatches:
    print('\nSample mismatches:')
    for m in mismatches[:50]:
        print(f"story_id={m[0]}, story_model_id={m[1]} ({m[2]}), agent_id={m[3]}, agent_model_id={m[4]} ({m[5]}), agent_name={m[6]}")

conn.close()
