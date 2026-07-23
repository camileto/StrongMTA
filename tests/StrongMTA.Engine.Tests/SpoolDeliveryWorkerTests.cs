using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using StrongMTA.Core;
using StrongMTA.Smtp.Client;
using StrongMTA.Smtp.Client.Tests;

namespace StrongMTA.Engine.Tests;

public class SpoolDeliveryWorkerTests : IDisposable
{
    private readonly EngineTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private static Func<CancellationToken, Task<Stream>> Body(string text = "Subject: t\r\n\r\ncorpo\r\n") =>
        _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    private SpoolDeliveryWorker CreateWorker(
        int smtpPort, PendingRetryIndex pendingIndex,
        IVirtualMtaProvider? vmtaProvider = null, DomainConfig? domainConfig = null, IMxResolver? mxResolver = null) =>
        new(_fixture.Reader, _fixture.StateUpdater, mxResolver ?? new TestMxResolver(), _fixture.Accounting,
            new StaticDomainConfigProvider(domainConfig ?? EngineTestFixture.CreateDomainConfig()),
            vmtaProvider ?? EngineTestFixture.CreateVirtualMtaProvider(EngineTestFixture.CreateVirtualMta()),
            pendingIndex, _fixture.RuleEngine, _fixture.BackoffStateStore, _fixture.DisabledSourceStore, _fixture.BounceQueueService,
            smtpPort: smtpPort);

    [Fact]
    public async Task DeliverOneAsync_Success_MarksRecipientDelivered_AndRecordsAccountingEvent()
    {
        await using var server = new FakeSmtpServer();
        server.Start();

        var pendingIndex = new PendingRetryIndex();
        var scheduler = new RecordingScheduler();
        var submission = _fixture.CreateSubmissionService(scheduler);
        var worker = CreateWorker(server.Port, pendingIndex);

        await submission.SubmitAsync(null,
            [new SubmissionRecipient("destino@example.com", "vmta-01")], Body());

        var item = scheduler.Items[0];
        var result = await worker.DeliverOneAsync(item, CancellationToken.None);

        Assert.Equal(SmtpDeliveryOutcome.Delivered, result.Outcome);

        var state = await _fixture.Reader.ReadStateAsync(item.StateFilePath);
        Assert.Equal(RecipientStatus.Delivered, state!.Recipients[0].Status);

        Assert.Equal(2, _fixture.Accounting.Events.Count); // Received (na submissão) + Delivered (na entrega)
        Assert.Equal(AccountingEventType.Received, _fixture.Accounting.Events[0].Type);
        Assert.Equal(AccountingEventType.Delivered, _fixture.Accounting.Events[1].Type);
        Assert.Equal(0, pendingIndex.Count);
    }

    [Fact]
    public async Task DeliverOneAsync_RecipientPaused_SkipsWithoutConnectingOrChangingState()
    {
        var pendingIndex = new PendingRetryIndex();
        var scheduler = new RecordingScheduler();
        var submission = _fixture.CreateSubmissionService(scheduler);

        await submission.SubmitAsync(null, [new SubmissionRecipient("destino@example.com", "vmta-01")], Body());
        var item = scheduler.Items[0];

        await _fixture.StateUpdater.UpdateRecipientAsync(item.MessageId, item.RecipientId,
            recipient => recipient.Status = RecipientStatus.Paused);

        // porta inválida de propósito: se o worker tentasse conectar mesmo pausado, o teste falharia por exceção/timeout
        var worker = CreateWorker(smtpPort: 1, pendingIndex);
        var result = await worker.DeliverOneAsync(item, CancellationToken.None);

        Assert.Equal(SmtpDeliveryOutcome.Skipped, result.Outcome);

        var state = await _fixture.Reader.ReadStateAsync(item.StateFilePath);
        Assert.Equal(RecipientStatus.Paused, state!.Recipients[0].Status);
        Assert.Equal(0, pendingIndex.Count);
        Assert.Single(_fixture.Accounting.Events); // só o Received da submissão, nenhum evento novo para o skip
    }

