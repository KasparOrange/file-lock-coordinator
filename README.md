# File Lock Coordinator

A lightweight coordinator process that enables safe concurrent file editing across multiple Claude Code sessions. When multiple agents try to edit the same file, the coordinator ensures they take turns - preventing race conditions and file corruption.

## Problem Statement

When running multiple Claude Code sessions concurrently (e.g., parallel agents, multiple terminal windows), file edits can collide:
- No built-in file locking in Claude Code
- Race conditions cause file corruption
- Current workarounds are git worktrees or serial execution

## Solution

The File Lock Coordinator provides cooperative file locking through Claude Code hooks:
- **Zero context tokens used** - all coordination happens in hooks
- **Transparent blocking** - agents wait for locks automatically
- **Automatic cleanup** - locks expire after 5 minutes (TTL)

## Installation

### Install as Claude Code Plugin (Recommended)

```bash
/plugin install /path/to/file-lock-coordinator --scope project
```

For example:
```bash
/plugin install /Users/konradentner/code/file-lock-coordinator --scope project
```

This installs the plugin for the current project, including:
- Hook scripts that request/release locks
- Pre-built coordinator binary
- Automatic coordinator startup on session start

### Verify Installation

After starting a new Claude Code session, verify the coordinator is running:

```bash
curl http://localhost:9876/health
```

Expected response: `{"ok":true}`

## See It In Action: Conflict Test

This repository includes test files to demonstrate the lock coordinator working. Open **two terminal windows** in the same project directory and run these commands to create an intentional conflict.

### Terminal 1 - First Agent

```bash
claude --print "Edit the file test-files/shared-config.txt: Add a new section called [agent_one] at the bottom with these settings: name = Agent One, started_at = (current timestamp), task = primary configuration, status = working. Then WAIT and count to 20 slowly (print each number) before adding a final line 'completed = true' to your section."
```

### Terminal 2 - Second Agent (start while Terminal 1 is counting)

```bash
claude --print "Edit the file test-files/shared-config.txt: Add a new section called [agent_two] at the bottom with these settings: name = Agent Two, started_at = (current timestamp), task = secondary configuration, status = working, completed = true."
```

### What to Expect

**Without the lock coordinator:**
- Both agents edit simultaneously
- File gets corrupted or one agent's changes are lost

**With the lock coordinator:**
- Agent One acquires the lock and starts editing
- Agent Two's edit is **blocked** (waits) until Agent One finishes
- Both sections appear correctly in the final file

### How to Confirm It's Working

Look for these indicators:

1. **In Terminal 2's output** - You may see a delay before the edit starts (while waiting for the lock)

2. **Hook output** - If the wait is longer than 1 second, you'll see:
   ```
   Lock acquired after 15.3s wait
   ```

3. **Check coordinator status** - While Agent One is editing, run:
   ```bash
   curl -s http://localhost:9876/status | jq
   ```
   You'll see the active lock:
   ```json
   {
     "locks": [
       {
         "session": "session-id-here",
         "file": "/path/to/test-files/shared-config.txt",
         "acquiredAt": "2024-01-25T..."
       }
     ]
   }
   ```

4. **Final file is intact** - After both complete, `test-files/shared-config.txt` contains both `[agent_one]` and `[agent_two]` sections with no corruption

## Configuration

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `FILE_LOCK_PORT` | `9876` | Port for the coordinator HTTP server |

### Supported Platforms

| Platform | Binary |
|----------|--------|
| macOS (Apple Silicon) | `bin/coordinator-osx-arm64` |
| macOS (Intel) | `bin/coordinator-osx-x64` |
| Linux (x64) | `bin/coordinator-linux-x64` |
| Windows (x64) | `bin/coordinator-win-x64.exe` |

## API Reference

### `GET /health`
Health check. Returns `{"ok":true}`.

### `POST /lock`
Acquire a lock on a file (queue-based).

Query parameters:
- `wait` (default: `true`) - Wait in queue until lock available
- `timeout` (default: `300s`) - Maximum wait time

Request body:
```json
{"session": "session-id", "file": "/path/to/file"}
```

Response (lock acquired):
```json
{"granted": true, "position": 1, "queueLength": 1, "waited": 2.5}
```

Response (queued, waiting):
```json
{"granted": false, "holder": "other-session", "position": 2, "queueLength": 2, "error": "Queued at position 2"}
```

### `POST /unlock`
Release a lock (promotes next in queue).

Request body:
```json
{"session": "session-id", "file": "/path/to/file"}
```

### `POST /unlock-all`
Release all locks for a session.

Request body:
```json
{"session": "session-id"}
```

### `GET /status`
List all active locks.

### `GET /queues`
List all queues with waiters.

Response:
```json
{
  "count": 1,
  "queues": [
    {
      "file": "/path/to/file",
      "holder": "session-1",
      "acquiredAt": "2026-01-26T...",
      "queueLength": 3,
      "waiters": ["session-2", "session-3"]
    }
  ]
}

## Building from Source

Requires: .NET 10 SDK

```bash
# Run tests
dotnet test

# Build AOT binary for your platform
dotnet publish FileLockCoordinator -c Release -r osx-arm64 --self-contained /p:PublishAot=true -o bin/
mv bin/FileLockCoordinator bin/coordinator-osx-arm64
```

## Architecture

- **.NET 10 with Native AOT** - Self-contained ~9MB binary, ~15ms startup
- **Minimal API** - Simple HTTP endpoints
- **Thread-safe** - ConcurrentDictionary for lock storage
- **Blocking wait** - Agents wait transparently for locks (no polling)

## License

MIT
