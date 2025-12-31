import sqlite3
import argparse


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=r"data\storage.db")
    ap.add_argument("--limit", type=int, default=200)
    ap.add_argument("--category", default=None)
    ap.add_argument("--level", default=None)
    args = ap.parse_args()

    conn = sqlite3.connect(args.db)
    cur = conn.cursor()

    where = []
    params = []
    if args.category:
        where.append("Category = ?")
        params.append(args.category)
    if args.level:
        where.append("Level = ?")
        params.append(args.level)

    sql = "SELECT Id, Ts, Level, Category, Message, Exception FROM Log"
    if where:
        sql += " WHERE " + " AND ".join(where)
    sql += " ORDER BY Id DESC LIMIT ?"
    params.append(args.limit)

    rows = cur.execute(sql, params).fetchall()
    print(f"Rows: {len(rows)} (newest first)\n")
    for (id_, ts, level, category, message, exception) in rows:
        print("=" * 100)
        print(f"#{id_} {ts} [{level}] {category}")
        if message:
            print(message)
        if exception:
            print("\nEXCEPTION:\n" + exception)

    conn.close()


if __name__ == "__main__":
    main()
