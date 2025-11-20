#!/usr/bin/env bash
set -euo pipefail

# Export enabled models from the SQLite `models` table into a SQL seed file
# Usage: ./scripts/export_models_seed.sh [path/to/storage.db] [output.sql]
# Defaults: data/storage.db -> data/models_seed.sql

DB_PATH="${1:-data/storage.db}"
OUT_PATH="${2:-data/models_seed.sql}"

if [ ! -f "$DB_PATH" ]; then
  echo "Database file not found: $DB_PATH" >&2
  exit 2
fi

echo "Exporting enabled models from $DB_PATH to $OUT_PATH"

# Use sqlite3 .mode insert to produce INSERT statements for the models table
# We restrict to Enabled = 1 to export only enabled models
{
  echo "-- Models seed generated on $(date -u +"%Y-%m-%dT%H:%M:%SZ")"
  echo "BEGIN TRANSACTION;"
  sqlite3 "$DB_PATH" <<SQL
.mode insert models
SELECT * FROM models WHERE Enabled = 1;
.quit
SQL
  echo "COMMIT;"
} > "$OUT_PATH"

echo "Done. Seed file: $OUT_PATH"

exit 0
