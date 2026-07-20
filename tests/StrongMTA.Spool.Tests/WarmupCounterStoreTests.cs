namespace StrongMTA.Spool.Tests;

public class WarmupCounterStoreTests : IDisposable
{
    private readonly SpoolTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task TryReserveColdSlotAsync_UnderLimit_ReservesAndReturnsTrue()
    {
        var store = new WarmupCounterStore(_fixture.Paths);

        var reserved1 = await store.TryReserveColdSlotAsync("vmta-quente", "example.com", dailyLimit: 3);
        var reserved2 = await store.TryReserveColdSlotAsync("vmta-quente", "example.com", dailyLimit: 3);

        Assert.True(reserved1);
        Assert.True(reserved2);
        Assert.Equal(2, await store.GetCountAsync("vmta-quente", "example.com"));
    }

    [Fact]
    public async Task TryReserveColdSlotAsync_AtLimit_StopsReservingAndReturnsFalse()
    {
        var store = new WarmupCounterStore(_fixture.Paths);

        for (var i = 0; i < 3; i++)
            Assert.True(await store.TryReserveColdSlotAsync("vmta-quente", "example.com", dailyLimit: 3));

        var fourth = await store.TryReserveColdSlotAsync("vmta-quente", "example.com", dailyLimit: 3);

        Assert.False(fourth);
        Assert.Equal(3, await store.GetCountAsync("vmta-quente", "example.com")); // não incrementou além do limite
    }

    [Fact]
    public async Task TryReserveColdSlotAsync_DifferentDomains_HaveIndependentCounters()
    {
        var store = new WarmupCounterStore(_fixture.Paths);

        await store.TryReserveColdSlotAsync("vmta-quente", "a.com", dailyLimit: 1);
        await store.TryReserveColdSlotAsync("vmta-quente", "a.com", dailyLimit: 1); // já no limite para a.com

        var bDotCom = await store.TryReserveColdSlotAsync("vmta-quente", "b.com", dailyLimit: 1);

        Assert.True(bDotCom, "domínios diferentes devem ter contadores independentes");
        Assert.Equal(1, await store.GetCountAsync("vmta-quente", "a.com"));
        Assert.Equal(1, await store.GetCountAsync("vmta-quente", "b.com"));
    }

    [Fact]
    public async Task TryReserveColdSlotAsync_DomainIsCaseInsensitive()
    {
        var store = new WarmupCounterStore(_fixture.Paths);

        await store.TryReserveColdSlotAsync("vmta-quente", "Example.COM", dailyLimit: 5);

        Assert.Equal(1, await store.GetCountAsync("vmta-quente", "example.com"));
    }

    [Fact]
    public async Task TryReserveColdSlotAsync_NewDay_ResetsCounterAutomatically()
    {
        var currentDay = new DateTimeOffset(2026, 6, 25, 10, 0, 0, TimeSpan.Zero);
        var store = new WarmupCounterStore(_fixture.Paths, () => currentDay);

        await store.TryReserveColdSlotAsync("vmta-quente", "example.com", dailyLimit: 1);
        Assert.Equal(1, await store.GetCountAsync("vmta-quente", "example.com"));

        currentDay = currentDay.AddDays(1); // "virada do dia" - mesmo processo, sem restart
        var afterMidnight = new DateTimeOffset(2026, 6, 26, 0, 5, 0, TimeSpan.Zero);
        store = new WarmupCounterStore(_fixture.Paths, () => afterMidnight);

        var reserved = await store.TryReserveColdSlotAsync("vmta-quente", "example.com", dailyLimit: 1);

        Assert.True(reserved, "contador deveria ter resetado no novo dia, liberando a vaga novamente");
        Assert.Equal(1, await store.GetCountAsync("vmta-quente", "example.com"));
    }

    [Fact]
    public async Task TryReserveColdSlotAsync_PersistsAcrossNewStoreInstances_SimulatingRestart()
    {
        var fixedNow = DateTimeOffset.UtcNow;
        var store1 = new WarmupCounterStore(_fixture.Paths, () => fixedNow);
        await store1.TryReserveColdSlotAsync("vmta-quente", "example.com", dailyLimit: 10);
        await store1.TryReserveColdSlotAsync("vmta-quente", "example.com", dailyLimit: 10);

        // "reinicia o processo": nova instância do store sobre o mesmo spool em disco
        var store2 = new WarmupCounterStore(_fixture.Paths, () => fixedNow);
        var count = await store2.GetCountAsync("vmta-quente", "example.com");

        Assert.Equal(2, count);
    }
}
