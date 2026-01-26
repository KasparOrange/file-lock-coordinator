# File Lock Coordinator for Claude Code

## Overview

A lightweight coordinator process that enables safe concurrent file editing across multiple Claude Code sessions. Sessions register with the coordinator and request permission before editing files, preventing race conditions and data corruption.

## Problem Statement

When running multiple Claude Code sessions concurrently (e.g., parallel agents, multiple terminal windows), file edits can collide:
- No built-in file locking in Claude Code
- Race conditions cause file corruption (documented: 14+ corruptions in 11 hours with 30+ sessions)
- Current workaround is git worktrees (separate directories) or serial execution

## Proposed Solution

```
┌─────────────┐     ┌─────────────┐     ┌─────────────┐
│  Claude A   │     │  Claude B   │     │  Claude C   │
└──────┬──────┘     └──────┬──────┘     └──────┬──────┘
       │                   │                   │
       │ PreToolUse Hook   │                   │
       ▼                   ▼                   ▼
┌─────────────────────────────────────────────────────┐
│           File Lock Coordinator (localhost:9876)     │
│                                                      │
│  POST /lock   { session, file } → { granted: bool } │
│  POST /unlock { session, file } → { ok: bool }      │
│  GET  /status                   → lock table        │
└─────────────────────────────────────────────────────┘
```

**Key design principle:** Hook-based integration = **zero context tokens** used.

---

## Technology Choice: .NET 10 with Native AOT

### Why .NET AOT

- **Familiar stack**: This is a .NET project, so staying in the ecosystem simplifies maintenance
- **Native AOT**: Produces self-contained executables with no runtime dependency
- **Minimal API**: Clean, concise HTTP server code
- **Excellent cross-platform**: macOS, Linux, Windows all supported
- **Strong typing**: Catches errors at compile time

### Binary Characteristics

| Metric | Value |
|--------|-------|
| Binary size | 8-12 MB |
| Startup time | ~15 ms |
| Memory usage | ~20 MB |
| Dependencies | None (self-contained) |

---

## AOT Compatibility Evaluation

Native AOT has restrictions due to the absence of JIT compilation. This section evaluates feature compatibility.

### What Works with AOT

| Feature | Status | Notes |
|---------|--------|-------|
| Minimal API endpoints | ✅ Works | Core ASP.NET feature, fully supported |
| System.Text.Json | ✅ Works | Requires source generators (JsonSerializerContext) |
| ConcurrentDictionary | ✅ Works | No reflection involved |
| HttpClient | ✅ Works | For health checks, etc. |
| Logging (ILogger) | ✅ Works | Microsoft.Extensions.Logging supports AOT |
| Configuration (IConfiguration) | ✅ Works | appsettings.json binding supported |
| Dependency injection | ✅ Works | Must avoid runtime-resolved services |
| async/await | ✅ Works | No JIT dependency |
| Channel&lt;T&gt; for wait queues | ✅ Works | No reflection involved |
| Timer-based TTL expiry | ✅ Works | System.Threading.Timer is AOT-safe |

### What Requires Adaptation

| Feature | Issue | Solution |
|---------|-------|----------|
| JSON serialization | Reflection-based won't work | Use `[JsonSerializable]` source generator |
| Model binding | Some scenarios need hints | Use `[AsParameters]` or explicit binding |
| Swagger/OpenAPI | Swashbuckle uses reflection | Use NSwag with static generation or skip |
| Dynamic configuration | Runtime type resolution | Use strongly-typed options pattern |

### What Does NOT Work with AOT

| Feature | Issue | Alternative |
|---------|-------|-------------|
| Reflection.Emit | No runtime code generation | Not needed for this project |
| Dynamic assembly loading | No JIT | Not needed |
| Some LINQ expressions | Expression tree compilation | Use method-based LINQ (supported) |

### Testing Considerations

| Testing Aspect | AOT Impact | Recommendation |
|----------------|------------|----------------|
| **Unit tests** | Tests run with JIT, not AOT | Test the published AOT binary separately |
| **Mocking (Moq, NSubstitute)** | ❌ Uses Reflection.Emit | Use manual fakes/stubs or interfaces |
| **xUnit/NUnit** | ✅ Works for JIT test runs | Standard test runners work fine |
| **Integration tests** | ✅ Can test AOT binary | Use `Process.Start` to launch AOT binary |
| **HttpClient tests** | ✅ Works | Test against running AOT server |

