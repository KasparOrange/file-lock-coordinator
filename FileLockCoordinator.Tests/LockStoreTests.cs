namespace FileLockCoordinator.Tests;

public class LockStoreTests {
    [Fact]
    public void EnqueueOrAcquire_WhenQueueEmpty_AcquiresImmediately() {
        var store = new LockStore();

        var result = store.EnqueueOrAcquire("/path/file.cs", "session-1");

        Assert.True(result.Acquired);
        Assert.Equal(1, result.Position);
        Assert.Equal(1, result.QueueLength);
    }

    [Fact]
    public void EnqueueOrAcquire_WhenQueueOccupied_JoinsQueue() {
        var store = new LockStore();
        store.EnqueueOrAcquire("/path/file.cs", "session-1");

        var result = store.EnqueueOrAcquire("/path/file.cs", "session-2");

        Assert.False(result.Acquired);
        Assert.Equal(2, result.Position);
        Assert.Equal(2, result.QueueLength);
    }

    [Fact]
    public void EnqueueOrAcquire_WhenAlreadyHolder_ReturnsAcquired() {
        var store = new LockStore();
        store.EnqueueOrAcquire("/path/file.cs", "session-1");

        var result = store.EnqueueOrAcquire("/path/file.cs", "session-1");

        Assert.True(result.Acquired);
        Assert.Equal(1, result.Position);
    }

    [Fact]
    public void EnqueueOrAcquire_WhenAlreadyInQueue_ReturnsSamePosition() {
        var store = new LockStore();
        store.EnqueueOrAcquire("/path/file.cs", "session-1");
        store.EnqueueOrAcquire("/path/file.cs", "session-2");

        var result = store.EnqueueOrAcquire("/path/file.cs", "session-2");

        Assert.False(result.Acquired);
        Assert.Equal(2, result.Position);
    }

    [Fact]
    public void EnqueueOrAcquire_DifferentFiles_BothAcquire() {
        var store = new LockStore();

        var result1 = store.EnqueueOrAcquire("/path/file1.cs", "session-1");
        var result2 = store.EnqueueOrAcquire("/path/file2.cs", "session-2");

        Assert.True(result1.Acquired);
        Assert.True(result2.Acquired);
    }

    [Fact]
    public void TryRelease_WhenHoldingLock_ReturnsTrue() {
        var store = new LockStore();
        store.EnqueueOrAcquire("/path/file.cs", "session-1");

        var result = store.TryRelease("/path/file.cs", "session-1");

        Assert.True(result);
        Assert.Null(store.GetHolder("/path/file.cs"));
    }

    [Fact]
    public void TryRelease_WhenNotHolder_ReturnsFalse() {
        var store = new LockStore();
        store.EnqueueOrAcquire("/path/file.cs", "session-1");
        store.EnqueueOrAcquire("/path/file.cs", "session-2"); // In queue but not holder

        var result = store.TryRelease("/path/file.cs", "session-2");

        Assert.False(result);
    }

    [Fact]
    public void TryRelease_WhenNoQueueExists_ReturnsFalse() {
        var store = new LockStore();

        var result = store.TryRelease("/path/file.cs", "session-1");

        Assert.False(result);
    }

    [Fact]
    public void TryRelease_PromotesNextInQueue() {
        var store = new LockStore();
        store.EnqueueOrAcquire("/path/file.cs", "session-1");
        store.EnqueueOrAcquire("/path/file.cs", "session-2");

        store.TryRelease("/path/file.cs", "session-1");

        Assert.Equal("session-2", store.GetHolder("/path/file.cs"));
    }

    [Fact]
    public void ReleaseAll_ReleasesAllLocksForSession() {
        var store = new LockStore();
        store.EnqueueOrAcquire("/path/file1.cs", "session-1");
        store.EnqueueOrAcquire("/path/file2.cs", "session-1");
        store.EnqueueOrAcquire("/path/file3.cs", "session-2");

        var count = store.ReleaseAll("session-1");

        Assert.Equal(2, count);
        Assert.Null(store.GetHolder("/path/file1.cs"));
        Assert.Null(store.GetHolder("/path/file2.cs"));
        Assert.Equal("session-2", store.GetHolder("/path/file3.cs"));
    }

    [Fact]
    public void ReleaseAll_ReturnsZeroWhenNoLocksHeld() {
        var store = new LockStore();
        store.EnqueueOrAcquire("/path/file.cs", "session-1");

        var count = store.ReleaseAll("session-2");

        Assert.Equal(0, count);
    }

