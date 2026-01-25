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
