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
    public void TryAcquire_DifferentFiles_BothSucceed() {
        var store = new LockStore();

        var result1 = store.TryAcquire("/path/file1.cs", "session-1");
        var result2 = store.TryAcquire("/path/file2.cs", "session-2");

        Assert.True(result1);
        Assert.True(result2);
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
    public void TryRelease_WhenNoLockExists_ReturnsFalse() {
        var store = new LockStore();

        var result = store.TryRelease("/path/file.cs", "session-1");

        Assert.False(result);
    }

    [Fact]
    public void TryRelease_AllowsOtherToAcquire() {
        var store = new LockStore();
        store.TryAcquire("/path/file.cs", "session-1");
        store.TryRelease("/path/file.cs", "session-1");

        var result = store.TryAcquire("/path/file.cs", "session-2");

        Assert.True(result);
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
    public void ReleaseAll_ReturnsZeroWhenNoLocksHeld() {
        var store = new LockStore();
        store.TryAcquire("/path/file.cs", "session-1");

        var count = store.ReleaseAll("session-2");

        Assert.Equal(0, count);
    }

    [Fact]
    public void GetHolder_ReturnsSessionWhenLocked() {
        var store = new LockStore();
        store.TryAcquire("/path/file.cs", "session-1");

        var holder = store.GetHolder("/path/file.cs");

        Assert.Equal("session-1", holder);
    }

    [Fact]
    public void GetHolder_ReturnsNullWhenNotLocked() {
        var store = new LockStore();

        var holder = store.GetHolder("/path/file.cs");

        Assert.Null(holder);
    }

    [Fact]
    public void GetAllLocks_ReturnsAllActiveLocks() {
        var store = new LockStore();
        store.TryAcquire("/path/file1.cs", "session-1");
        store.TryAcquire("/path/file2.cs", "session-2");

        var locks = store.GetAllLocks();

        Assert.Equal(2, locks.Count);
        Assert.Contains(locks, l => l.File == "/path/file1.cs" && l.Session == "session-1");
        Assert.Contains(locks, l => l.File == "/path/file2.cs" && l.Session == "session-2");
    }

    [Fact]
    public void GetAllLocks_ReturnsEmptyWhenNoLocks() {
        var store = new LockStore();

        var locks = store.GetAllLocks();

        Assert.Empty(locks);
    }

    [Fact]
    public async Task TryAcquire_WhenLockExpired_GrantsLock() {
        var store = new LockStore(ttl: TimeSpan.FromMilliseconds(50));
        store.TryAcquire("/path/file.cs", "session-1");

        await Task.Delay(100); // Wait for TTL to expire

        var result = store.TryAcquire("/path/file.cs", "session-2");
        Assert.True(result);
    }

    [Fact]
    public async Task WaitForReleaseAsync_ReturnsWhenLockReleased() {
        var store = new LockStore();
        store.TryAcquire("/path/file.cs", "session-1");

        var waitTask = Task.Run(async () => {
            await store.WaitForReleaseAsync("/path/file.cs", CancellationToken.None);
        });

        await Task.Delay(50);
        store.TryRelease("/path/file.cs", "session-1");

        // Should complete quickly after release
        await waitTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WaitForReleaseAsync_RespectsTimeout() {
        var store = new LockStore();
        store.TryAcquire("/path/file.cs", "session-1");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Should return without throwing after internal timeout
        await store.WaitForReleaseAsync("/path/file.cs", cts.Token);
    }
}
