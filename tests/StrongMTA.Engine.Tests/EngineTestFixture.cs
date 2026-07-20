using System.Net;
using StrongMTA.Accounting;
using StrongMTA.Bounce;
using StrongMTA.Core;
using StrongMTA.Dkim;
using StrongMTA.Spool;

namespace StrongMTA.Engine.Tests;

public sealed class EngineTestFixture : IDisposable
{
    public string RootDirectory { get; } = Path.Combine(Path.GetTempPath(), "strongmta-engine-tests-" + Guid.NewGuid().ToString("N"));
    public SpoolPaths Paths { get; }
    public SpoolWriter Writer { get; }
    public SpoolReader Reader { get; } = new();
    public SpoolStateUpdater StateUpdater { get; }
    public SpoolScanner Scanner { get; }
    public WarmupCounterStore WarmupCounterStore { get; }
    public BounceTokenStore BounceTokenStore { get; }
    public InMemoryAccountingSink Accounting { get; } = new();
    public ResponseRuleEngine RuleEngine { get; } = new();
    public BackoffStateStore BackoffStateStore { get; }
    public DisabledSourceStore DisabledSourceStore { get; }
    public BounceQueueService BounceQueueService { get; }

    public EngineTestFixture()
    {
        Paths = new SpoolPaths(RootDirectory);
        Writer = new SpoolWriter(Paths);
        StateUpdater = new SpoolStateUpdater(Paths, Reader, Writer);
        Scanner = new SpoolScanner(Paths, Reader);
        WarmupCounterStore = new WarmupCounterStore(Paths);
        BounceTokenStore = new BounceTokenStore(Paths);
        BackoffStateStore = new BackoffStateStore(Paths);
        DisabledSourceStore = new DisabledSourceStore(Paths);
        BounceQueueService = new BounceQueueService(Scanner, StateUpdater, Accounting);
    }

    public static DomainConfig CreateDomainConfig(TimeSpan? retryInterval = null, TimeSpan? bounceAfter = null) => new()
    {
        DomainName = "example.com",
        RetryIntervals = [retryInterval ?? TimeSpan.FromMilliseconds(80)],
        BounceAfter = bounceAfter ?? TimeSpan.FromMilliseconds(300)
    };

    public static VirtualMta CreateVirtualMta(
        string name = "vmta-01", IPAddress? sourceIp = null, string hostName = "client.test",
        string? coldVmtaName = null, int? coldDailyLimit = null) => new()
    {
        Name = name,
        SourceIp = sourceIp ?? IPAddress.Loopback,
        HostName = hostName,
        DkimSelector = "default",
        ColdVmtaName = coldVmtaName,
        ColdVmtaDailyLimitPerDomain = coldDailyLimit
    };

    public static IVirtualMtaProvider CreateVirtualMtaProvider(params VirtualMta[] vmtas) =>
        new StaticVirtualMtaProvider(vmtas.ToDictionary(v => v.Name));

    /// <summary>DKIM "no-op": nenhum domínio configurado, então toda mensagem passa sem assinatura (exercita o caminho real de pass-through).</summary>
    public static IDkimSigningService CreateNoOpDkimSigningService() =>
        new DkimSigningService(new StaticDkimKeyProvider(new Dictionary<string, DkimSigningConfig>()));

    public SubmissionService CreateSubmissionService(
        IDeliveryScheduler scheduler,
        IVirtualMtaProvider? vmtaProvider = null,
        IDkimSigningService? dkimSigningService = null,
        string bounceDomain = "bounce.strongmta.test") =>
        new(Paths, Writer, scheduler,
            dkimSigningService ?? CreateNoOpDkimSigningService(),
            vmtaProvider ?? CreateVirtualMtaProvider(CreateVirtualMta()),
            new WarmupRouter(WarmupCounterStore),
            BounceTokenStore,
            Accounting,
            bounceDomain);

    public void Dispose()
    {
        if (Directory.Exists(RootDirectory))
        {
            try { Directory.Delete(RootDirectory, recursive: true); }
            catch (IOException) { }
        }
    }
}