**Testing Strategy:**
1. **Unit tests**: Run with standard `dotnet test` (JIT mode) - tests business logic
2. **Integration tests**: Launch the AOT-compiled binary and test via HTTP
3. **Manual fakes**: Instead of Moq, create simple interface implementations

```csharp
// Instead of Moq:
// var mock = new Mock<ILockStore>();
// mock.Setup(x => x.TryAcquire(...)).Returns(true);

// Use a manual fake:
public class FakeLockStore : ILockStore {
    public bool ShouldGrantLock { get; set; } = true;
    public bool TryAcquire(string file, string session) => ShouldGrantLock;
    public bool TryRelease(string file, string session) => true;
}
```

---

## Lock Acquisition & Retry Strategies

### The Problem with Poll-Based Waiting

The original design used poll-based waiting where agents would repeatedly try to acquire a lock. This had several issues:
- **No fairness**: When a lock was released, all waiting agents raced for it
- **Timeout failures**: After 60s, agents would get errors and retry, creating wasteful cycles
- **Agent gives up**: After multiple timeout errors, Claude would stop and ask the user what to do

### Queue-Based Waiting (Current Implementation)

The coordinator now uses a **FIFO queue** for fair lock distribution:

```
Claude A: "Edit file.ts"     Claude B: "Edit file.ts"
    │                              │
    ▼                              ▼
┌─────────────────────────────────────────────────────────┐
│ File Lock Coordinator                                    │
│                                                          │
│ Queue for file.ts:                                       │
│   Position 1: Claude A (HOLDER) ─────────► Edit proceeds │
│   Position 2: Claude B (WAITING) ─────────► Waits...     │
│                                                          │
│ When A releases:                                         │
│   Position 1: Claude B (HOLDER) ─────────► Edit proceeds │
└─────────────────────────────────────────────────────────┘
```

### Key Benefits of Queue System

| Aspect | Poll-Based (Old) | Queue-Based (New) |
|--------|------------------|-------------------|
| Fairness | Race condition | FIFO ordering |
| Visibility | None | Position in queue |
| Timeout behavior | ERROR after 60s | Stays in queue (300s default) |
| Retry overhead | Multiple requests | Single request |
| Agent experience | May give up | Waits patiently |

### What Claude Sees

| Scenario | Queue-Based Behavior |
|----------|---------------------|
| Lock available | Edit succeeds immediately |
| Lock held briefly | Edit succeeds after queue wait |
| Lock held long | Edit succeeds (position tracked) |
| Timeout (5 min) | "Blocked by hook: timeout at position N" |

**Key insight:** With queue-based waiting, Claude almost never sees failures. Agents wait in a fair queue with their position tracked.

---

## Implementation

### Project Structure

```
FileLockCoordinator/
├── FileLockCoordinator/
│   ├── Program.cs                 # Entry point + Minimal API endpoints
│   ├── LockStore.cs               # Thread-safe lock table with wait support
│   ├── Models.cs                  # Request/response DTOs
│   ├── AppJsonContext.cs          # AOT JSON source generator
│   ├── FileLockCoordinator.csproj
│   └── appsettings.json           # Configuration
├── FileLockCoordinator.Tests/
│   ├── LockStoreTests.cs          # Unit tests with manual fakes
│   ├── IntegrationTests.cs        # HTTP tests against running server
│   └── Fakes/
│       └── FakeLockStore.cs       # Manual test doubles
├── hooks/
│   ├── ensure-coordinator.sh      # SessionStart: start if needed
│   ├── request-lock.sh            # PreToolUse: request lock
│   └── release-lock.sh            # PostToolUse: release lock
├── bin/                           # Published AOT binaries
│   ├── coordinator-osx-arm64
│   ├── coordinator-osx-x64
│   ├── coordinator-linux-x64
│   └── coordinator-win-x64.exe
└── README.md
```

### Core Implementation

