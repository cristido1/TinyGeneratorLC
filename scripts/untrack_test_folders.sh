#!/usr/bin/env bash
set -euo pipefail

echo "Untracking test_run_folders and wwwroot/tts_test from git (cached removal)"

if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "Not a git repository. Run this script from the repository root." >&2
  exit 2
fi

git rm -r --cached --quiet "test_run_folders" || true
git rm -r --cached --quiet "wwwroot/tts_test" || true

echo "Files untracked. Commit the change to update repository index."
echo "Suggested commands:"
echo "  git add .gitignore"
echo "  git commit -m 'Remove test run folders from repo and ignore them'"
