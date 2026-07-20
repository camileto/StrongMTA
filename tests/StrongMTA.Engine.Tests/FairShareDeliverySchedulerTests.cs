using StrongMTA.Core;

namespace StrongMTA.Engine.Tests;

/// <summary>
/// Testes do scheduler em isolamento, sem rede: usa um delegate de entrega instrumentado
/// (<see cref="GatedDelegate"/>) que fica "em voo" até o teste liberar explicitamente,
/// permitindo observar e controlar concorrência de forma determinística.
/// </summary>
public class FairShareDeliverySchedulerTests
{
    private static RecipientWorkItem CreateItem(string domain, string vmta = "vmta-01") => new()
    {
        MessageId = Guid.NewGuid(),
        RecipientId = Guid.NewGuid(),
        MsgFilePath = "x.msg",
        StateFilePath = "x.state",
        EnvelopeFrom = "bounce@strongmta.test",
        RecipientAddress = $"a@{domain}",
        DestinationDomain = domain,
        VirtualMtaName = vmta,
        SubmittedAt = DateTimeOffset.UtcNow,
        NextAttemptAt = DateTimeOffset.UtcNow
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "condição não satisfeita dentro do timeout");
    }

    /// <summary>Delegate de entrega de teste: cada chamada fica bloqueada em <see cref="ReleaseGate"/> até o teste liberar, permitindo observar o pico real de concorrência.</summary>
    private sealed class GatedDelegate
    {
        private int _active;
        private int _maxActive;
        private int _completed;

        public SemaphoreSlim ReleaseGate { get; } = new(0);
        public int Active => Volatile.Read(ref _active);
        public int MaxActive => Volatile.Read(ref _maxActive);
        public int Completed => Volatile.Read(ref _completed);

        public async Task InvokeAsync(RecipientWorkItem item, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref _active);
            InterlockedMax(ref _maxActive, current);
            await ReleaseGate.WaitAsync(ct).ConfigureAwait(false);
            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _completed);
        }

        private static void InterlockedMax(ref int target, int candidate)
        {
            var current = target;
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref target, candidate, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }

    [Fact]
    public async Task Enqueue_RespectsGlobalConcurrencyCap_AcrossManyKeys()
    {
        var gated = new GatedDelegate();
        var domainConfigProvider = new StaticDomainConfigProvider(new DomainConfig
        {
            DomainName = "*",
            RetryIntervals = [TimeSpan.FromMinutes(1)],
            BounceAfter = TimeSpan.FromHours(1),
            MaxConcurrentConnections = 10 // bem acima do teto global, pra não interferir nesta asserção
        });
        var scheduler = new FairShareDeliveryScheduler(domainConfigProvider, new SchedulerOptions { GlobalMaxConcurrency = 3 }, gated.InvokeAsync);

        const int totalItems = 10;
        for (var i = 0; i < totalItems; i++)
            scheduler.Enqueue(CreateItem($"domain{i % 5}.test"));

        await WaitUntilAsync(() => gated.Active == 3, TimeSpan.FromSeconds(2));
        await Task.Delay(100); // folga curta pra garantir que não suba além do teto
        Assert.Equal(3, gated.Active);

        gated.ReleaseGate.Release(totalItems);
        await WaitUntilAsync(() => gated.Completed == totalItems, TimeSpan.FromSeconds(3));
        Assert.Equal(3, gated.MaxActive);
    }

    [Fact]
    public async Task Enqueue_RespectsPerKeyCap_EvenWithGlobalHeadroom()
    {
        var gated = new GatedDelegate();
        var domainConfigProvider = new StaticDomainConfigProvider(new DomainConfig
        {
            DomainName = "*",
            RetryIntervals = [TimeSpan.FromMinutes(1)],
            BounceAfter = TimeSpan.FromHours(1),
            MaxConcurrentConnections = 2
        });
        var scheduler = new FairShareDeliveryScheduler(domainConfigProvider, new SchedulerOptions { GlobalMaxConcurrency = 100 }, gated.InvokeAsync);

        const int totalItems = 8;
        for (var i = 0; i < totalItems; i++)
            scheduler.Enqueue(CreateItem("huge.test")); // mesma QueueKey pra todos

        await WaitUntilAsync(() => gated.Active == 2, TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.Equal(2, gated.Active); // nunca passa do teto por chave, mesmo com folga global enorme

        gated.ReleaseGate.Release(totalItems);
        await WaitUntilAsync(() => gated.Completed == totalItems, TimeSpan.FromSeconds(3));
        Assert.Equal(2, gated.MaxActive);
    }

    [Fact]
    public async Task Enqueue_SmallKeysDispatchPromptly_EvenWhileBigKeyIsSaturatedAtItsCap()
    {
        var gated = new GatedDelegate();
        var bigConfig = new DomainConfig { DomainName = "big.test", RetryIntervals = [TimeSpan.FromMinutes(1)], BounceAfter = TimeSpan.FromHours(1), MaxConcurrentConnections = 3 };
        var defaultConfig = new DomainConfig { DomainName = "*", RetryIntervals = [TimeSpan.FromMinutes(1)], BounceAfter = TimeSpan.FromHours(1) };
        var domainConfigProvider = new StaticDomainConfigProvider(defaultConfig, new Dictionary<string, DomainConfig> { ["big.test"] = bigConfig });

        // teto global (4) > teto do domínio grande (3): sempre fica pelo menos 1 slot de folga
        // estrutural pros domínios pequenos, mesmo com o grande saturado — essa é a garantia
        // de "nunca passa fome" do design.
        var scheduler = new FairShareDeliveryScheduler(domainConfigProvider, new SchedulerOptions { GlobalMaxConcurrency = 4 }, gated.InvokeAsync);

        for (var i = 0; i < 20; i++)
            scheduler.Enqueue(CreateItem("big.test"));

        await WaitUntilAsync(() => gated.Active == 3, TimeSpan.FromSeconds(2)); // big.test satura seu próprio teto

        for (var i = 0; i < 5; i++)
            scheduler.Enqueue(CreateItem($"small{i}.test"));

        // mesmo sem o domínio grande liberar nada, o 4º slot global livre deveria ser ocupado
        // por um dos domínios pequenos quase imediatamente
        await WaitUntilAsync(() => gated.Active == 4, TimeSpan.FromSeconds(2));
        Assert.Equal(4, gated.Active);

        gated.ReleaseGate.Release(25);
        await WaitUntilAsync(() => gated.Completed == 25, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Enqueue_DomainWithoutExplicitOverride_UsesDefaultMaxConcurrentConnections()
    {
        var domainConfigProvider = new StaticDomainConfigProvider(new DomainConfig
        {
            DomainName = "*",
            RetryIntervals = [TimeSpan.FromMinutes(1)],
            BounceAfter = TimeSpan.FromHours(1)
            // MaxConcurrentConnections não setado -> cai no default da classe (5)
        });
        var gated = new GatedDelegate();
        var scheduler = new FairShareDeliveryScheduler(domainConfigProvider, new SchedulerOptions { GlobalMaxConcurrency = 100 }, gated.InvokeAsync);

        const int totalItems = 8;
        for (var i = 0; i < totalItems; i++)
            scheduler.Enqueue(CreateItem("sem-config.test"));

        await WaitUntilAsync(() => gated.Active == 5, TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.Equal(5, gated.Active);

        gated.ReleaseGate.Release(totalItems);
        await WaitUntilAsync(() => gated.Completed == totalItems, TimeSpan.FromSeconds(2));
    }

    /// <summary>Uso legítimo pela CLI (SubmitCommand): teto global zero significa "só escreve no spool, nunca despacha" — não deve nem construir nem nunca disparar o delegate.</summary>
    [Fact]
    public async Task Enqueue_GlobalMaxConcurrencyZero_NeverDispatches_AndDoesNotThrow()
    {
        var gated = new GatedDelegate();
        var domainConfigProvider = new StaticDomainConfigProvider(new DomainConfig
        {
            DomainName = "*", RetryIntervals = [TimeSpan.FromMinutes(1)], BounceAfter = TimeSpan.FromHours(1)
        });

        var scheduler = new FairShareDeliveryScheduler(domainConfigProvider, new SchedulerOptions { GlobalMaxConcurrency = 0 }, gated.InvokeAsync);

        scheduler.Enqueue(CreateItem("sem-worker.test"));
        scheduler.Enqueue(CreateItem("sem-worker.test"));

        await Task.Delay(200); // folga generosa pra garantir que nada dispara de forma assíncrona
        Assert.Equal(0, gated.Active);
        Assert.Equal(0, gated.Completed);
    }
}
