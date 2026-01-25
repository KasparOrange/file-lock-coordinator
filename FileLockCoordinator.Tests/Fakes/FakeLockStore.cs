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
