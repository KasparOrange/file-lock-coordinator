#!/bin/bash

PORT="${FILE_LOCK_PORT:-9876}"

# Parse hook input
INPUT=$(cat)
FILE_PATH=$(echo "$INPUT" | jq -r '.tool_input.file_path // .tool_input.filePath // empty')
SESSION_ID="${CLAUDE_SESSION_ID:-$(echo "$INPUT" | jq -r '.session_id')}"

# Skip if no file path
if [ -z "$FILE_PATH" ]; then
    exit 0
fi

# Check if coordinator is running
if ! nc -z 127.0.0.1 "$PORT" 2>/dev/null; then
    exit 0
fi

# Release lock (fire and forget, don't block on failure)
curl -s -X POST "http://localhost:$PORT/unlock" \
    -H "Content-Type: application/json" \
    -d "{\"session\": \"$SESSION_ID\", \"file\": \"$FILE_PATH\"}" \
    >/dev/null 2>&1 || true

exit 0