    [Fact]
    public void GetHolder_ReturnsSessionWhenLocked() {
        var store = new LockStore();
        store.EnqueueOrAcquire("/path/file.cs", "session-1");

        var holder = store.GetHolder("/path/file.cs");

        Assert.Equal("session-1", holder);
    }

    [Fact]
    public void GetHolder_ReturnsNullWhenNoQueue() {
        var store = new LockStore();

        var holder = store.GetHolder("/path/file.cs");

        Assert.Null(holder);
    }

    [Fact]
    public void GetQueueInfo_ReturnsQueueDetails() {
        var store = new LockStore();
        store.EnqueueOrAcquire("/path/file.cs", "session-1");
        store.EnqueueOrAcquire("/path/file.cs", "session-2");
        store.EnqueueOrAcquire("/path/file.cs", "session-3");

        var info = store.GetQueueInfo("/path/file.cs");

        Assert.NotNull(info);
        Assert.Equal("session-1", info.Holder);
        Assert.Equal(3, info.QueueLength);
        Assert.Equal(2, info.Waiters.Count);
        Assert.Equal("session-2", info.Waiters[0]);
        Assert.Equal("session-3", info.Waiters[1]);
    }

    [Fact]
    public void GetAllLocks_ReturnsAllActiveHolders() {
        var store = new LockStore();
        store.EnqueueOrAcquire("/path/file1.cs", "session-1");
        store.EnqueueOrAcquire("/path/file2.cs", "session-2");

        var locks = store.GetAllLocks();

        Assert.Equal(2, locks.Count);
        Assert.Contains(locks, l => l.File == "/path/file1.cs" && l.Session == "session-1");
        Assert.Contains(locks, l => l.File == "/path/file2.cs" && l.Session == "session-2");
    }

    [Fact]
    public void GetAllLocks_ReturnsEmptyWhenNoQueues() {
        var store = new LockStore();

        var locks = store.GetAllLocks();

        Assert.Empty(locks);
    }

    [Fact]
    public void GetAllQueues_ReturnsAllQueuesWithWaiters() {
        var store = new LockStore();
        store.EnqueueOrAcquire("/path/file1.cs", "session-1");
        store.EnqueueOrAcquire("/path/file1.cs", "session-2");
        store.EnqueueOrAcquire("/path/file2.cs", "session-3");

        var queues = store.GetAllQueues();

        Assert.Equal(2, queues.Count);
        var q1 = queues.First(q => q.File == "/path/file1.cs");
        Assert.Equal("session-1", q1.Holder);
        Assert.Equal(2, q1.QueueLength);
        Assert.Single(q1.Waiters);
    }

    [Fact]
    public async Task EnqueueOrAcquire_WhenHolderExpired_EvictsAndGrants() {
        var store = new LockStore(ttl: TimeSpan.FromMilliseconds(50));
        store.EnqueueOrAcquire("/path/file.cs", "session-1");

        await Task.Delay(100); // Wait for TTL to expire

        var result = store.EnqueueOrAcquire("/path/file.cs", "session-2");
        Assert.True(result.Acquired);
        Assert.Equal("session-2", store.GetHolder("/path/file.cs"));
    }

    [Fact]
    public async Task WaitForTurnAsync_ReturnsWhenPromoted() {
        var store = new LockStore();
        store.EnqueueOrAcquire("/path/file.cs", "session-1");
        store.EnqueueOrAcquire("/path/file.cs", "session-2");

        var waitTask = Task.Run(async () => {
            return await store.WaitForTurnAsync("/path/file.cs", "session-2", CancellationToken.None);
        });

        await Task.Delay(50);
        store.TryRelease("/path/file.cs", "session-1");

        var result = await waitTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result);
    }

    [Fact]
    public async Task WaitForTurnAsync_RespectsTimeout() {
        var store = new LockStore();
        store.EnqueueOrAcquire("/path/file.cs", "session-1");
        store.EnqueueOrAcquire("/path/file.cs", "session-2");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await store.WaitForTurnAsync("/path/file.cs", "session-2", cts.Token);

        Assert.False(result); // Should timeout, not acquire
    }

    [Fact]
    public async Task WaitForTurnAsync_ReturnsFalseIfNotInQueue() {
        var store = new LockStore();

        var result = await store.WaitForTurnAsync("/path/file.cs", "session-1", CancellationToken.None);

        Assert.False(result);
    }
}
