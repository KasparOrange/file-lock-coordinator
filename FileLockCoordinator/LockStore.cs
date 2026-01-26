using System.Collections.Concurrent;

namespace FileLockCoordinator;

public interface ILockStore {
    /// <summary>
    /// Join the queue for a file. Returns queue position (1 = you have the lock).
    /// </summary>
    QueueResult EnqueueOrAcquire(string file, string session);

    /// <summary>
    /// Release the lock (only works if you're at front of queue).
    /// </summary>
    bool TryRelease(string file, string session);

    /// <summary>
    /// Release all locks held by a session.
    /// </summary>
    int ReleaseAll(string session);

    /// <summary>
    /// Get current holder of a file (front of queue).
    /// </summary>
    string? GetHolder(string file);

    /// <summary>
    /// Get queue info for a file.
    /// </summary>
    QueueInfo? GetQueueInfo(string file);

    /// <summary>
    /// Get all current locks (front of each queue).
    /// </summary>
    IReadOnlyList<LockInfo> GetAllLocks();

    /// <summary>
    /// Get all queues with their waiters.
    /// </summary>
    IReadOnlyList<QueueStatus> GetAllQueues();

    /// <summary>
    /// Wait until session reaches front of queue or timeout.
    /// </summary>
    Task<bool> WaitForTurnAsync(string file, string session, CancellationToken ct);
}

public record QueueResult(int Position, int QueueLength, bool Acquired);
public record QueueInfo(string File, string Holder, int QueueLength, IReadOnlyList<string> Waiters);
public record QueueStatus(string File, string Holder, DateTime AcquiredAt, int QueueLength, IReadOnlyList<string> Waiters);

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
                // Already in queue, return current position
                return new QueueResult(existingPosition, queue.Count, existingPosition == 1);
            }

            // Check if front of queue is expired and should be evicted
            if (queue.Count > 0 && DateTime.UtcNow - queue.AcquiredAt > _ttl) {
                queue.Dequeue();
                queue.NotifyAll();
            }

            // Add to queue
            queue.Enqueue(session);
            var position = queue.GetPosition(session);
            return new QueueResult(position, queue.Count, position == 1);
        }
    }

    public bool TryRelease(string file, string session) {
        if (!_queues.TryGetValue(file, out var queue)) {
            return false;
        }

        lock (queue.Lock) {
            // Only the holder (front of queue) can release
            if (queue.Holder != session) {
                return false;
            }

            queue.Dequeue();
            queue.NotifyAll();

            // Clean up empty queues
            if (queue.Count == 0) {
                _queues.TryRemove(file, out _);
            }

            return true;
        }
    }

    public int ReleaseAll(string session) {
        var released = 0;

        foreach (var kvp in _queues) {
            var queue = kvp.Value;
            lock (queue.Lock) {
                // If session is holder, release
                if (queue.Holder == session) {
                    queue.Dequeue();
                    queue.NotifyAll();
                    released++;

                    if (queue.Count == 0) {
                        _queues.TryRemove(kvp.Key, out _);
                    }
                }
                // Also remove from waiting positions
                else if (queue.Remove(session)) {
                    // Removed from queue but wasn't holding, don't count as "released"
                }
            }
        }

        return released;
    }

    public string? GetHolder(string file) {
        if (_queues.TryGetValue(file, out var queue)) {
            lock (queue.Lock) {
                return queue.Holder;
            }
        }
        return null;
    }

    public QueueInfo? GetQueueInfo(string file) {
        if (_queues.TryGetValue(file, out var queue)) {
            lock (queue.Lock) {
                if (queue.Count == 0) return null;
                return new QueueInfo(file, queue.Holder!, queue.Count, queue.GetWaiters());
            }
        }
        return null;
    }

    public IReadOnlyList<LockInfo> GetAllLocks() {
        var result = new List<LockInfo>();
        foreach (var kvp in _queues) {
            lock (kvp.Value.Lock) {
                if (kvp.Value.Holder != null) {
                    result.Add(new LockInfo(kvp.Value.Holder, kvp.Key, kvp.Value.AcquiredAt));
                }
            }
        }
        return result;
    }

    public IReadOnlyList<QueueStatus> GetAllQueues() {
        var result = new List<QueueStatus>();
        foreach (var kvp in _queues) {
            lock (kvp.Value.Lock) {
                if (kvp.Value.Count > 0) {
                    result.Add(new QueueStatus(
                        kvp.Key,
                        kvp.Value.Holder!,
                        kvp.Value.AcquiredAt,
                        kvp.Value.Count,
                        kvp.Value.GetWaiters()
                    ));
                }
            }
        }
        return result;
    }

    public async Task<bool> WaitForTurnAsync(string file, string session, CancellationToken ct) {
        if (!_queues.TryGetValue(file, out var queue)) {
            return false; // Not in any queue
        }

        try {
            while (!ct.IsCancellationRequested) {
                Task waitTask;

                lock (queue.Lock) {
                    var position = queue.GetPosition(session);
                    if (position == 0) {
                        return false; // Not in queue anymore
                    }
                    if (position == 1) {
                        return true; // It's our turn!
                    }

                    // Get the wait task while holding lock
                    waitTask = queue.WaitAsync();
                }

                // Wait for notification (outside lock)
                try {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(5)); // Poll every 5s as backup
                    await waitTask.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                    // Internal timeout - continue loop to check position
                }
            }
        }
        catch (OperationCanceledException) {
            // External cancellation
        }

        return false; // Cancelled or not in queue
    }

    private void CleanupExpired(object? state) {
        var now = DateTime.UtcNow;
        foreach (var kvp in _queues) {
            lock (kvp.Value.Lock) {
                // Evict expired holder
                if (kvp.Value.Count > 0 && now - kvp.Value.AcquiredAt > _ttl) {
                    kvp.Value.Dequeue();
                    kvp.Value.NotifyAll();
                }

                // Clean up empty queues
                if (kvp.Value.Count == 0) {
                    _queues.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    public void Dispose() {
        _cleanupTimer.Dispose();
    }

    private class FileQueue {
        private readonly List<QueueEntry> _entries = new();
        private TaskCompletionSource _notifyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public object Lock { get; } = new();
        public int Count => _entries.Count;
        public string? Holder => _entries.Count > 0 ? _entries[0].Session : null;
        public DateTime AcquiredAt => _entries.Count > 0 ? _entries[0].EnqueuedAt : DateTime.MinValue;

        public void Enqueue(string session) {
            _entries.Add(new QueueEntry(session, DateTime.UtcNow));
        }

        public void Dequeue() {
            if (_entries.Count > 0) {
                _entries.RemoveAt(0);
                // Update acquired time for new holder
                if (_entries.Count > 0) {
                    _entries[0] = _entries[0] with { EnqueuedAt = DateTime.UtcNow };
                }
            }
        }

        public bool Remove(string session) {
            var index = _entries.FindIndex(e => e.Session == session);
            if (index > 0) { // Don't remove holder this way
                _entries.RemoveAt(index);
                return true;
            }
            return false;
        }

        public int GetPosition(string session) {
            var index = _entries.FindIndex(e => e.Session == session);
            return index >= 0 ? index + 1 : 0; // 1-indexed, 0 = not in queue
        }

        public IReadOnlyList<string> GetWaiters() {
            return _entries.Skip(1).Select(e => e.Session).ToList();
        }

        public Task WaitAsync() {
            return _notifyTcs.Task;
        }

        public void NotifyAll() {
            var oldTcs = _notifyTcs;
            _notifyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            oldTcs.TrySetResult();
        }

        private record QueueEntry(string Session, DateTime EnqueuedAt);
    }
}
