namespace FileLockCoordinator.Tests.Fakes;

public class FakeLockStore : ILockStore {
    private readonly Dictionary<string, List<string>> _queues = new();

    public bool ShouldGrantLock { get; set; } = true;
    public int AcquireCallCount { get; private set; }
    public int ReleaseCallCount { get; private set; }

    public QueueResult EnqueueOrAcquire(string file, string session) {
        AcquireCallCount++;

        if (!_queues.TryGetValue(file, out var queue)) {
            queue = new List<string>();
            _queues[file] = queue;
        }

        var existingPos = queue.IndexOf(session);
        if (existingPos >= 0) {
            return new QueueResult(existingPos + 1, queue.Count, existingPos == 0);
        }

        if (!ShouldGrantLock && queue.Count > 0) {
            queue.Add(session);
            return new QueueResult(queue.Count, queue.Count, false);
        }

        queue.Add(session);
        return new QueueResult(queue.Count, queue.Count, queue.Count == 1);
    }

    public bool TryRelease(string file, string session) {
        ReleaseCallCount++;
        if (_queues.TryGetValue(file, out var queue) && queue.Count > 0 && queue[0] == session) {
            queue.RemoveAt(0);
            if (queue.Count == 0) _queues.Remove(file);
            return true;
        }
        return false;
    }

    public int ReleaseAll(string session) {
        var released = 0;
        foreach (var kvp in _queues.ToList()) {
            if (kvp.Value.Count > 0 && kvp.Value[0] == session) {
                kvp.Value.RemoveAt(0);
                released++;
                if (kvp.Value.Count == 0) _queues.Remove(kvp.Key);
            }
        }
        return released;
    }

    public string? GetHolder(string file) =>
        _queues.TryGetValue(file, out var queue) && queue.Count > 0 ? queue[0] : null;

    public QueueInfo? GetQueueInfo(string file) {
        if (!_queues.TryGetValue(file, out var queue) || queue.Count == 0) return null;
        return new QueueInfo(file, queue[0], queue.Count, queue.Skip(1).ToList());
    }

    public IReadOnlyList<LockInfo> GetAllLocks() =>
        _queues.Where(kvp => kvp.Value.Count > 0)
               .Select(kvp => new LockInfo(kvp.Value[0], kvp.Key, DateTime.UtcNow))
               .ToList();

    public IReadOnlyList<QueueStatus> GetAllQueues() =>
        _queues.Where(kvp => kvp.Value.Count > 0)
               .Select(kvp => new QueueStatus(kvp.Key, kvp.Value[0], DateTime.UtcNow, kvp.Value.Count, kvp.Value.Skip(1).ToList()))
               .ToList();

    public Task<bool> WaitForTurnAsync(string file, string session, CancellationToken ct) {
        if (_queues.TryGetValue(file, out var queue) && queue.Count > 0 && queue[0] == session) {
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}
