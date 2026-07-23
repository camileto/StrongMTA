using System.Net;
using StrongMTA.Core;

namespace StrongMTA.Engine.Tests;

/// <summary>
/// Verifica que o teto por VirtualMta (<see cref="VirtualMta.MaxConcurrentConnections"/>) é
/// respeitado somando conexões de todos os domínios do mesmo VMTA, e que a ausência de teto
/// (null) não altera o comportamento existente.
/// </summary>
public class VmtaConcurrencyCapTests
{
    private static RecipientWorkItem CreateItem(string domain, string vmtaName = "vmta-capped") => new()
    {
        MessageId = Guid.NewGuid(),
        RecipientId = Guid.NewGuid(),
        MsgFilePath = "x.msg",
        StateFilePath = "x.state",
        EnvelopeFrom = "bounce@strongmta.test",
        RecipientAddress = $"a@{domain}",
        DestinationDomain = domain,
        VirtualMtaName = vmtaName,
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
            var prev = _maxActive;
            while (current > prev)
            {
                var observed = Interlocked.CompareExchange(ref _maxActive, current, prev);
                if (observed == prev) break;
                prev = observed;
            }
            await ReleaseGate.WaitAsync(ct).ConfigureAwait(false);
            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _completed);
        }
    }

    [Fact]
    public async Task VmtaCap_RespectsLimit_AcrossMultipleDomains()
    {
        // 3 domínios × 5 conexões por domínio = 15 possíveis; teto VMTA = 3 → nunca passa de 3
        var gated = new GatedDelegate();

        var vmta = new VirtualMta
        {
            Name = "vmta-capped",
            SourceIps = [IPAddress.Loopback],
            HostName = "mta.test",
            DkimSelector = "default",
            MaxConcurrentConnections = 3
        };
        var vmtaProvider = new StaticVirtualMtaProvider(new Dictionary<string, VirtualMta> { [vmta.Name] = vmta });

        var domainConfig = new DomainConfig
        {
            DomainName = "*",
            RetryIntervals = [TimeSpan.FromMinutes(1)],
            BounceAfter = TimeSpan.FromHours(1),
            MaxConcurrentConnections = 5 // bem acima do teto VMTA: só o VMTA é o gargalo
        };
        var domainConfigProvider = new StaticDomainConfigProvider(domainConfig);

        var scheduler = new FairShareDeliveryScheduler(
            domainConfigProvider,
            new SchedulerOptions { GlobalMaxConcurrency = 20 },
            gated.InvokeAsync,
            vmtaProvider);

        const int totalItems = 18; // 3 domínios × 6 itens
        for (var d = 0; d < 3; d++)
            for (var i = 0; i < 6; i++)
                scheduler.Enqueue(CreateItem($"domain{d}.test"));

        await WaitUntilAsync(() => gated.Active == 3, TimeSpan.FromSeconds(2));
        await Task.Delay(100); // folga pra garantir que não sobe além do teto
        Assert.Equal(3, gated.Active);

        gated.ReleaseGate.Release(totalItems);
        await WaitUntilAsync(() => gated.Completed == totalItems, TimeSpan.FromSeconds(3));
        Assert.Equal(3, gated.MaxActive);
    }

    [Fact]
    public async Task VmtaCap_NullLimit_AllowsFullDomainLevelConcurrency()
    {
        // Sem MaxConcurrentConnections no VMTA: comportamento idêntico ao anterior (sem VMTA cap)
        var gated = new GatedDelegate();

        var vmta = new VirtualMta
        {
            Name = "vmta-uncapped",
            SourceIps = [IPAddress.Loopback],
            HostName = "mta.test",
            DkimSelector = "default"
            // MaxConcurrentConnections = null → sem teto por VMTA
        };
        var vmtaProvider = new StaticVirtualMtaProvider(new Dictionary<string, VirtualMta> { [vmta.Name] = vmta });

        var domainConfig = new DomainConfig
        {
            DomainName = "*",
            RetryIntervals = [TimeSpan.FromMinutes(1)],
            BounceAfter = TimeSpan.FromHours(1),
            MaxConcurrentConnections = 3
        };
        var domainConfigProvider = new StaticDomainConfigProvider(domainConfig);

        var scheduler = new FairShareDeliveryScheduler(
            domainConfigProvider,
            new SchedulerOptions { GlobalMaxConcurrency = 20 },
            gated.InvokeAsync,
            vmtaProvider);

        // 2 domínios × 3 slots cada = 6 em voo possíveis (sem VMTA cap, só por domínio)
        for (var i = 0; i < 6; i++)
            scheduler.Enqueue(CreateItem("domain0.test", "vmta-uncapped"));
        for (var i = 0; i < 6; i++)
            scheduler.Enqueue(CreateItem("domain1.test", "vmta-uncapped"));

        await WaitUntilAsync(() => gated.Active == 6, TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.Equal(6, gated.Active); // 2 domínios × 3 cada = 6, sem interferência do VMTA cap

        gated.ReleaseGate.Release(12);
        await WaitUntilAsync(() => gated.Completed == 12, TimeSpan.FromSeconds(3));
    }
}
