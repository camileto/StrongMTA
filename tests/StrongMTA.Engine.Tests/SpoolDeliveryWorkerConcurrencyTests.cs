using System.Net;
using System.Text;
using StrongMTA.Core;
using StrongMTA.Smtp.Client.Tests;

namespace StrongMTA.Engine.Tests;

/// <summary>
/// Ponta a ponta com sockets reais (sem mock): prova que o <see cref="FairShareDeliveryScheduler"/>
/// + <see cref="SpoolDeliveryWorker"/> de fato paraleliza entregas reais via loopback, respeita o
/// teto por domínio mesmo sob carga concentrada (cenário 80/20 do usuário: poucos domínios grandes
/// concentram a maior parte do volume) e não deixa domínios pequenos esperando indefinidamente.
/// </summary>
public class SpoolDeliveryWorkerConcurrencyTests : IDisposable
{
    private readonly EngineTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private static Func<CancellationToken, Task<Stream>> Body() =>
        _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("Subject: t\r\n\r\ncorpo\r\n")));

    [Fact]
    public async Task FairShareScheduler_DeliversConcurrently_RespectsPerDomainCap_AndDoesNotStarveSmallDomains()
    {
        var artificialDelay = TimeSpan.FromMilliseconds(150);

        // 3 domínios "grandes" (Gmail/Hotmail/Yahoo do cenário do usuário), cada um com seu
        // próprio FakeSmtpServer (endereços de loopback distintos, mesma porta compartilhada —
        // mesmo truque já usado no teste de skip-mx do M6).
        await using var big1 = new FakeSmtpServer(IPAddress.Loopback) { ArtificialDelay = artificialDelay };
        big1.Start();
        var sharedPort = big1.Port;
        await using var big2 = new FakeSmtpServer(IPAddress.Parse("127.0.0.2"), sharedPort) { ArtificialDelay = artificialDelay };
        big2.Start();
        await using var big3 = new FakeSmtpServer(IPAddress.Parse("127.0.0.3"), sharedPort) { ArtificialDelay = artificialDelay };
        big3.Start();

        // domínios "pequenos": todos batem no mesmo servidor fake (representa "o resto da internet"),
        // um destinatário cada — o que importa é o teto por QueueKey de cada domínio, independente.
        await using var smallServer = new FakeSmtpServer(IPAddress.Parse("127.0.0.4"), sharedPort) { ArtificialDelay = artificialDelay };
        smallServer.Start();

        var hostByDomain = new Dictionary<string, string>
        {
            ["big1.test"] = "127.0.0.1",
            ["big2.test"] = "127.0.0.2",
            ["big3.test"] = "127.0.0.3"
        };
        const int smallDomainCount = 10;
        for (var i = 0; i < smallDomainCount; i++)
            hostByDomain[$"small{i}.test"] = "127.0.0.4";

        const int bigDomainCap = 3;
        var bigDomainConfig = new DomainConfig
        {
            DomainName = "big",
            RetryIntervals = [TimeSpan.FromMinutes(1)],
            BounceAfter = TimeSpan.FromHours(1),
            MaxConcurrentConnections = bigDomainCap
        };
        var defaultDomainConfig = new DomainConfig
        {
            DomainName = "*",
            RetryIntervals = [TimeSpan.FromMinutes(1)],
            BounceAfter = TimeSpan.FromHours(1),
            MaxConcurrentConnections = 5
        };
        var domainConfigProvider = new StaticDomainConfigProvider(defaultDomainConfig, new Dictionary<string, DomainConfig>
        {
            ["big1.test"] = bigDomainConfig,
            ["big2.test"] = bigDomainConfig,
            ["big3.test"] = bigDomainConfig
        });

        var vmtaProvider = EngineTestFixture.CreateVirtualMtaProvider(EngineTestFixture.CreateVirtualMta());
        var mxResolver = new DomainMappedMxResolver(hostByDomain);
        var pendingRetryIndex = new PendingRetryIndex();

        var worker = new SpoolDeliveryWorker(
            _fixture.Reader, _fixture.StateUpdater, mxResolver, _fixture.Accounting,
            domainConfigProvider, vmtaProvider, pendingRetryIndex,
            _fixture.RuleEngine, _fixture.BackoffStateStore, _fixture.DisabledSourceStore, _fixture.BounceQueueService,
            smtpPort: sharedPort);

        // teto global (10) > soma dos tetos dos 3 domínios grandes saturados (9) — sempre fica
        // folga estrutural pros domínios pequenos, mesmo com os grandes no limite.
        var scheduler = new FairShareDeliveryScheduler(domainConfigProvider, new SchedulerOptions { GlobalMaxConcurrency = 10 }, worker.DeliverOneAsync);
        var submission = _fixture.CreateSubmissionService(scheduler, vmtaProvider);

        const int recipientsPerBigDomain = 6;
        var allMessageIds = new List<Guid>();

        var started = DateTimeOffset.UtcNow;

        foreach (var bigDomain in new[] { "big1.test", "big2.test", "big3.test" })
        {
            for (var i = 0; i < recipientsPerBigDomain; i++)
            {
                var messageId = await submission.SubmitAsync(null,
                    [new SubmissionRecipient($"dest{i}@{bigDomain}", "vmta-01")], Body());
                allMessageIds.Add(messageId);
            }
        }

        for (var i = 0; i < smallDomainCount; i++)
        {
            var messageId = await submission.SubmitAsync(null,
                [new SubmissionRecipient($"dest@small{i}.test", "vmta-01")], Body());
            allMessageIds.Add(messageId);
        }

        var totalItems = allMessageIds.Count;
        Assert.Equal(recipientsPerBigDomain * 3 + smallDomainCount, totalItems);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        int deliveredCount;
        do
        {
            deliveredCount = 0;
            foreach (var messageId in allMessageIds)
            {
                var state = await _fixture.Reader.ReadStateAsync(_fixture.Paths.GetStateFilePath(messageId));
                if (state!.Recipients[0].Status == RecipientStatus.Delivered)
                    deliveredCount++;
            }
            if (deliveredCount < totalItems)
                await Task.Delay(20);
        } while (deliveredCount < totalItems && DateTime.UtcNow < deadline);

        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(totalItems, deliveredCount);

        // sequentialEstimate = artificialDelay * totalItems = tempo se cada entrega fosse 100% sequencial
        // e não houvesse nenhum overhead de socket/spool. Com paralelismo real (e overhead de I/O que
        // soma à latência artificial), o tempo total deve ser MENOR que este valor — o que comprova que
        // a entrega está ocorrendo em paralelo, mesmo levando em conta o overhead de CI/WSL2.
        var sequentialEstimate = artificialDelay * totalItems;
        Assert.True(elapsed < sequentialEstimate,
            $"esperava entrega paralela mais rápida que o sequencial estimado ({sequentialEstimate}); levou {elapsed}");

        Assert.True(big1.MaxConcurrentSeen <= bigDomainCap, $"big1 viu {big1.MaxConcurrentSeen} conexões simultâneas, acima do teto {bigDomainCap}");
        Assert.True(big2.MaxConcurrentSeen <= bigDomainCap, $"big2 viu {big2.MaxConcurrentSeen} conexões simultâneas, acima do teto {bigDomainCap}");
        Assert.True(big3.MaxConcurrentSeen <= bigDomainCap, $"big3 viu {big3.MaxConcurrentSeen} conexões simultâneas, acima do teto {bigDomainCap}");
    }
}
