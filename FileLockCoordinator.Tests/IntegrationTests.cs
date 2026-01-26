using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FileLockCoordinator.Tests;

public class IntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;

    public IntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<HealthResponse>(AppJsonContext.Default.HealthResponse);
        Assert.NotNull(result);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Lock_WhenAvailable_Granted()
    {
        var response = await _client.PostAsJsonAsync("/lock",
            new LockRequest("integration-test-1", "/test/available.cs"),
            AppJsonContext.Default.LockRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<LockResponse>(AppJsonContext.Default.LockResponse);
        Assert.NotNull(result);
        Assert.True(result.Granted);
    }

    [Fact]
    public async Task Lock_WhenHeldByOther_JoinsQueue()
    {
        // First session acquires lock
        await _client.PostAsJsonAsync("/lock",
            new LockRequest("session-holder", "/test/conflict.cs"),
            AppJsonContext.Default.LockRequest);

        // Second session tries to acquire (no-wait mode) - joins queue but doesn't wait
        var response = await _client.PostAsJsonAsync("/lock?wait=false",
            new LockRequest("session-requester", "/test/conflict.cs"),
            AppJsonContext.Default.LockRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<LockResponse>(AppJsonContext.Default.LockResponse);
        Assert.NotNull(result);
        Assert.False(result.Granted);
        Assert.Equal("session-holder", result.Holder);
        Assert.Equal(2, result.Position);
        Assert.Equal(2, result.QueueLength);
        Assert.Contains("Queued at position", result.Error);
    }

    [Fact]
    public async Task Lock_WhenHeldBySelf_Granted()
    {
        // Acquire lock
        await _client.PostAsJsonAsync("/lock",
            new LockRequest("session-self", "/test/self-reacquire.cs"),
            AppJsonContext.Default.LockRequest);

        // Re-acquire same lock
        var response = await _client.PostAsJsonAsync("/lock",
            new LockRequest("session-self", "/test/self-reacquire.cs"),
            AppJsonContext.Default.LockRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<LockResponse>(AppJsonContext.Default.LockResponse);
        Assert.NotNull(result);
        Assert.True(result.Granted);
    }

    [Fact]
    public async Task Lock_BlockingWait_AcquiresAfterRelease()
    {
        // First session acquires lock
        await _client.PostAsJsonAsync("/lock",
            new LockRequest("session-blocker", "/test/blocking.cs"),
            AppJsonContext.Default.LockRequest);

        // Start a task that will release the lock after a short delay
        var releaseTask = Task.Run(async () =>
        {
            await Task.Delay(200);
            await _client.PostAsJsonAsync("/unlock",
                new UnlockRequest("session-blocker", "/test/blocking.cs"),
                AppJsonContext.Default.UnlockRequest);
        });

        // Second session tries to acquire with blocking wait
        var response = await _client.PostAsJsonAsync("/lock?wait=true&timeout=5s",
            new LockRequest("session-waiter", "/test/blocking.cs"),
            AppJsonContext.Default.LockRequest);

        await releaseTask;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<LockResponse>(AppJsonContext.Default.LockResponse);
        Assert.NotNull(result);
        Assert.True(result.Granted);
        Assert.NotNull(result.Waited);
        Assert.True(result.Waited > 0.1, "Should have waited for lock");
    }

    [Fact]
    public async Task Lock_BlockingWait_TimeoutWhenNotReleased()
    {
        // First session acquires lock and doesn't release
        await _client.PostAsJsonAsync("/lock",
            new LockRequest("session-permanent", "/test/timeout.cs"),
            AppJsonContext.Default.LockRequest);

        // Second session tries to acquire with short timeout
        var response = await _client.PostAsJsonAsync("/lock?wait=true&timeout=1s",
            new LockRequest("session-timeout-test", "/test/timeout.cs"),
            AppJsonContext.Default.LockRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<LockResponse>(AppJsonContext.Default.LockResponse);
        Assert.NotNull(result);
        Assert.False(result.Granted);
        Assert.Contains("Timeout", result.Error);
        Assert.NotNull(result.Waited);
        Assert.True(result.Waited >= 0.9, "Should have waited approximately 1 second");
    }

    [Fact]
    public async Task Unlock_WhenHoldingLock_ReturnsOk()
    {
        // Acquire lock
        await _client.PostAsJsonAsync("/lock",
            new LockRequest("session-unlock", "/test/unlock.cs"),
            AppJsonContext.Default.LockRequest);

        // Release lock
        var response = await _client.PostAsJsonAsync("/unlock",
            new UnlockRequest("session-unlock", "/test/unlock.cs"),
            AppJsonContext.Default.UnlockRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<UnlockResponse>(AppJsonContext.Default.UnlockResponse);
        Assert.NotNull(result);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Unlock_WhenNotHoldingLock_ReturnsFalse()
    {
        var response = await _client.PostAsJsonAsync("/unlock",
            new UnlockRequest("nonexistent-session", "/test/not-held.cs"),
            AppJsonContext.Default.UnlockRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<UnlockResponse>(AppJsonContext.Default.UnlockResponse);
        Assert.NotNull(result);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task UnlockAll_ReleasesAllSessionLocks()
    {
        var session = "session-unlock-all";

        // Acquire multiple locks
        await _client.PostAsJsonAsync("/lock", new LockRequest(session, "/test/all1.cs"), AppJsonContext.Default.LockRequest);
        await _client.PostAsJsonAsync("/lock", new LockRequest(session, "/test/all2.cs"), AppJsonContext.Default.LockRequest);
        await _client.PostAsJsonAsync("/lock", new LockRequest(session, "/test/all3.cs"), AppJsonContext.Default.LockRequest);

        // Release all
        var response = await _client.PostAsJsonAsync("/unlock-all",
            new UnlockAllRequest(session),
            AppJsonContext.Default.UnlockAllRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<UnlockAllResponse>(AppJsonContext.Default.UnlockAllResponse);
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task Status_ReturnsCurrentLocks()
    {
        // Use unique file paths for this test
        var session = "session-status-test";
        var file1 = $"/test/status-{Guid.NewGuid()}.cs";
        var file2 = $"/test/status-{Guid.NewGuid()}.cs";

        // Acquire some locks
        await _client.PostAsJsonAsync("/lock", new LockRequest(session, file1), AppJsonContext.Default.LockRequest);
        await _client.PostAsJsonAsync("/lock", new LockRequest(session, file2), AppJsonContext.Default.LockRequest);

        // Get status
        var response = await _client.GetAsync("/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<StatusResponse>(AppJsonContext.Default.StatusResponse);
        Assert.NotNull(result);
        Assert.NotNull(result.Locks);

        // Our locks should be in the list
        Assert.Contains(result.Locks, l => l.File == file1 && l.Session == session);
        Assert.Contains(result.Locks, l => l.File == file2 && l.Session == session);
    }

    [Fact]
    public async Task Queues_ReturnsAllQueuesWithWaiters()
    {
        var file = $"/test/queues-{Guid.NewGuid()}.cs";

        // Build a queue with 3 sessions
        await _client.PostAsJsonAsync("/lock", new LockRequest("holder", file), AppJsonContext.Default.LockRequest);
        await _client.PostAsJsonAsync("/lock?wait=false", new LockRequest("waiter-1", file), AppJsonContext.Default.LockRequest);
        await _client.PostAsJsonAsync("/lock?wait=false", new LockRequest("waiter-2", file), AppJsonContext.Default.LockRequest);

        // Get queues
        var response = await _client.GetAsync("/queues");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<QueuesResponse>(AppJsonContext.Default.QueuesResponse);
        Assert.NotNull(result);

        var queue = result.Queues.FirstOrDefault(q => q.File == file);
        Assert.NotNull(queue);
        Assert.Equal("holder", queue.Holder);
        Assert.Equal(3, queue.QueueLength);
        Assert.Equal(2, queue.Waiters.Count);
        Assert.Contains("waiter-1", queue.Waiters);
        Assert.Contains("waiter-2", queue.Waiters);
    }

    [Fact]
    public async Task Lock_ReturnsQueuePosition()
    {
        var file = $"/test/position-{Guid.NewGuid()}.cs";

        // First acquires
        var r1 = await _client.PostAsJsonAsync("/lock", new LockRequest("s1", file), AppJsonContext.Default.LockRequest);
        var result1 = await r1.Content.ReadFromJsonAsync<LockResponse>(AppJsonContext.Default.LockResponse);
        Assert.True(result1!.Granted);
        Assert.Equal(1, result1.Position);

        // Second joins queue
        var r2 = await _client.PostAsJsonAsync("/lock?wait=false", new LockRequest("s2", file), AppJsonContext.Default.LockRequest);
        var result2 = await r2.Content.ReadFromJsonAsync<LockResponse>(AppJsonContext.Default.LockResponse);
        Assert.False(result2!.Granted);
        Assert.Equal(2, result2.Position);
        Assert.Equal(2, result2.QueueLength);

        // Third joins queue
        var r3 = await _client.PostAsJsonAsync("/lock?wait=false", new LockRequest("s3", file), AppJsonContext.Default.LockRequest);
        var result3 = await r3.Content.ReadFromJsonAsync<LockResponse>(AppJsonContext.Default.LockResponse);
        Assert.False(result3!.Granted);
        Assert.Equal(3, result3.Position);
        Assert.Equal(3, result3.QueueLength);
    }
}
