using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FileLockCoordinator;

public interface ILockStore {
    bool TryAcquire(string file, string session);
    bool TryRelease(string file, string session);
    int ReleaseAll(string session);
    string? GetHolder(string file);
    IReadOnlyList<LockInfo> GetAllLocks();
    Task WaitForReleaseAsync(string file, CancellationToken ct);
}

public class LockStore : ILockStore, IDisposable {
    private readonly ConcurrentDictionary<string, LockEntry> _locks = new();
    private readonly ConcurrentDictionary<string, Channel<bool>> _waitChannels = new();
    private readonly TimeSpan _ttl;
    private readonly Timer _cleanupTimer;

    public LockStore(TimeSpan? ttl = null) {
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
        _cleanupTimer = new Timer(CleanupExpired, null, _ttl, _ttl);
    }

    public bool TryAcquire(string file, string session) {
        var now = DateTime.UtcNow;
        var newEntry = new LockEntry(session, now);

        // Try to add new lock
        if (_locks.TryAdd(file, newEntry)) {
            return true;
        }

        // Check if existing lock is ours or expired
        if (_locks.TryGetValue(file, out var existing)) {
            if (existing.Session == session) {
                return true; // Already own it
            }
            if (now - existing.AcquiredAt > _ttl) {
                // Expired, try to replace
                if (_locks.TryUpdate(file, newEntry, existing)) {
                    return true;
                }
            }
        }

        return false;
    }

    public bool TryRelease(string file, string session) {
        if (_locks.TryGetValue(file, out var entry) && entry.Session == session) {
            if (_locks.TryRemove(file, out _)) {
                NotifyWaiters(file);
                return true;
            }
        }
        return false;
    }

    public int ReleaseAll(string session) {
        var released = 0;
        foreach (var kvp in _locks) {
            if (kvp.Value.Session == session) {
                if (_locks.TryRemove(kvp.Key, out _)) {
                    released++;
                    NotifyWaiters(kvp.Key);
                }
            }
        }
        return released;
    }

    public string? GetHolder(string file) {
        return _locks.TryGetValue(file, out var entry) ? entry.Session : null;
    }

    public IReadOnlyList<LockInfo> GetAllLocks() {
        return _locks.Select(kvp => new LockInfo(kvp.Value.Session, kvp.Key, kvp.Value.AcquiredAt))
                     .ToList();
    }

    public async Task WaitForReleaseAsync(string file, CancellationToken ct) {
        var channel = _waitChannels.GetOrAdd(file, _ => Channel.CreateUnbounded<bool>());
        try {
            // Wait up to 1 second, then return to retry lock acquisition
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(1));
            await channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) {
            // Expected - either timeout or external cancellation
        }
    }

    private void NotifyWaiters(string file) {
        if (_waitChannels.TryGetValue(file, out var channel)) {
            // Non-blocking write to notify any waiters
            channel.Writer.TryWrite(true);
        }
    }

    private void CleanupExpired(object? state) {
        var now = DateTime.UtcNow;
        foreach (var kvp in _locks) {
            if (now - kvp.Value.AcquiredAt > _ttl) {
                if (_locks.TryRemove(kvp.Key, out _)) {
                    NotifyWaiters(kvp.Key);
                }
            }
        }
    }

    public void Dispose() {
        _cleanupTimer.Dispose();
    }

    private record LockEntry(string Session, DateTime AcquiredAt);
}
