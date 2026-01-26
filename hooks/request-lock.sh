#!/bin/bash
set -e

PORT="${FILE_LOCK_PORT:-9876}"

# Parse hook input (JSON from stdin)
INPUT=$(cat)
FILE_PATH=$(echo "$INPUT" | jq -r '.tool_input.file_path // .tool_input.filePath // empty')
SESSION_ID="${CLAUDE_SESSION_ID:-$(echo "$INPUT" | jq -r '.session_id')}"

# Parse arguments (defaults: wait=true, timeout=300 for queue-based waiting)
WAIT="true"
TIMEOUT="300"
while [[ $# -gt 0 ]]; do
    case $1 in
        --wait) WAIT="true"; shift ;;
        --no-wait) WAIT="false"; shift ;;
        --timeout) TIMEOUT="$2"; shift 2 ;;
        *) shift ;;
    esac
done

# Skip if no file path
if [ -z "$FILE_PATH" ]; then
    exit 0
fi

# Validate session ID
if [ -z "$SESSION_ID" ] || [ "$SESSION_ID" = "null" ]; then
    echo "Warning: No session ID available for lock request on $FILE_PATH" >&2
    exit 0
fi

# Check if coordinator is running
if ! nc -z 127.0.0.1 "$PORT" 2>/dev/null; then
    echo "Coordinator not running, allowing edit" >&2
    exit 0
fi

# Request lock (queue-based)
RESPONSE=$(curl -s -X POST \
    "http://localhost:$PORT/lock?wait=$WAIT&timeout=${TIMEOUT}s" \
    -H "Content-Type: application/json" \
    -d "{\"session\": \"$SESSION_ID\", \"file\": \"$FILE_PATH\"}")

GRANTED=$(echo "$RESPONSE" | jq -r '.granted')
POSITION=$(echo "$RESPONSE" | jq -r '.position // 0')
QUEUE_LENGTH=$(echo "$RESPONSE" | jq -r '.queueLength // 0')

if [ "$GRANTED" = "true" ]; then
    WAITED=$(echo "$RESPONSE" | jq -r '.waited // 0')
    if (( $(echo "$WAITED > 1" | bc -l 2>/dev/null || echo 0) )); then
        echo "Lock acquired after ${WAITED}s queue wait" >&2
    fi
    exit 0
else
    ERROR=$(echo "$RESPONSE" | jq -r '.error // "Lock denied"')
    HOLDER=$(echo "$RESPONSE" | jq -r '.holder // "unknown"')
    echo "Failed to acquire lock on $FILE_PATH: $ERROR (held by $HOLDER, queue position: $POSITION/$QUEUE_LENGTH)" >&2
    exit 2
fi
