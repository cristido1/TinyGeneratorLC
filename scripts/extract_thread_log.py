import argparse
import sqlite3
from pathlib import Path
from textwrap import shorten


def main() -> int:
    parser = argparse.ArgumentParser(description="Extract TinyGenerator Log rows for a given ThreadId")
    parser.add_argument("thread_id", type=int, help="ThreadId to extract")
    parser.add_argument("--db", default="data/storage.db", help="Path to sqlite db (default: data/storage.db)")
    parser.add_argument("--out", default="", help="Optional output file path")
    parser.add_argument("--max", type=int, default=0, help="Max rows to print (0 = all)")
    parser.add_argument("--contains", default="", help="Only print rows where Message or ChatText contains this substring (case-insensitive)")
    parser.add_argument("--wide", action="store_true", help="Do not shorten output lines")
    args = parser.parse_args()

    db_path = Path(args.db)
    if not db_path.exists():
        raise SystemExit(f"DB not found: {db_path}")

    conn = sqlite3.connect(str(db_path))
    cur = conn.cursor()

    # Table is mapped as [Table("Log")] in Models/LogEntry.cs
    table = "Log"

    cur.execute(f"PRAGMA table_info({table})")
    cols = [r[1] for r in cur.fetchall()]
    if not cols:
        raise SystemExit(f"No columns found for table {table}. DB schema mismatch?")

    preferred = [
        "Id",
        "Ts",
        "Level",
        "Category",
        "Message",
        "chat_text",
        "ChatText",
        "State",
        "Result",
        "ThreadId",
        "story_id",
        "StoryId",
        "ThreadScope",
        "AgentName",
        "StepNumber",
        "MaxStep",
        "Exception",
    ]
    select_cols = [c for c in preferred if c in cols]
    if not select_cols:
        select_cols = cols

    sql = f"SELECT {', '.join(select_cols)} FROM {table} WHERE ThreadId = ? ORDER BY Id ASC"
    cur.execute(sql, (args.thread_id,))
    rows = cur.fetchall()

    needle = args.contains.lower().strip()

    out_lines: list[str] = []
    out_lines.append(f"# ThreadId={args.thread_id} rows={len(rows)} table={table}\n")

    printed = 0
    for row in rows:
        rec = dict(zip(select_cols, row))
        msg = (rec.get("Message") or "")
        chat = (rec.get("ChatText") or rec.get("chat_text") or "")
        hay = (msg + "\n" + chat).lower()
        if needle and needle not in hay:
            continue

        header = (
            f"--- Id={rec.get('Id')} Ts={rec.get('Ts')} Level={rec.get('Level')} "
            f"Cat={rec.get('Category')} StoryId={rec.get('StoryId') or rec.get('story_id')} "
            f"Agent={rec.get('AgentName')} Step={rec.get('StepNumber')}/{rec.get('MaxStep')} Result={rec.get('Result')} ---"
        )
        out_lines.append(header)

        body_parts: list[str] = []
        if msg:
            body_parts.append("Message: " + msg.replace("\r\n", "\n").strip())
        if chat and chat != msg:
            body_parts.append("ChatText: " + chat.replace("\r\n", "\n").strip())
        exc = rec.get("Exception")
        if exc:
            body_parts.append("Exception: " + str(exc).replace("\r\n", "\n").strip())

        body = "\n".join(body_parts) if body_parts else "(no message/chattext)"
        if not args.wide:
            body = shorten(body.replace("\n", "\\n"), width=800, placeholder=" …")
        out_lines.append(body)
        out_lines.append("")

        printed += 1
        if args.max and printed >= args.max:
            break

    conn.close()

    output = "\n".join(out_lines)

    if args.out:
        out_path = Path(args.out)
        out_path.write_text(output, encoding="utf-8")
        print(f"Wrote: {out_path} ({printed} rows printed)")
    else:
        print(output)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