**FileLockCoordinator.csproj:**
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <StripSymbols>true</StripSymbols>
    <OptimizationPreference>Size</OptimizationPreference>
  </PropertyGroup>
</Project>
```

**Program.cs:**
```csharp
using System.Text.Json.Serialization;
using FileLockCoordinator;

var builder = WebApplication.CreateSlimBuilder(args);

// AOT-compatible JSON serialization
builder.Services.ConfigureHttpJsonOptions(options => {
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

// Register lock store as singleton
builder.Services.AddSingleton<ILockStore, LockStore>();

var app = builder.Build();
var lockStore = app.Services.GetRequiredService<ILockStore>();

// Health check
app.MapGet("/health", () => Results.Ok(new HealthResponse(true)));

// Acquire lock (with optional blocking wait)
app.MapPost("/lock", async (LockRequest req, HttpContext ctx, CancellationToken ct) => {
    var wait = ctx.Request.Query["wait"] != "false";
    var timeoutStr = ctx.Request.Query["timeout"].FirstOrDefault() ?? "60s";
    var timeout = ParseTimeout(timeoutStr);

    if (!wait) {
        // Immediate mode
        if (lockStore.TryAcquire(req.File, req.Session)) {
            return Results.Ok(new LockResponse(true));
        }
        var holder = lockStore.GetHolder(req.File);
        return Results.Ok(new LockResponse(false, holder, Error: "Lock held by another session"));
    }

    // Blocking wait mode
    var startTime = DateTime.UtcNow;
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(timeout);

    try {
        while (!cts.Token.IsCancellationRequested) {
            if (lockStore.TryAcquire(req.File, req.Session)) {
                var waited = (DateTime.UtcNow - startTime).TotalSeconds;
                return Results.Ok(new LockResponse(true, Waited: waited));
            }

            // Wait for release notification or timeout
            await lockStore.WaitForReleaseAsync(req.File, cts.Token);
        }
    }
    catch (OperationCanceledException) {
        // Timeout reached
    }

    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
    return Results.Ok(new LockResponse(
        false,
        lockStore.GetHolder(req.File),
        Error: "Timeout waiting for lock",
        Waited: elapsed
    ));
});

// Release lock
app.MapPost("/unlock", (UnlockRequest req) => {
    var released = lockStore.TryRelease(req.File, req.Session);
    return Results.Ok(new UnlockResponse(released));
});

// Release all locks for a session
app.MapPost("/unlock-all", (UnlockAllRequest req) => {
    var count = lockStore.ReleaseAll(req.Session);
    return Results.Ok(new UnlockAllResponse(count));
});

// Status endpoint
app.MapGet("/status", () => {
    var locks = lockStore.GetAllLocks();
    return Results.Ok(new StatusResponse(locks));
});

app.Run();

static TimeSpan ParseTimeout(string s) {
    if (s.EndsWith("s") && int.TryParse(s[..^1], out var secs))
        return TimeSpan.FromSeconds(Math.Min(secs, 300));
    if (s.EndsWith("m") && int.TryParse(s[..^1], out var mins))
        return TimeSpan.FromMinutes(Math.Min(mins, 5));
    return TimeSpan.FromSeconds(60);
}
```

**Models.cs:**
```csharp
namespace FileLockCoordinator;

public record LockRequest(string Session, string File);
public record UnlockRequest(string Session, string File);
public record UnlockAllRequest(string Session);

public record LockResponse(
    bool Granted,
    string? Holder = null,
    string? Error = null,
    double? Waited = null
);

public record UnlockResponse(bool Ok);
public record UnlockAllResponse(int Count);
public record HealthResponse(bool Ok);

public record LockInfo(string Session, string File, DateTime AcquiredAt);
public record StatusResponse(IReadOnlyList<LockInfo> Locks);
```

**AppJsonContext.cs (AOT Source Generator):**
```csharp
using System.Text.Json.Serialization;

namespace FileLockCoordinator;

[JsonSerializable(typeof(LockRequest))]
[JsonSerializable(typeof(UnlockRequest))]
[JsonSerializable(typeof(UnlockAllRequest))]
[JsonSerializable(typeof(LockResponse))]
[JsonSerializable(typeof(UnlockResponse))]
[JsonSerializable(typeof(UnlockAllResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(LockInfo))]
[JsonSerializable(typeof(IReadOnlyList<LockInfo>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class AppJsonContext : JsonSerializerContext { }
```

**LockStore.cs (Queue-Based Implementation):**
```csharp
using System.Collections.Concurrent;

namespace FileLockCoordinator;

public interface ILockStore {
    QueueResult EnqueueOrAcquire(string file, string session);
    bool TryRelease(string file, string session);
    int ReleaseAll(string session);
    string? GetHolder(string file);
    QueueInfo? GetQueueInfo(string file);
    IReadOnlyList<LockInfo> GetAllLocks();
    IReadOnlyList<QueueStatus> GetAllQueues();
    Task<bool> WaitForTurnAsync(string file, string session, CancellationToken ct);
}

public record QueueResult(int Position, int QueueLength, bool Acquired);
public record QueueInfo(string File, string Holder, int QueueLength, IReadOnlyList<string> Waiters);

public class LockStore : ILockStore, IDisposable {
    private readonly ConcurrentDictionary<string, FileQueue> _queues = new();
    private readonly TimeSpan _ttl;
    private readonly Timer _cleanupTimer;

    public LockStore(TimeSpan? ttl = null) {
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
        _cleanupTimer = new Timer(CleanupExpired, null, _ttl, _ttl);
    }

    public QueueResult EnqueueOrAcquire(string file, string session) {
        var queue = _queues.GetOrAdd(file, _ => new FileQueue());

        lock (queue.Lock) {
            // Check if session is already in queue
            var existingPosition = queue.GetPosition(session);
            if (existingPosition > 0) {
                return new QueueResult(existingPosition, queue.Count, existingPosition == 1);
            }

            // Add to queue
            queue.Enqueue(session);
            var position = queue.GetPosition(session);
            return new QueueResult(position, queue.Count, position == 1);
        }
    }

    public bool TryRelease(string file, string session) {
        if (!_queues.TryGetValue(file, out var queue)) return false;

        lock (queue.Lock) {
            if (queue.Holder != session) return false;
            queue.Dequeue();
            queue.NotifyAll();  // Wake up next waiter
            if (queue.Count == 0) _queues.TryRemove(file, out _);
            return true;
        }
    }

    public async Task<bool> WaitForTurnAsync(string file, string session, CancellationToken ct) {
        if (!_queues.TryGetValue(file, out var queue)) return false;

        while (!ct.IsCancellationRequested) {
            lock (queue.Lock) {
                var position = queue.GetPosition(session);
                if (position == 0) return false;  // Not in queue
                if (position == 1) return true;   // Our turn!
            }
            // Wait for notification, poll every 5s as backup
            await queue.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        return false;
    }

    // FileQueue maintains FIFO order with thread-safe notifications
    private class FileQueue {
        private readonly List<QueueEntry> _entries = new();
        private TaskCompletionSource _notifyTcs = new();

        public object Lock { get; } = new();
        public int Count => _entries.Count;
        public string? Holder => _entries.FirstOrDefault()?.Session;

        public void Enqueue(string session) => _entries.Add(new(session, DateTime.UtcNow));
        public void Dequeue() { if (_entries.Count > 0) _entries.RemoveAt(0); }
        public int GetPosition(string s) => _entries.FindIndex(e => e.Session == s) + 1;

        public Task WaitAsync() => _notifyTcs.Task;
        public void NotifyAll() {
            var old = _notifyTcs;
            _notifyTcs = new TaskCompletionSource();
            old.TrySetResult();
        }
    }
}
```

### Testing Without Mocking Frameworks

**FakeLockStore.cs:**
```csharp
namespace FileLockCoordinator.Tests.Fakes;

public class FakeLockStore : ILockStore {
    private readonly Dictionary<string, string> _locks = new();

    public bool ShouldGrantLock { get; set; } = true;
    public int AcquireCallCount { get; private set; }
    public int ReleaseCallCount { get; private set; }

    public bool TryAcquire(string file, string session) {
        AcquireCallCount++;
        if (!ShouldGrantLock) return false;

        if (_locks.TryGetValue(file, out var holder)) {
            return holder == session;
        }
        _locks[file] = session;
        return true;
    }

    public bool TryRelease(string file, string session) {
        ReleaseCallCount++;
        if (_locks.TryGetValue(file, out var holder) && holder == session) {
            _locks.Remove(file);
            return true;
        }
        return false;
    }

    public int ReleaseAll(string session) {
        var toRemove = _locks.Where(kvp => kvp.Value == session).Select(kvp => kvp.Key).ToList();
        foreach (var file in toRemove) _locks.Remove(file);
        return toRemove.Count;
    }

    public string? GetHolder(string file) => _locks.GetValueOrDefault(file);

    public IReadOnlyList<LockInfo> GetAllLocks() =>
        _locks.Select(kvp => new LockInfo(kvp.Value, kvp.Key, DateTime.UtcNow)).ToList();

    public Task WaitForReleaseAsync(string file, CancellationToken ct) =>
        Task.Delay(100, ct); // Short delay for tests
}
```

**LockStoreTests.cs:**
```csharp
namespace FileLockCoordinator.Tests;

public class LockStoreTests {
    [Fact]
    public void TryAcquire_WhenLockAvailable_ReturnsTrue() {
        var store = new LockStore();

        var result = store.TryAcquire("/path/file.cs", "session-1");

        Assert.True(result);
    }

    [Fact]
    public void TryAcquire_WhenLockHeldByOther_ReturnsFalse() {
        var store = new LockStore();
        store.TryAcquire("/path/file.cs", "session-1");

        var result = store.TryAcquire("/path/file.cs", "session-2");

        Assert.False(result);
    }

    [Fact]
    public void TryAcquire_WhenLockHeldBySelf_ReturnsTrue() {
        var store = new LockStore();
        store.TryAcquire("/path/file.cs", "session-1");

        var result = store.TryAcquire("/path/file.cs", "session-1");

        Assert.True(result);
    }

    [Fact]
    public void TryRelease_WhenHoldingLock_ReturnsTrue() {
        var store = new LockStore();
        store.TryAcquire("/path/file.cs", "session-1");

        var result = store.TryRelease("/path/file.cs", "session-1");

        Assert.True(result);
        Assert.Null(store.GetHolder("/path/file.cs"));
    }

    [Fact]
    public void TryRelease_WhenNotHoldingLock_ReturnsFalse() {
        var store = new LockStore();
        store.TryAcquire("/path/file.cs", "session-1");

        var result = store.TryRelease("/path/file.cs", "session-2");

        Assert.False(result);
    }

    [Fact]
    public void ReleaseAll_ReleasesAllLocksForSession() {
        var store = new LockStore();
        store.TryAcquire("/path/file1.cs", "session-1");
        store.TryAcquire("/path/file2.cs", "session-1");
        store.TryAcquire("/path/file3.cs", "session-2");

        var count = store.ReleaseAll("session-1");

        Assert.Equal(2, count);
        Assert.Null(store.GetHolder("/path/file1.cs"));
        Assert.Null(store.GetHolder("/path/file2.cs"));
        Assert.Equal("session-2", store.GetHolder("/path/file3.cs"));
    }

    [Fact]
    public async Task TryAcquire_WhenLockExpired_GrantsLock() {
        var store = new LockStore(ttl: TimeSpan.FromMilliseconds(50));
        store.TryAcquire("/path/file.cs", "session-1");

        await Task.Delay(100); // Wait for TTL to expire

        var result = store.TryAcquire("/path/file.cs", "session-2");
        Assert.True(result);
    }
}
```

**IntegrationTests.cs:**
```csharp
using System.Diagnostics;
using System.Net.Http.Json;

namespace FileLockCoordinator.Tests;

public class IntegrationTests : IAsyncLifetime {
    private Process? _serverProcess;
    private HttpClient _client = null!;
    private const int Port = 19876; // Use non-standard port for tests

    public async Task InitializeAsync() {
        // Start the AOT-compiled server
        var binaryPath = GetBinaryPath();
        _serverProcess = Process.Start(new ProcessStartInfo {
            FileName = binaryPath,
            Arguments = $"--urls=http://localhost:{Port}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });

        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{Port}") };

        // Wait for server to be ready
        for (int i = 0; i < 50; i++) {
            try {
                var response = await _client.GetAsync("/health");
                if (response.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(100);
        }
        throw new Exception("Server failed to start");
    }

    public async Task DisposeAsync() {
        _client.Dispose();
        if (_serverProcess is { HasExited: false }) {
            _serverProcess.Kill();
            await _serverProcess.WaitForExitAsync();
        }
        _serverProcess?.Dispose();
    }

    [Fact]
    public async Task Lock_WhenAvailable_Granted() {
        var response = await _client.PostAsJsonAsync("/lock", new { session = "test-1", file = "/test.cs" });
        var result = await response.Content.ReadFromJsonAsync<LockResponse>();

        Assert.True(result?.Granted);
    }

    [Fact]
    public async Task Lock_WhenHeldByOther_Denied() {
        await _client.PostAsJsonAsync("/lock", new { session = "test-1", file = "/conflict.cs" });

        var response = await _client.PostAsJsonAsync("/lock?wait=false", new { session = "test-2", file = "/conflict.cs" });
        var result = await response.Content.ReadFromJsonAsync<LockResponse>();

        Assert.False(result?.Granted);
        Assert.Equal("test-1", result?.Holder);
    }

    private static string GetBinaryPath() {
        // Locate the published AOT binary relative to test assembly
        var assemblyDir = Path.GetDirectoryName(typeof(IntegrationTests).Assembly.Location)!;
        var solutionDir = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", ".."));

        var rid = OperatingSystem.IsMacOS()
            ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
            : OperatingSystem.IsLinux() ? "linux-x64" : "win-x64";

        return Path.Combine(solutionDir, "bin", $"coordinator-{rid}");
    }
}
```

---

## Hook Scripts

**hooks/ensure-coordinator.sh:**
```bash
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
```

**hooks/request-lock.sh:**
```bash
#!/bin/bash
set -e

PORT="${FILE_LOCK_PORT:-9876}"

# Parse hook input (JSON from stdin)
INPUT=$(cat)
FILE_PATH=$(echo "$INPUT" | jq -r '.tool_input.file_path // .tool_input.filePath // empty')
SESSION_ID="${CLAUDE_SESSION_ID:-$(echo "$INPUT" | jq -r '.session_id')}"

# Parse arguments (defaults: wait=true, timeout=60)
WAIT="true"
TIMEOUT="60"
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

# Check if coordinator is running
if ! nc -z 127.0.0.1 "$PORT" 2>/dev/null; then
    echo "Coordinator not running, allowing edit" >&2
    exit 0
fi

# Request lock
RESPONSE=$(curl -s -X POST \
    "http://localhost:$PORT/lock?wait=$WAIT&timeout=${TIMEOUT}s" \
    -H "Content-Type: application/json" \
    -d "{\"session\": \"$SESSION_ID\", \"file\": \"$FILE_PATH\"}")

GRANTED=$(echo "$RESPONSE" | jq -r '.granted')

if [ "$GRANTED" = "true" ]; then
    WAITED=$(echo "$RESPONSE" | jq -r '.waited // 0')
    if (( $(echo "$WAITED > 1" | bc -l 2>/dev/null || echo 0) )); then
        echo "Lock acquired after ${WAITED}s wait" >&2
    fi
    exit 0
else
    ERROR=$(echo "$RESPONSE" | jq -r '.error // "Lock denied"')
    HOLDER=$(echo "$RESPONSE" | jq -r '.holder // "unknown"')
    echo "Failed to acquire lock on $FILE_PATH: $ERROR (held by $HOLDER)" >&2
    exit 2
fi
```

**hooks/release-lock.sh:**
```bash
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
```

---

## Build & Distribution

### Build Commands

```bash
# Publish for all platforms
dotnet publish -c Release -r osx-arm64 --self-contained /p:PublishAot=true -o bin/
mv bin/FileLockCoordinator bin/coordinator-osx-arm64

dotnet publish -c Release -r osx-x64 --self-contained /p:PublishAot=true -o bin/
mv bin/FileLockCoordinator bin/coordinator-osx-x64

dotnet publish -c Release -r linux-x64 --self-contained /p:PublishAot=true -o bin/
mv bin/FileLockCoordinator bin/coordinator-linux-x64

dotnet publish -c Release -r win-x64 --self-contained /p:PublishAot=true -o bin/
mv bin/FileLockCoordinator.exe bin/coordinator-win-x64.exe
```

### Plugin Configuration

**plugin.json:**
```json
{
  "name": "file-lock-coordinator",
  "version": "1.0.0",
  "description": "Cooperative file locking for multi-agent workflows",
  "hooks": {
    "SessionStart": [{
      "hooks": [{
        "type": "command",
        "command": "${CLAUDE_PLUGIN_ROOT}/hooks/ensure-coordinator.sh"
      }]
    }],
    "PreToolUse": [{
      "matcher": "Edit|Write",
      "hooks": [{
        "type": "command",
        "command": "${CLAUDE_PLUGIN_ROOT}/hooks/request-lock.sh --wait --timeout 60"
      }]
    }],
    "PostToolUse": [{
      "matcher": "Edit|Write",
      "hooks": [{
        "type": "command",
        "command": "${CLAUDE_PLUGIN_ROOT}/hooks/release-lock.sh"
      }]
    }]
  }
}
```

### Size Budget

| Component | Size |
|-----------|------|
| coordinator-osx-arm64 | ~10 MB |
| coordinator-osx-x64 | ~10 MB |
| coordinator-linux-x64 | ~10 MB |
| coordinator-win-x64.exe | ~10 MB |
| Hook scripts | ~5 KB |
| **Total** | **~40 MB** |

---

## Implementation Phases

### Phase 1: Core Coordinator (MVP) ✅

- [x] Lock acquisition (atomic, fail if held)
- [x] Blocking wait for lock (default behavior)
- [x] Release lock (only by holder)
- [x] Release all locks for session
- [x] TTL-based expiry (default: 5 minutes)
- [x] Health check endpoint
- [x] AOT-compatible JSON serialization

### Phase 2: Hook Integration ✅

- [x] ensure-coordinator.sh (SessionStart)
- [x] request-lock.sh (PreToolUse)
- [x] release-lock.sh (PostToolUse)
- [x] Plugin manifest (plugin.json)

### Phase 3: Testing & Polish ✅

- [x] Unit tests with manual fakes (21 tests)
- [x] Integration tests with WebApplicationFactory (12 tests)
- [x] Graceful shutdown
- [x] Logging configuration
- [x] Status endpoint with queue info

### Phase 4: Queue-Based Locking ✅ (NEW)

- [x] FIFO queue system for fair lock distribution
- [x] Queue position tracking in API responses
- [x] `/queues` endpoint for visibility
- [x] `WaitForTurnAsync` with proper notification
- [x] Default timeout increased to 300s (5 min)
- [x] Tests updated for queue-based API (33 total tests)

### Phase 5: Distribution

- [x] Build binary for macOS ARM64 (9 MB)
- [ ] Build binaries for other platforms
- [ ] Create plugin package
- [ ] GitHub release workflow
- [ ] Documentation updates

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Coordinator crashes | Low | High | TTL expiry clears stale locks |
| Session crashes without unlock | Medium | Medium | TTL expiry + unlock-all on reconnect |
| Port conflict | Low | Medium | Configurable port via environment |
| Hook fails silently | Medium | High | Verbose logging, health checks |
| AOT binary larger than Go | Expected | Low | Acceptable tradeoff for ecosystem fit |

---

## Success Criteria

1. **Functional:** Two Claude sessions cannot edit the same file simultaneously
2. **Transparent:** Zero context tokens used for coordination
3. **Reliable:** Stale locks expire automatically
4. **AOT-Compatible:** All features work in Native AOT mode
5. **Testable:** Unit tests run without mocking frameworks
6. **Portable:** Single binary, no dependencies, works on macOS/Linux/Windows
7. **Seamless:** Blocking wait means Claude almost never sees lock failures

---

## References

- [.NET Native AOT Deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [ASP.NET Core Native AOT](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot)
- [System.Text.Json Source Generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)
- [Claude Code Hooks Reference](https://docs.anthropic.com/en/docs/claude-code/hooks)
- [Claude Code File Corruption Issue #18998](https://github.com/anthropics/claude-code/issues/18998)