    [Fact]
    public async Task DeliverOneAsync_UsesVirtualMtaHostNameAsHelo_AndBindsItsSourceIp()
    {
        await using var server = new FakeSmtpServer();
        server.Start();

        var vmta = EngineTestFixture.CreateVirtualMta(name: "vmta-loopback2", sourceIp: IPAddress.Parse("127.0.0.2"), hostName: "vmta-loopback2.strongmta.test");
        var vmtaProvider = EngineTestFixture.CreateVirtualMtaProvider(vmta);

        var pendingIndex = new PendingRetryIndex();
        var scheduler = new RecordingScheduler();
        var submission = _fixture.CreateSubmissionService(scheduler, vmtaProvider);
        var worker = CreateWorker(server.Port, pendingIndex, vmtaProvider);

        await submission.SubmitAsync(null,
            [new SubmissionRecipient("destino@example.com", "vmta-loopback2")], Body());

        var item = scheduler.Items[0];
        var result = await worker.DeliverOneAsync(item, CancellationToken.None);

        Assert.Equal(SmtpDeliveryOutcome.Delivered, result.Outcome);
        Assert.Equal("vmta-loopback2.strongmta.test", server.ReceivedEhloArgument);
        Assert.Equal(IPAddress.Parse("127.0.0.2"), server.RemoteEndPoint!.Address);
    }

    [Fact]
    public async Task DeliverOneAsync_Bounce5xx_MarksRecipientBounced_AndDoesNotSchedule()
    {
        await using var server = new FakeSmtpServer { RcptToResponse = "550 5.1.1 mailbox unavailable\r\n" };
        server.Start();

        var pendingIndex = new PendingRetryIndex();
        var scheduler = new RecordingScheduler();
        var submission = _fixture.CreateSubmissionService(scheduler);
        var worker = CreateWorker(server.Port, pendingIndex);

        await submission.SubmitAsync(null,
            [new SubmissionRecipient("destino@example.com", "vmta-01")], Body());
        var item = scheduler.Items[0];

        var result = await worker.DeliverOneAsync(item, CancellationToken.None);

        Assert.Equal(SmtpDeliveryOutcome.Bounced, result.Outcome);
        var state = await _fixture.Reader.ReadStateAsync(item.StateFilePath);
        Assert.Equal(RecipientStatus.Bounced, state!.Recipients[0].Status);
        Assert.Equal(0, pendingIndex.Count);
    }

    [Fact]
    public async Task DeliverOneAsync_Transient4xx_SchedulesRetry_WithComputedNextAttempt()
    {
        await using var server = new FakeSmtpServer { FinalResponse = "451 4.3.0 temp\r\n" };
        server.Start();

        var domainConfig = EngineTestFixture.CreateDomainConfig(retryInterval: TimeSpan.FromMinutes(10), bounceAfter: TimeSpan.FromHours(48));
        var pendingIndex = new PendingRetryIndex();
        var scheduler = new RecordingScheduler();
        var submission = _fixture.CreateSubmissionService(scheduler);
        var worker = CreateWorker(server.Port, pendingIndex, domainConfig: domainConfig);

        await submission.SubmitAsync(null,
            [new SubmissionRecipient("destino@example.com", "vmta-01")], Body());
        var item = scheduler.Items[0];

        var before = DateTimeOffset.UtcNow;
        var result = await worker.DeliverOneAsync(item, CancellationToken.None);

        Assert.Equal(SmtpDeliveryOutcome.Transient, result.Outcome);
        var state = await _fixture.Reader.ReadStateAsync(item.StateFilePath);
        Assert.Equal(RecipientStatus.Transient, state!.Recipients[0].Status);
        Assert.Equal(1, state.Recipients[0].AttemptCount);
        Assert.True(state.Recipients[0].NextAttemptAt >= before.Add(TimeSpan.FromMinutes(10)).AddSeconds(-2));

        Assert.Equal(1, pendingIndex.Count);
    }

