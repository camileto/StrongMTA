using System.Threading.RateLimiting;
using StrongMTA.Core;

namespace StrongMTA.Engine.Tests;

public class RateLimitingTests
{
    private static RecipientWorkItem CreateItem(string domain = "rate.test", string vmta = "vmta-01") => new()
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

    private static FairShareDeliveryScheduler CreateScheduler(Func<RecipientWorkItem, CancellationToken, Task> deliver, int globalConcurrency = 10) =>
        new(
            new StaticDomainConfigProvider(new DomainConfig
            {
                DomainName = "*",
                RetryIntervals = [TimeSpan.FromMinutes(1)],
                BounceAfter = TimeSpan.FromHours(1),
                MaxConcurrentConnections = 10
            }),
            new SchedulerOptions { GlobalMaxConcurrency = globalConcurrency },
            deliver);

    [Fact]
    public async Task Lane_WithRateLimiter_BlocksAboveLimit()
    {
        var active = 0;
        var completed = 0;
        var release = new SemaphoreSlim(0);

        var scheduler = CreateScheduler(async (_, ct) =>
        {
            Interlocked.Increment(ref active);
            await release.WaitAsync(ct);
            Interlocked.Decrement(ref active);
            Interlocked.Increment(ref completed);
        });

        // 2 tokens, sem auto-replenish: testa o limite fixo sem esperar replenishment
        var rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 2,
            TokensPerPeriod = 2,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            AutoReplenishment = false,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });

        var lane = new QueueLaneAccessor(rateLimiter, maxConcurrent: 10, scheduler);

        for (var i = 0; i < 5; i++)
            lane.Enqueue(CreateItem());

        // apenas 2 devem ser despachados (esgotaram os 2 tokens disponíveis)
        await WaitUntilAsync(() => Volatile.Read(ref active) == 2, TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.Equal(2, Volatile.Read(ref active));

        release.Release(5);
        await WaitUntilAsync(() => Volatile.Read(ref completed) == 2, TimeSpan.FromSeconds(2));
        Assert.Equal(2, Volatile.Read(ref completed));
    }

    [Fact]
    public async Task Lane_WithRateLimiter_ReplenishmentAllowsMoreDeliveries()
    {
        var completed = 0;

        var scheduler = CreateScheduler(async (_, _) =>
        {
            await Task.Yield();
            Interlocked.Increment(ref completed);
        });

        // 1 token reposto a cada 200ms: testa que os re-pumps agendados disparam após replenishment
        var rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMilliseconds(200),
            AutoReplenishment = true,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });

        var lane = new QueueLaneAccessor(rateLimiter, maxConcurrent: 10, scheduler);

        for (var i = 0; i < 3; i++)
            lane.Enqueue(CreateItem());

        await WaitUntilAsync(() => Volatile.Read(ref completed) >= 3, TimeSpan.FromSeconds(3));
        Assert.True(Volatile.Read(ref completed) >= 3);
    }

    [Fact]
    public async Task Scheduler_WithMaxMessagesPerMinuteZero_DoesNotRateLimit()
    {
        var completed = 0;

        var scheduler = CreateScheduler(async (_, _) =>
        {
            await Task.Yield();
            Interlocked.Increment(ref completed);
        }, globalConcurrency: 20);

        // MaxMessagesPerMinute=0 → CreateLane não cria rate limiter → todos despachados livremente
        for (var i = 0; i < 10; i++)
            scheduler.Enqueue(CreateItem());

        await WaitUntilAsync(() => Volatile.Read(ref completed) == 10, TimeSpan.FromSeconds(2));
        Assert.Equal(10, Volatile.Read(ref completed));
    }
}

/// <summary>
/// Cria uma <see cref="QueueLane"/> com um rate limiter externo para testes de throttling.
/// QueueLane é internal; acesso permitido via InternalsVisibleTo configurado no Engine.csproj.
/// </summary>
internal sealed class QueueLaneAccessor
{
    private readonly QueueLane _lane;

    public QueueLaneAccessor(RateLimiter? rateLimiter, int maxConcurrent, FairShareDeliveryScheduler owner)
    {
        var key = new QueueKey { DestinationDomain = "rate.test", VirtualMtaName = "vmta-01" };
        _lane = new QueueLane(key, maxConcurrent, owner, rateLimiter);
    }

    public void Enqueue(RecipientWorkItem item) => _lane.Enqueue(item);
}
