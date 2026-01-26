namespace FileLockCoordinator;

public record LockRequest(string Session, string File);
public record UnlockRequest(string Session, string File);
public record UnlockAllRequest(string Session);

public record LockResponse(
    bool Granted,
    string? Holder = null,
    string? Error = null,
    double? Waited = null,
    int? Position = null,
    int? QueueLength = null
);

public record UnlockResponse(bool Ok);
public record UnlockAllResponse(int Count);
public record HealthResponse(bool Ok);

public record LockInfo(string Session, string File, DateTime AcquiredAt);
public record StatusResponse(IReadOnlyList<LockInfo> Locks);
public record LocksResponse(int Count, IReadOnlyList<LockInfo> Locks);

// Queue-specific responses
public record QueueResponse(
    string File,
    string Holder,
    int QueueLength,
    IReadOnlyList<string> Waiters
);

public record QueuesResponse(
    int Count,
    IReadOnlyList<QueueStatusDto> Queues
);

public record QueueStatusDto(
    string File,
    string Holder,
    DateTime AcquiredAt,
    int QueueLength,
    IReadOnlyList<string> Waiters
);
