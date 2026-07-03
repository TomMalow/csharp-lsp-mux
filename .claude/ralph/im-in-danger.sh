#!/bin/bash

# Usage: ./im-in-danger.sh <iterations>

set -e

cd "$(git rev-parse --show-toplevel)" || exit 1

if [ -z "$1" ]; then
  echo "Usage: $0 <iterations>"
  exit 1
fi

# For each iteration, run Claude Code with the following prompt.
# This prompt is basic, we'll expand it later.
for ((i = 1; i <= $1; i++)); do
  echo "Running Iteration: $i"
  commits=$(git log -n 5 --format="%H%n%ad%n%B---" --date=short 2>/dev/null || echo "no commits found")
  prompt=$(cat .claude/ralph/prompt.md)
  result=$(sbx run --name private-sandbox claude -- -p \
    "Previous commits: $commits. Look at github for issues to work on. $prompt")

  echo "$result"

  if [[ "$result" == *"<promise>NO MORE TASKS</promise>"* ]]; then
    echo "No More Tasks. Exiting"
    exit 0
  fi
  if [[ "$result" == *"<promise>TASK INCOMPLETE</promise>"* ]]; then
    echo "Task Incomplete. Exisiting - resolve broken state"
    exit 0
  fi
  if [[ "$result" == *"<promise>TASK COMPLETE</promise>"* ]]; then
    echo "Task complete."
  fi

done
