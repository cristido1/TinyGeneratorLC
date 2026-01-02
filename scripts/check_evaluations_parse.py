import sqlite3, os, json, re

WORKDIR = os.path.join(r"c:\Users\User\Documents\ai\TinyGeneratorLC")
DB = os.path.join(WORKDIR, 'data', 'storage.db')
OUT = os.path.join(WORKDIR, 'data', 'evals_parse_report.json')

headings = ["Coerenza narrativa", "Originalità", "Impatto emotivo", "Azione"]

def unwrap_raw(raw):
    if not raw:
        return raw
    # If raw looks like JSON with role/content, try to extract assistant content
    try:
        obj = json.loads(raw)
        # if it's a dict with role/content
        if isinstance(obj, dict):
            if obj.get('role') and obj.get('content'):
                return obj.get('content')
            # sometimes nested
            if 'message' in obj and isinstance(obj['message'], dict) and obj['message'].get('content'):
                return obj['message'].get('content')
        # if it's a list of messages
        if isinstance(obj, list):
            for el in obj:
                if isinstance(el, dict) and el.get('role','').lower() == 'assistant' and el.get('content'):
                    return el.get('content')
        # fallback: if dict has 'content' as string
        if isinstance(obj, dict) and 'content' in obj and isinstance(obj['content'], str):
            return obj['content']
    except Exception:
        # not JSON, fall through
        pass
    return raw


def normalize_text(text):
    if text is None:
        return ''
    s = text.replace('\r\n', '\n')
    # collapse internal whitespace per line, trim ends but keep blank lines
    lines = [re.sub(r'\s+', ' ', l).rstrip() for l in s.split('\n')]
    return '\n'.join(lines).strip()


def try_parse(text):
    parsed = {}
    error = None
    if not text or not text.strip():
        return False, None, 'empty'
    normalized = normalize_text(text)
    # find headings
    def extract(heading):
        low = normalized.lower()
        hlow = heading.lower()
        idx = low.find(hlow)
        if idx < 0:
            return None
        after = idx + len(heading)
        next_idx = len(normalized)
        for h in headings:
            if h.lower() == hlow:
                continue
            j = low.find(h.lower(), after)
            if j != -1 and j < next_idx:
                next_idx = j
        section = normalized[after:next_idx].strip()
        if not section:
            return (None, None)
        lines = [ln.strip() for ln in section.split('\n') if ln.strip()!='']
        score = None
        for i, ln in enumerate(lines):
            m = re.match(r'^\s*([1-5])\s*$', ln)
            if m:
                score = int(m.group(1))
                explanation = ' '.join(lines[i+1:]).strip()
                return (score, explanation)
        return (None, None)

    results = {}
    missing = []
    for h in headings:
        res = extract(h)
        if res is None:
            missing.append(h)
        else:
            score, explanation = res
            if score is None:
                return False, None, f'missing_score_for_{h}'
            results[h] = {'score': score, 'explanation': explanation}
    if missing:
        return False, None, 'missing_sections:' + ','.join(missing)
    return True, results, None


def main(limit=1000):
    if not os.path.exists(DB):
        print('DB not found', DB)
        return
    conn = sqlite3.connect(DB)
    cur = conn.cursor()
    rows = cur.execute("SELECT id, story_id, model_id, agent_id, ts, raw_json FROM stories_evaluations WHERE raw_json IS NOT NULL ORDER BY ts DESC LIMIT ?", (limit,)).fetchall()
    out = []
    stats = {'checked': 0, 'parsed': 0, 'failed': 0}
    for r in rows:
        id, story_id, model_id, agent_id, ts, raw = r
        stats['checked'] += 1
        unwrapped = unwrap_raw(raw)
        ok, results, err = try_parse(unwrapped)
        rec = {'id': id, 'story_id': story_id, 'model_id': model_id, 'agent_id': agent_id, 'ts': ts, 'ok': ok}
        if ok:
            rec['parsed'] = results
            stats['parsed'] += 1
        else:
            rec['error'] = err
            # include a safe snippet of unwrapped content
            snippet = (unwrapped or '')[:800]
            rec['snippet'] = snippet
            stats['failed'] += 1
        out.append(rec)
    conn.close()
    meta = {'stats': stats}
    with open(OUT, 'w', encoding='utf-8') as f:
        json.dump({'meta': meta, 'rows': out}, f, ensure_ascii=False, indent=2)
    print('Wrote report to', OUT)

if __name__ == '__main__':
    main(1000)
