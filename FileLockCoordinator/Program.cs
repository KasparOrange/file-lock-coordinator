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

// Acquire lock (with optional blocking wait)
app.MapPost("/lock", async (LockRequest req, HttpContext ctx, CancellationToken ct) => {
    var wait = ctx.Request.Query["wait"] != "false";
    var timeoutStr = ctx.Request.Query["timeout"].FirstOrDefault() ?? "60s";
    var timeout = ParseTimeout(timeoutStr);

    if (!wait) {
        // Immediate mode
        if (lockStore.TryAcquire(req.File, req.Session)) {
            logger.LogDebug("Lock granted immediately: {File} -> {Session}", req.File, req.Session);
            return Results.Ok(new LockResponse(true));
        }
        var holder = lockStore.GetHolder(req.File);
        logger.LogDebug("Lock denied (no-wait): {File} held by {Holder}, requested by {Session}", req.File, holder, req.Session);
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
                if (waited > 0.1) {
                    logger.LogInformation("Lock acquired after {Waited:F1}s wait: {File} -> {Session}", waited, req.File, req.Session);
                } else {
                    logger.LogDebug("Lock granted: {File} -> {Session}", req.File, req.Session);
                }
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
    logger.LogWarning("Lock timeout after {Elapsed:F1}s: {File} requested by {Session}, held by {Holder}",
        elapsed, req.File, req.Session, lockStore.GetHolder(req.File));
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

// Required for WebApplicationFactory in integration tests
public partial class Program { }
