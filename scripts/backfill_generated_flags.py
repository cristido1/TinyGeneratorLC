import os
import sqlite3
from pathlib import Path

BASE_DIR = Path(os.getcwd())
DB_PATH = BASE_DIR / 'data' / 'storage.db'
STORIES_FOLDER = BASE_DIR / 'stories_folder'

if not DB_PATH.exists():
    print('DB not found:', DB_PATH)
    raise SystemExit(1)

conn = sqlite3.connect(DB_PATH)
cur = conn.cursor()
cur.execute('SELECT id, folder FROM stories')
rows = cur.fetchall()

updated = []
for sid, folder in rows:
    flags = {
        'generated_tts_json': 0,
        'generated_tts': 0,
        'generated_ambient': 0,
        'generated_effects': 0,
        'generated_music': 0,
        'generated_mixed_audio': 0
    }
    if not folder:
        # update to zeros
        cur.execute('UPDATE stories SET generated_tts_json=?, generated_tts=?, generated_ambient=?, generated_effects=?, generated_music=?, generated_mixed_audio=? WHERE id=?',
                    (0,0,0,0,0,0,sid))
        conn.commit()
        continue
    folder_path = STORIES_FOLDER / folder
    if not folder_path.exists() or not folder_path.is_dir():
        # still update flags to 0
        cur.execute('UPDATE stories SET generated_tts_json=?, generated_tts=?, generated_ambient=?, generated_effects=?, generated_music=?, generated_mixed_audio=? WHERE id=?',
                    (0,0,0,0,0,0,sid))
        conn.commit()
        continue

    files = [f.name.lower() for f in folder_path.iterdir() if f.is_file()]
    # tts_schema.json
    if 'tts_schema.json' in files:
        flags['generated_tts_json'] = 1
    # final mix
    if 'final_mix.wav' in files or 'final_mix.mp3' in files:
        flags['generated_mixed_audio'] = 1
    # music: any file with 'music' or ends with .mp3/.wav and name contains music
    for fn in files:
        if fn.endswith('.wav') or fn.endswith('.mp3'):
            if 'music' in fn:
                flags['generated_music'] = 1
            if 'ambience' in fn or 'ambience' in fn or 'amb_' in fn:
                flags['generated_ambient'] = 1
            if 'fx' in fn or 'effect' in fn or 'sfx' in fn:
                flags['generated_effects'] = 1
    # generated_tts: any wav file that is not music/ambience/fx/final_mix
    for fn in files:
        if not (fn.endswith('.wav') or fn.endswith('.mp3')):
            continue
        lower = fn
        if lower in ('final_mix.wav','final_mix.mp3'): 
            continue
        if any(x in lower for x in ('music','ambience','amb_','fx','effect','sfx')):
            continue
        # otherwise count as tts voice file
        flags['generated_tts'] = 1
        break

    cur.execute('UPDATE stories SET generated_tts_json=?, generated_tts=?, generated_ambient=?, generated_effects=?, generated_music=?, generated_mixed_audio=? WHERE id=?',
                (flags['generated_tts_json'], flags['generated_tts'], flags['generated_ambient'], flags['generated_effects'], flags['generated_music'], flags['generated_mixed_audio'], sid))
    conn.commit()
    updated.append((sid, folder, flags))

print(f'Updated {len(updated)} stories with detected flags')
for u in updated:
    print(u)

conn.close()
