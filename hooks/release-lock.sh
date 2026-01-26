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

# Validate session ID
if [ -z "$SESSION_ID" ] || [ "$SESSION_ID" = "null" ]; then
    echo "Warning: No session ID available for lock release on $FILE_PATH" >&2
    exit 0
fi

# Check if coordinator is running
if ! nc -z 127.0.0.1 "$PORT" 2>/dev/null; then
    exit 0
fi

# Release lock and check result
RESPONSE=$(curl -s -X POST "http://localhost:$PORT/unlock" \
    -H "Content-Type: application/json" \
    -d "{\"session\": \"$SESSION_ID\", \"file\": \"$FILE_PATH\"}" 2>&1)

if [ $? -ne 0 ]; then
    echo "Warning: Failed to contact coordinator for lock release on $FILE_PATH" >&2
    exit 0
fi

OK=$(echo "$RESPONSE" | jq -r '.ok // false')
if [ "$OK" != "true" ]; then
    echo "Warning: Lock release failed for $FILE_PATH (session: $SESSION_ID) - lock may be held by different session" >&2
fi

exit 0
