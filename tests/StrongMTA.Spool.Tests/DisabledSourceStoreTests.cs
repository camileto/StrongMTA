namespace StrongMTA.Spool.Tests;

public class DisabledSourceStoreTests : IDisposable
{
    private readonly SpoolTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task IsDisabledAsync_NeverDisabled_ReturnsFalse()
    {
        var store = new DisabledSourceStore(_fixture.Paths);

        Assert.False(await store.IsDisabledAsync("vmta-01"));
    }

    [Fact]
    public async Task DisableAsync_WithoutReenableAfter_StaysDisabledIndefinitely()
    {
        var store = new DisabledSourceStore(_fixture.Paths);

        await store.DisableAsync("vmta-01", reenableAfter: null);

        Assert.True(await store.IsDisabledAsync("vmta-01"));
    }

    [Fact]
    public async Task IsDisabledAsync_ReenableAfterElapsed_ReenablesLazilyAndReturnsFalse()
    {
        var currentTime = DateTimeOffset.UtcNow;
        var store = new DisabledSourceStore(_fixture.Paths, () => currentTime);

        await store.DisableAsync("vmta-01", reenableAfter: TimeSpan.FromMinutes(30));
        Assert.True(await store.IsDisabledAsync("vmta-01"));

        currentTime += TimeSpan.FromMinutes(31);
        Assert.False(await store.IsDisabledAsync("vmta-01"), "deveria ter reabilitado automaticamente após ReenableAfter");
    }

    [Fact]
    public async Task DifferentVirtualMtas_HaveIndependentDisabledState()
    {
        var store = new DisabledSourceStore(_fixture.Paths);

        await store.DisableAsync("vmta-01", reenableAfter: null);

        Assert.True(await store.IsDisabledAsync("vmta-01"));
        Assert.False(await store.IsDisabledAsync("vmta-02"));
    }

    [Fact]
    public async Task DisableAsync_PersistsAcrossNewStoreInstances_SimulatingRestart()
    {
        var fixedNow = DateTimeOffset.UtcNow;
        var store1 = new DisabledSourceStore(_fixture.Paths, () => fixedNow);
        await store1.DisableAsync("vmta-01", reenableAfter: TimeSpan.FromHours(2));

        var store2 = new DisabledSourceStore(_fixture.Paths, () => fixedNow);

        Assert.True(await store2.IsDisabledAsync("vmta-01"));
    }
}
