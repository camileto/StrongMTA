using System.Net;
using System.Text;
using StrongMTA.Core;
using StrongMTA.Smtp.Client;
using StrongMTA.Smtp.Client.Tests;

namespace StrongMTA.Engine.Tests;

/// <summary>
/// Testa o round-robin de IPs de origem e a desabilitação por IP individual.
/// O MX (servidor destino) é sempre 127.0.0.1; o que varia é o IP LOCAL usado para bindar
/// o socket (LocalIpAddress do SmtpDeliveryRequest), observável via RemoteEndPoint no servidor.
/// </summary>
public class IpPoolTests : IDisposable
{
    private readonly EngineTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private static Func<CancellationToken, Task<Stream>> Body() =>
        _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("Subject: t\r\n\r\ncorpo\r\n")));

    private SpoolDeliveryWorker CreateWorker(int smtpPort, PendingRetryIndex pendingIndex, IVirtualMtaProvider vmtaProvider) =>
        new(_fixture.Reader, _fixture.StateUpdater, new TestMxResolver(), _fixture.Accounting,
            new StaticDomainConfigProvider(EngineTestFixture.CreateDomainConfig(bounceAfter: TimeSpan.FromHours(1))),
            vmtaProvider, pendingIndex, _fixture.RuleEngine, _fixture.BackoffStateStore,
            _fixture.DisabledSourceStore, _fixture.BounceQueueService, smtpPort: smtpPort);

    [Fact]
    public async Task IpPool_RoundRobin_AlternatesSourceIpAcrossDeliveries()
    {
        await using var server = new FakeSmtpServer(IPAddress.Loopback);
        server.Start();

        // pool com 2 IPs: round-robin alterna entre eles a cada entrega
        var vmta = EngineTestFixture.CreateVirtualMta(
            name: "vmta-pool",
            sourceIps: [IPAddress.Loopback, IPAddress.Parse("127.0.0.2")]);
        var vmtaProvider = EngineTestFixture.CreateVirtualMtaProvider(vmta);

        var pendingIndex = new PendingRetryIndex();
        var scheduler = new RecordingScheduler();
        var submission = _fixture.CreateSubmissionService(scheduler, vmtaProvider);
        var worker = CreateWorker(server.Port, pendingIndex, vmtaProvider);

        for (var i = 0; i < 2; i++)
            await submission.SubmitAsync(null, [new SubmissionRecipient($"r{i}@example.com", "vmta-pool")], Body());

        // entrega 1: IP[0] = 127.0.0.1
        await worker.DeliverOneAsync(scheduler.Items[0], CancellationToken.None);
        var sourceIp1 = server.RemoteEndPoint!.Address;

        // entrega 2: IP[1] = 127.0.0.2
        await worker.DeliverOneAsync(scheduler.Items[1], CancellationToken.None);
        var sourceIp2 = server.RemoteEndPoint!.Address;

        // os dois source IPs devem ser distintos (round-robin funcionando)
        Assert.NotEqual(sourceIp1, sourceIp2);
        Assert.Equal(IPAddress.Loopback, sourceIp1);
        Assert.Equal(IPAddress.Parse("127.0.0.2"), sourceIp2);
    }

    [Fact]
    public async Task IpPool_SkipsDisabledIp_UsesNextAvailableSourceIp()
    {
        await using var server = new FakeSmtpServer(IPAddress.Loopback);
        server.Start();

        var vmta = EngineTestFixture.CreateVirtualMta(
            name: "vmta-pool2",
            sourceIps: [IPAddress.Loopback, IPAddress.Parse("127.0.0.2")]);
        var vmtaProvider = EngineTestFixture.CreateVirtualMtaProvider(vmta);

        // desabilita o primeiro IP (127.0.0.1) — round-robin começaria por ele, mas deve saltar
        await _fixture.DisabledSourceStore.DisableAsync(IPAddress.Loopback.ToString(), reenableAfter: null);

        var pendingIndex = new PendingRetryIndex();
        var scheduler = new RecordingScheduler();
        var submission = _fixture.CreateSubmissionService(scheduler, vmtaProvider);
        var worker = CreateWorker(server.Port, pendingIndex, vmtaProvider);

        await submission.SubmitAsync(null, [new SubmissionRecipient("r@example.com", "vmta-pool2")], Body());
        var result = await worker.DeliverOneAsync(scheduler.Items[0], CancellationToken.None);

        Assert.Equal(SmtpDeliveryOutcome.Delivered, result.Outcome);
        // 127.0.0.1 foi pulado; o socket foi bindado em 127.0.0.2
        Assert.Equal(IPAddress.Parse("127.0.0.2"), server.RemoteEndPoint!.Address);
    }

    [Fact]
    public async Task IpPool_AllIpsDisabled_SkipsDeliveryAndSchedulesRetry()
    {
        var vmta = EngineTestFixture.CreateVirtualMta(
            name: "vmta-alldisabled",
            sourceIps: [IPAddress.Loopback, IPAddress.Parse("127.0.0.2")]);
        var vmtaProvider = EngineTestFixture.CreateVirtualMtaProvider(vmta);

        await _fixture.DisabledSourceStore.DisableAsync(IPAddress.Loopback.ToString(), reenableAfter: null);
        await _fixture.DisabledSourceStore.DisableAsync("127.0.0.2", reenableAfter: null);

        var pendingIndex = new PendingRetryIndex();
        var scheduler = new RecordingScheduler();
        var submission = _fixture.CreateSubmissionService(scheduler, vmtaProvider);

        await submission.SubmitAsync(null, [new SubmissionRecipient("r@example.com", "vmta-alldisabled")], Body());

        // porta inválida: se tentar conectar o teste falha imediatamente
        var worker = CreateWorker(smtpPort: 1, pendingIndex, vmtaProvider);
        var result = await worker.DeliverOneAsync(scheduler.Items[0], CancellationToken.None);

        Assert.Equal(SmtpDeliveryOutcome.ConnectionFailed, result.Outcome);
        var state = await _fixture.Reader.ReadStateAsync(scheduler.Items[0].StateFilePath);
        Assert.Equal(RecipientStatus.Transient, state!.Recipients[0].Status);
        Assert.Equal(1, pendingIndex.Count);
    }
}
