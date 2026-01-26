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
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FileLockCoordinator");
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

// Graceful shutdown
lifetime.ApplicationStarted.Register(() => {
    logger.LogInformation("File Lock Coordinator started on {Urls}", string.Join(", ", app.Urls));
});

lifetime.ApplicationStopping.Register(() => {
    logger.LogInformation("File Lock Coordinator shutting down...");
    if (lockStore is IDisposable disposable) {
        disposable.Dispose();
    }
    logger.LogInformation("File Lock Coordinator shutdown complete");
});

// Health check
app.MapGet("/health", () => Results.Ok(new HealthResponse(true)));

// Acquire lock (with queue-based waiting)
app.MapPost("/lock", async (LockRequest req, HttpContext ctx, CancellationToken ct) => {
    var wait = ctx.Request.Query["wait"] != "false";
    var timeoutStr = ctx.Request.Query["timeout"].FirstOrDefault() ?? "300s";
    var timeout = ParseTimeout(timeoutStr);

    var startTime = DateTime.UtcNow;

    // Join queue (or acquire immediately if queue is empty)
    var result = lockStore.EnqueueOrAcquire(req.File, req.Session);

    if (result.Acquired) {
        logger.LogDebug("Lock granted immediately: {File} -> {Session}", req.File, req.Session);
        return Results.Ok(new LockResponse(true, Position: 1, QueueLength: result.QueueLength));
    }

    if (!wait) {
        // Non-blocking mode: joined queue but don't wait
        logger.LogDebug("Lock queued (no-wait): {File} position {Position}/{QueueLength} for {Session}",
            req.File, result.Position, result.QueueLength, req.Session);
        return Results.Ok(new LockResponse(
            false,
            lockStore.GetHolder(req.File),
            Error: $"Queued at position {result.Position}",
            Position: result.Position,
            QueueLength: result.QueueLength
        ));
    }

    // Wait for our turn in queue
    logger.LogInformation("Session {Session} waiting in queue for {File} at position {Position}/{QueueLength}",
        req.Session, req.File, result.Position, result.QueueLength);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(timeout);

    try {
        var acquired = await lockStore.WaitForTurnAsync(req.File, req.Session, cts.Token);

        if (acquired) {
            var waited = (DateTime.UtcNow - startTime).TotalSeconds;
            var queueInfo = lockStore.GetQueueInfo(req.File);
            logger.LogInformation("Lock acquired after {Waited:F1}s queue wait: {File} -> {Session}",
                waited, req.File, req.Session);
            return Results.Ok(new LockResponse(
                true,
                Waited: waited,
                Position: 1,
                QueueLength: queueInfo?.QueueLength ?? 1
            ));
        }
    }
    catch (OperationCanceledException) {
        // Timeout reached
    }

    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
    var currentQueue = lockStore.GetQueueInfo(req.File);
    var currentPosition = currentQueue?.Waiters.ToList().IndexOf(req.Session) + 2 ?? 0;

    logger.LogWarning("Lock timeout after {Elapsed:F1}s: {File} requested by {Session} at position {Position}",
        elapsed, req.File, req.Session, currentPosition);

    return Results.Ok(new LockResponse(
        false,
        lockStore.GetHolder(req.File),
        Error: $"Timeout waiting in queue at position {currentPosition}",
        Waited: elapsed,
        Position: currentPosition,
        QueueLength: currentQueue?.QueueLength ?? 0
    ));
});

// Release lock
app.MapPost("/unlock", (UnlockRequest req) => {
    var released = lockStore.TryRelease(req.File, req.Session);
    if (released) {
        logger.LogDebug("Lock released: {File} by {Session}", req.File, req.Session);
    }
    return Results.Ok(new UnlockResponse(released));
});

// Release all locks for a session
app.MapPost("/unlock-all", (UnlockAllRequest req) => {
    var count = lockStore.ReleaseAll(req.Session);
    if (count > 0) {
        logger.LogInformation("Released {Count} locks for session {Session}", count, req.Session);
    }
    return Results.Ok(new UnlockAllResponse(count));
});

// Status endpoint (legacy)
app.MapGet("/status", () => {
    var locks = lockStore.GetAllLocks();
    return Results.Ok(new StatusResponse(locks));
});

// Locks endpoint - shows current lock table
app.MapGet("/locks", () => {
    var locks = lockStore.GetAllLocks();
    return Results.Ok(new LocksResponse(locks.Count, locks));
});

// Queues endpoint - shows all queues with waiters
app.MapGet("/queues", () => {
    var queues = lockStore.GetAllQueues();
    var dtos = queues.Select(q => new QueueStatusDto(
        q.File, q.Holder, q.AcquiredAt, q.QueueLength, q.Waiters
    )).ToList();
    return Results.Ok(new QueuesResponse(dtos.Count, dtos));
});

// Queue info for a specific file
app.MapGet("/queue/{*file}", (string file) => {
    var info = lockStore.GetQueueInfo("/" + file);
    if (info == null) {
        return Results.Ok(new { exists = false, file = "/" + file });
    }
    return Results.Ok(new QueueResponse(info.File, info.Holder, info.QueueLength, info.Waiters));
});

app.Run();

static TimeSpan ParseTimeout(string s) {
    if (s.EndsWith("s") && int.TryParse(s[..^1], out var secs))
        return TimeSpan.FromSeconds(Math.Min(secs, 300));
    if (s.EndsWith("m") && int.TryParse(s[..^1], out var mins))
        return TimeSpan.FromMinutes(Math.Min(mins, 5));
    return TimeSpan.FromSeconds(60);
}

// Required for WebApplicationFactory in integration tests
public partial class Program { }