    [Fact]
    public async Task DeliverOneAsync_RepeatedTransient_EventuallyExpiresAfterTtlExpires()
    {
        await using var server = new FakeSmtpServer { FinalResponse = "451 4.3.0 temp sempre\r\n" };
        server.Start();

        var domainConfig = EngineTestFixture.CreateDomainConfig(retryInterval: TimeSpan.FromMilliseconds(60), bounceAfter: TimeSpan.FromMilliseconds(250));
        var pendingIndex = new PendingRetryIndex();
        var scheduler = new RecordingScheduler();
        var submission = _fixture.CreateSubmissionService(scheduler);
        var worker = CreateWorker(server.Port, pendingIndex, domainConfig: domainConfig);
        var retryScheduler = new RetryScheduler(pendingIndex, scheduler);

        await submission.SubmitAsync(null,
            [new SubmissionRecipient("destino@example.com", "vmta-01")], Body());

        var deadline = DateTime.UtcNow.AddSeconds(5);
        var finalStatus = RecipientStatus.Pending;

        while (DateTime.UtcNow < deadline)
        {
            if (scheduler.TryDequeue(out var item))
            {
                await worker.DeliverOneAsync(item, CancellationToken.None);

                var state = await _fixture.Reader.ReadStateAsync(item.StateFilePath);
                finalStatus = state!.Recipients[0].Status;
                if (finalStatus == RecipientStatus.Expired)
                    break;
            }
            else
            {
                await retryScheduler.RunOnceAsync();
                await Task.Delay(20);
            }
        }

        Assert.Equal(RecipientStatus.Expired, finalStatus);
        Assert.True(_fixture.Accounting.Events.Count >= 2, "esperava ao menos uma tentativa transient seguida da expiração definitiva");
        Assert.Equal(AccountingEventType.Expired, _fixture.Accounting.Events[^1].Type);
        Assert.Contains("bounce-after", _fixture.Accounting.Events[^1].SmtpResponseText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeliverOneAsync_RuleForcesBounceOn4xx_MarksRecipientBounced_AndDoesNotSchedule()
    {
        await using var server = new FakeSmtpServer { RcptToResponse = "450 4.2.1 mailbox temporarily unavailable, but blacklisted\r\n" };
        server.Start();

        var domainConfig = new DomainConfig
        {
            DomainName = "example.com",
            RetryIntervals = [TimeSpan.FromMilliseconds(80)],
            BounceAfter = TimeSpan.FromHours(1),
            ResponseRules = [new ResponseRule
            {
                Pattern = new Regex("blacklisted", RegexOptions.IgnoreCase),
                Actions = [ResponseRuleAction.ForceBounce]
            }]
        };

        var pendingIndex = new PendingRetryIndex();
        var scheduler = new RecordingScheduler();
        var submission = _fixture.CreateSubmissionService(scheduler);
        var worker = CreateWorker(server.Port, pendingIndex, domainConfig: domainConfig);

        await submission.SubmitAsync(null, [new SubmissionRecipient("destino@example.com", "vmta-01")], Body());
        var item = scheduler.Items[0];

        var result = await worker.DeliverOneAsync(item, CancellationToken.None);

        Assert.Equal(SmtpDeliveryOutcome.Transient, result.Outcome); // a resposta SMTP crua continua 4xx
        var state = await _fixture.Reader.ReadStateAsync(item.StateFilePath);
        Assert.Equal(RecipientStatus.Bounced, state!.Recipients[0].Status); // mas a regra força o bounce mesmo assim
        Assert.Equal(0, pendingIndex.Count);
    }

    [Fact]
    public async Task DeliverOneAsync_SkipMxRule_AdvancesToNextMxHost_AndDelivers()
    {
        await using var server1 = new FakeSmtpServer(IPAddress.Loopback) { RcptToResponse = "451 4.4.1 try another mx\r\n" };
        server1.Start();
        await using var server2 = new FakeSmtpServer(IPAddress.Parse("127.0.0.2"), server1.Port);
        server2.Start();

        var domainConfig = new DomainConfig
        {
            DomainName = "example.com",
            RetryIntervals = [TimeSpan.FromMilliseconds(80)],
            BounceAfter = TimeSpan.FromHours(1),
            ResponseRules = [new ResponseRule
            {
                Pattern = new Regex("try another mx", RegexOptions.IgnoreCase),
                Actions = [ResponseRuleAction.SkipMx]
            }]
        };

        var mxResolver = new TestMxResolver("127.0.0.1", "127.0.0.2");
        var pendingIndex = new PendingRetryIndex();
        var scheduler = new RecordingScheduler();
        var submission = _fixture.CreateSubmissionService(scheduler);
        var worker = CreateWorker(server1.Port, pendingIndex, domainConfig: domainConfig, mxResolver: mxResolver);

        await submission.SubmitAsync(null, [new SubmissionRecipient("destino@example.com", "vmta-01")], Body());
        var item = scheduler.Items[0];

        var result = await worker.DeliverOneAsync(item, CancellationToken.None);

        Assert.Equal(SmtpDeliveryOutcome.Delivered, result.Outcome);
        Assert.Null(server1.ReceivedDataBody); // primeiro host rejeitou no RCPT TO, nunca chegou no DATA
        Assert.NotNull(server2.ReceivedDataBody); // segundo host recebeu a entrega completa
        var state = await _fixture.Reader.ReadStateAsync(item.StateFilePath);
        Assert.Equal(RecipientStatus.Delivered, state!.Recipients[0].Status);
    }

    [Fact]
    public async Task DeliverOneAsync_EnterBackoffRule_UsesBackoffRetryInterval_OnSubsequentAttempt()
    {
        await using var server = new FakeSmtpServer { FinalResponse = "451 4.7.1 rate limited, slow down\r\n" };
        server.Start();

        var domainConfig = new DomainConfig
        {
            DomainName = "example.com",
            RetryIntervals = [TimeSpan.FromMinutes(1)],
            BackoffRetryIntervals = [TimeSpan.FromMinutes(30)],
            BounceAfter = TimeSpan.FromHours(48),
            ResponseRules = [new ResponseRule
            {
                Pattern = new Regex("rate limited", RegexOptions.IgnoreCase),
                Actions = [ResponseRuleAction.EnterBackoff]
            }]
        };

        var pendingIndex = new PendingRetryIndex();
        var scheduler = new RecordingScheduler();
        var submission = _fixture.CreateSubmissionService(scheduler);
        var worker = CreateWorker(server.Port, pendingIndex, domainConfig: domainConfig);

        await submission.SubmitAsync(null, [new SubmissionRecipient("destino@example.com", "vmta-01")], Body());
        var item = scheduler.Items[0];

        await worker.DeliverOneAsync(item, CancellationToken.None); // 1a tentativa: entra em backoff, mas ainda agenda com o intervalo normal

        var queueKey = new QueueKey { DestinationDomain = "example.com", VirtualMtaName = "vmta-01" };
        Assert.True(await _fixture.BackoffStateStore.IsInBackoffAsync(queueKey));

        var before = DateTimeOffset.UtcNow;
        await worker.DeliverOneAsync(item, CancellationToken.None); // 2a tentativa: já em backoff, usa BackoffRetryIntervals

        var state = await _fixture.Reader.ReadStateAsync(item.StateFilePath);
        Assert.Equal(RecipientStatus.Transient, state!.Recipients[0].Status);
        Assert.True(state.Recipients[0].NextAttemptAt >= before.Add(TimeSpan.FromMinutes(30)).AddSeconds(-2));
    }

    [Fact]
    public async Task DeliverOneAsync_DisabledVirtualMta_SkipsConnectionEntirely_AndSchedulesRetry()
    {
        var pendingIndex = new PendingRetryIndex();
        var scheduler = new RecordingScheduler();
        var submission = _fixture.CreateSubmissionService(scheduler);

        await submission.SubmitAsync(null, [new SubmissionRecipient("destino@example.com", "vmta-01")], Body());
        var item = scheduler.Items[0];

        // desabilita pelo IP do VirtualMta padrão (127.0.0.1), não pelo nome do VMTA
        await _fixture.DisabledSourceStore.DisableAsync(IPAddress.Loopback.ToString(), reenableAfter: null);

        // porta inválida de propósito: se o worker tentasse conectar mesmo desabilitado, o teste falharia por exceção/timeout
        var worker = CreateWorker(smtpPort: 1, pendingIndex);
        var result = await worker.DeliverOneAsync(item, CancellationToken.None);

        Assert.Equal(SmtpDeliveryOutcome.ConnectionFailed, result.Outcome);
        var state = await _fixture.Reader.ReadStateAsync(item.StateFilePath);
        Assert.Equal(RecipientStatus.Transient, state!.Recipients[0].Status);
        Assert.Equal(1, pendingIndex.Count);
    }
}
