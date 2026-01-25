#!/bin/bash
set -e

PLUGIN_ROOT="${CLAUDE_PLUGIN_ROOT:-$(dirname "$0")/..}"
PORT="${FILE_LOCK_PORT:-9876}"

# Detect platform and select binary
case "$(uname -s)-$(uname -m)" in
    Darwin-arm64)  BIN="$PLUGIN_ROOT/bin/coordinator-osx-arm64" ;;
    Darwin-x86_64) BIN="$PLUGIN_ROOT/bin/coordinator-osx-x64" ;;
    Linux-x86_64)  BIN="$PLUGIN_ROOT/bin/coordinator-linux-x64" ;;
    MINGW*|CYGWIN*|MSYS*) BIN="$PLUGIN_ROOT/bin/coordinator-win-x64.exe" ;;
    *)
        echo "Unsupported platform: $(uname -s)-$(uname -m)" >&2
        exit 0  # Allow session to continue without coordinator
        ;;
esac

# Check if binary exists
if [ ! -f "$BIN" ]; then
    echo "Coordinator binary not found: $BIN" >&2
    exit 0  # Allow session to continue
fi

# Check if already running
if nc -z 127.0.0.1 "$PORT" 2>/dev/null; then
    exit 0  # Already running
fi

# Start coordinator in background
"$BIN" --urls="http://localhost:$PORT" &

# Wait for startup (max 5 seconds)
for i in {1..10}; do
    sleep 0.5
    if nc -z 127.0.0.1 "$PORT" 2>/dev/null; then
        exit 0  # Started successfully
    fi
done

echo "Coordinator failed to start within timeout" >&2
exit 0  # Allow session to continue (graceful degradation)
