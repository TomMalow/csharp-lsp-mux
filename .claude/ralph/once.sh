#!/bin/bash

cd "$(git rev-parse --show-toplevel)" || exit 1

commits=$(git log -n 5 --format="%H%n%ad%n%B---" --date=short 2>/dev/null || echo "no commits found")
prompt=$(cat .claude/ralph/prompt.md)

claude --permission-mode acceptEdits \
  "Previous commits: $commits. Look at github for issues to work on. $prompt"
