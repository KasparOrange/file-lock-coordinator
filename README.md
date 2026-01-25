# File Lock Coordinator

A lightweight coordinator process that enables safe concurrent file editing across multiple Claude Code sessions. Sessions register with the coordinator and request permission before editing files, preventing race conditions and data corruption.

## Problem Statement

When running multiple Claude Code sessions concurrently (e.g., parallel agents, multiple terminal windows), file edits can collide:
- No built-in file locking in Claude Code
- Race conditions cause file corruption
- Current workarounds are git worktrees (separate directories) or serial execution

## Solution

The File Lock Coordinator provides cooperative file locking through HTTP hooks:

```
+-------------+     +-------------+     +-------------+
|  Claude A   |     |  Claude B   |     |  Claude C   |
+------+------+     +------+------+     +------+------+
       |                   |                   |
       | PreToolUse Hook   |                   |
       v                   v                   v
+-----------------------------------------------------+
|         File Lock Coordinator (localhost:9876)       |
|                                                      |
|  POST /lock   { session, file } -> { granted: bool } |
|  POST /unlock { session, file } -> { ok: bool }      |
|  GET  /status                   -> lock table        |
+-----------------------------------------------------+
```

**Key design principle:** Hook-based integration = **zero context tokens** used.

## Installation

### Option 1: Download Pre-built Binary

Download the appropriate binary for your platform from the [releases page](https://github.com/your-org/file-lock-coordinator/releases):

| Platform | Binary |
|----------|--------|
| macOS (Apple Silicon) | `coordinator-osx-arm64` |
| macOS (Intel) | `coordinator-osx-x64` |
| Linux (x64) | `coordinator-linux-x64` |
| Windows (x64) | `coordinator-win-x64.exe` |

Place the binary in the `bin/` directory of this project.

### Option 2: Build from Source

Requirements: .NET 10 SDK

```bash
# Clone the repository
git clone https://github.com/your-org/file-lock-coordinator.git
cd file-lock-coordinator

# Build and publish AOT binary
dotnet publish FileLockCoordinator -c Release -r osx-arm64 --self-contained /p:PublishAot=true -o bin/
mv bin/FileLockCoordinator bin/coordinator-osx-arm64

# Or for other platforms:
# dotnet publish FileLockCoordinator -c Release -r osx-x64 --self-contained /p:PublishAot=true
# dotnet publish FileLockCoordinator -c Release -r linux-x64 --self-contained /p:PublishAot=true
# dotnet publish FileLockCoordinator -c Release -r win-x64 --self-contained /p:PublishAot=true
```

## Configuration

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `FILE_LOCK_PORT` | `9876` | Port for the coordinator HTTP server |
| `CLAUDE_SESSION_ID` | (from hook input) | Session identifier for lock ownership |

### Claude Code Hook Setup

Add to your Claude Code settings (`.claude/settings.json`):

```json
{
  "hooks": {
    "SessionStart": [
      {
        "type": "command",
        "command": "/path/to/file-lock-coordinator/hooks/ensure-coordinator.sh"
      }
    ],
    "PreToolUse": [
      {
        "matcher": "Edit|Write",
        "type": "command",
        "command": "/path/to/file-lock-coordinator/hooks/request-lock.sh --wait --timeout 60"
      }
    ],
    "PostToolUse": [
      {
        "matcher": "Edit|Write",
        "type": "command",
        "command": "/path/to/file-lock-coordinator/hooks/release-lock.sh"
      }
    ]
  }
}
```

Or use the provided `plugin.json` by setting `CLAUDE_PLUGIN_ROOT` to this directory.

## How It Works

### Lock Acquisition (Blocking Wait)

When Claude attempts to edit a file, the `request-lock.sh` hook:
1. Sends a POST request to `/lock?wait=true&timeout=60s`
2. If the lock is available, it's granted immediately
3. If held by another session, the request blocks until the lock is released or timeout
4. Claude never sees a failure unless something is genuinely wrong

```
Claude: "Edit file.ts"
    |
    v
+---------------------------------------+
| PreToolUse Hook                       |
|                                       |
| POST /lock?wait=true&timeout=60s      |
|     |                                 |
| Coordinator holds request open        |
| until lock is available or timeout    |
|     |                                 |
| Returns: { granted: true }            |
| Hook exits 0                          |
+---------------------------------------+
    |
    v
Edit proceeds (Claude never knew there was contention)
```

### Automatic Lock Expiry

Locks automatically expire after 5 minutes (TTL) to prevent deadlocks from crashed sessions.

## API Reference

### `GET /health`

Health check endpoint.

**Response:**
```json
{ "ok": true }
```

### `POST /lock`

Acquire a lock on a file.

**Query Parameters:**
- `wait` (default: `true`) - Block until lock is available
- `timeout` (default: `60s`) - Maximum wait time (e.g., `30s`, `2m`)

**Request Body:**
```json
{
  "session": "session-id",
  "file": "/path/to/file.ts"
}
```

**Response:**
```json
{
  "granted": true,
  "waited": 2.5
}
```

Or if denied:
```json
{
  "granted": false,
  "holder": "other-session",
  "error": "Timeout waiting for lock",
  "waited": 60.0
}
```

### `POST /unlock`

Release a lock on a file.

**Request Body:**
```json
{
  "session": "session-id",
  "file": "/path/to/file.ts"
}
```

**Response:**
```json
{ "ok": true }
```

### `POST /unlock-all`

Release all locks held by a session.

**Request Body:**
```json
{
  "session": "session-id"
}
```

**Response:**
```json
{ "count": 3 }
```

### `GET /status`

Get all current locks.

**Response:**
```json
{
  "locks": [
    {
      "session": "session-1",
      "file": "/path/to/file.ts",
      "acquiredAt": "2024-01-15T10:30:00Z"
    }
  ]
}
```

## Development

### Running Tests

```bash
# Run unit tests
dotnet test FileLockCoordinator.Tests/

# Run with verbose output
dotnet test FileLockCoordinator.Tests/ -v normal
```

### Building

```bash
# Debug build
dotnet build FileLockCoordinator/

# Release build
dotnet build FileLockCoordinator/ -c Release

# AOT publish
dotnet publish FileLockCoordinator -c Release -r osx-arm64 --self-contained /p:PublishAot=true
```

### Manual Testing

```bash
# Start the coordinator
./bin/coordinator-osx-arm64 --urls=http://localhost:9876

# In another terminal:
# Health check
curl http://localhost:9876/health

# Acquire lock
curl -X POST http://localhost:9876/lock \
  -H "Content-Type: application/json" \
  -d '{"session": "test", "file": "/test.txt"}'

# Check status
curl http://localhost:9876/status

# Release lock
curl -X POST http://localhost:9876/unlock \
  -H "Content-Type: application/json" \
  -d '{"session": "test", "file": "/test.txt"}'
```

## Architecture

### Technology

- **.NET 10 with Native AOT**: Self-contained executables with no runtime dependency
- **Minimal API**: Clean, concise HTTP server code
- **Thread-safe**: ConcurrentDictionary for lock storage
- **Async/await**: Non-blocking wait for lock availability

### Binary Characteristics

| Metric | Value |
|--------|-------|
| Binary size | ~10 MB |
| Startup time | ~15 ms |
| Memory usage | ~20 MB |
| Dependencies | None (self-contained) |

## License

MIT
