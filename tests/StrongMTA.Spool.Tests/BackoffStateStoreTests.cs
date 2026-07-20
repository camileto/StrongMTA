using StrongMTA.Core;

namespace StrongMTA.Spool.Tests;

public class BackoffStateStoreTests : IDisposable
{
    private readonly SpoolTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private static QueueKey Key(string vmta = "vmta-01", string domain = "example.com") =>
        new() { DestinationDomain = domain, VirtualMtaName = vmta };

    [Fact]
    public async Task IsInBackoffAsync_NeverEntered_ReturnsFalse()
    {
        var store = new BackoffStateStore(_fixture.Paths);

        Assert.False(await store.IsInBackoffAsync(Key()));
    }

    [Fact]
    public async Task EnterBackoffAsync_WithoutAutoNormalAfter_StaysInBackoffIndefinitely()
    {
        var store = new BackoffStateStore(_fixture.Paths);

        await store.EnterBackoffAsync(Key(), autoNormalAfter: null);

        Assert.True(await store.IsInBackoffAsync(Key()));
    }

    [Fact]
    public async Task ExitBackoffAsync_AfterEnter_ReturnsToNormal()
    {
        var store = new BackoffStateStore(_fixture.Paths);

        await store.EnterBackoffAsync(Key(), autoNormalAfter: null);
        await store.ExitBackoffAsync(Key());

        Assert.False(await store.IsInBackoffAsync(Key()));
    }

    [Fact]
    public async Task IsInBackoffAsync_AutoNormalAfterElapsed_ExitsLazilyAndReturnsFalse()
    {
        var currentTime = DateTimeOffset.UtcNow;
        var store = new BackoffStateStore(_fixture.Paths, () => currentTime);

        await store.EnterBackoffAsync(Key(), autoNormalAfter: TimeSpan.FromMinutes(10));
        Assert.True(await store.IsInBackoffAsync(Key()));

        currentTime += TimeSpan.FromMinutes(11);
        Assert.False(await store.IsInBackoffAsync(Key()), "deveria ter saído do backoff automaticamente após BackoffToNormalAfter");
    }

    [Fact]
    public async Task DifferentQueueKeys_HaveIndependentBackoffState()
    {
        var store = new BackoffStateStore(_fixture.Paths);

        await store.EnterBackoffAsync(Key(domain: "a.com"), autoNormalAfter: null);

        Assert.True(await store.IsInBackoffAsync(Key(domain: "a.com")));
        Assert.False(await store.IsInBackoffAsync(Key(domain: "b.com")));
        Assert.False(await store.IsInBackoffAsync(Key(vmta: "vmta-02", domain: "a.com")));
    }

    [Fact]
    public async Task EnterBackoffAsync_PersistsAcrossNewStoreInstances_SimulatingRestart()
    {
        var fixedNow = DateTimeOffset.UtcNow;
        var store1 = new BackoffStateStore(_fixture.Paths, () => fixedNow);
        await store1.EnterBackoffAsync(Key(), autoNormalAfter: TimeSpan.FromHours(1));

        var store2 = new BackoffStateStore(_fixture.Paths, () => fixedNow);

        Assert.True(await store2.IsInBackoffAsync(Key()));
    }
}
