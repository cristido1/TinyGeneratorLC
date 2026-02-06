import argparse
import sqlite3


def main() -> int:
    parser = argparse.ArgumentParser(description="Dump recent rows from data/storage.db Log table")
    parser.add_argument("--db", default="data/storage.db", help="Path to SQLite db (default: data/storage.db)")
    parser.add_argument("--limit", type=int, default=60, help="Max rows (default: 60)")
    parser.add_argument(
        "--only-model",
        action="store_true",
        help="Show only model traffic rows (ModelCompletion/ModelResponse/ModelRequest/ModelPrompt)")
    parser.add_argument(
        "--category",
        action="append",
        default=[],
        help="Filter by Category (repeatable). Example: --category ResponseValidation --category ResponseChecker",
    )
    parser.add_argument(
        "--agent",
        action="append",
        default=[],
        help="Filter by AgentName (repeatable). Example: --agent Formatter --agent 'Response Checker'",
    )
    parser.add_argument(
        "--contains",
        default=None,
        help="Filter Message by substring (case-insensitive, SQL LIKE). Example: --contains IS_VALID]false",
    )
    args = parser.parse_args()

    conn = sqlite3.connect(args.db)
    cur = conn.cursor()

    where_clauses = []
    parameters = []
    if args.only_model:
        where_clauses.append("Category IN ('ModelCompletion','ModelResponse','ModelRequest','ModelPrompt')")

    if args.category:
        placeholders = ",".join(["?"] * len(args.category))
        where_clauses.append(f"Category IN ({placeholders})")
        parameters.extend(args.category)

    if args.contains:
        where_clauses.append("LOWER(Message) LIKE ?")
        parameters.append(f"%{args.contains.lower()}%")

    if args.agent:
        placeholders = ",".join(["?"] * len(args.agent))
        where_clauses.append(f"AgentName IN ({placeholders})")
        parameters.extend(args.agent)

    where = ""
    if where_clauses:
        where = "WHERE " + " AND ".join(where_clauses)

    parameters.append(max(1, args.limit))
    cur.execute(
        f"""
        SELECT
            Id,
            Ts,
            ThreadId,
            ThreadScope,
            Category,
            AgentName,
            model_name,
            Result,
            ResultFailReason,
            Examined,
            substr(Message, 1, 140),
            substr(chat_text, 1, 180)
        FROM Log
        {where}
        ORDER BY Id DESC
        LIMIT ?
        """,
        tuple(parameters),
    )
    rows = cur.fetchall()

    print(
        "Id | Ts | ThreadId | Scope | Category | Agent | Model | Result | Examined | FailReason | ChatText | Message"
    )
    for (
        log_id,
        ts,
        thread_id,
        scope,
        category,
        agent,
        model,
        result,
        fail_reason,
        examined,
        msg,
        chat,
    ) in rows:
        scope_short = (scope or "")
        if len(scope_short) > 40:
            scope_short = scope_short[:40] + "…"
        fail_short = (fail_reason or "")
        if len(fail_short) > 70:
            fail_short = fail_short[:70] + "…"
        msg_short = (msg or "")
        msg_short = msg_short.replace("\n", " ").replace("\r", " ")

        chat_short = (chat or "")
        chat_short = chat_short.replace("\n", " ").replace("\r", " ")
        print(
            f"{log_id} | {ts} | {thread_id} | {scope_short} | {category} | {agent} | {model} | {result} | {examined} | {fail_short} | {chat_short} | {msg_short}"
        )

    conn.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
