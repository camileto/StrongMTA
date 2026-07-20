using System.Text;
using StrongMTA.Core;
using StrongMTA.Spool;

namespace StrongMTA.Engine.Tests;

/// <summary>
/// Simula o cenário "mata o processo no meio de uma entrega": escreve .msg/.state
/// diretamente no spool (como se uma instância anterior tivesse persistido aquele
/// estado antes de morrer) e então roda a recuperação de boot numa instância nova.
/// </summary>
public class SpoolBootRecoveryTests : IDisposable
{
    private readonly EngineTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private async Task<(Guid MessageId, RecipientEnvelopeData[] Recipients)> WriteMessageWithRecipientsAsync(
        params (RecipientStatus Status, DateTimeOffset NextAttemptAt, int AttemptCount)[] recipientStates)
    {
        var recipients = recipientStates.Select(_ =>
        {
            var recipientId = Guid.NewGuid();
            return new RecipientEnvelopeData
            {
                RecipientId = recipientId,
                Address = $"r-{recipientId:N}@example.com",
                EnvelopeFrom = $"bounce-{recipientId:N}@strongmta.test",
                DestinationDomain = "example.com",
                VirtualMtaName = "vmta-01"
            };
        }).ToArray();

        var envelope = new MessageEnvelopeData
        {
            MessageId = Guid.NewGuid(),
            SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Recipients = recipients
        };

        using var body = new MemoryStream(Encoding.UTF8.GetBytes("corpo"));
        await _fixture.Writer.WriteMessageAsync(envelope, body);

        var state = new MessageStateData
        {
            MessageId = envelope.MessageId,
            Recipients = recipients.Zip(recipientStates, (r, s) => new RecipientStateData
            {
                RecipientId = r.RecipientId,
                Status = s.Status,
                NextAttemptAt = s.NextAttemptAt,
                AttemptCount = s.AttemptCount
            }).ToList()
        };
        await _fixture.Writer.WriteStateAsync(state);

        return (envelope.MessageId, recipients);
    }

    [Fact]
    public async Task RecoverAsync_PendingDue_GoesDirectlyToQueue()
    {
        await WriteMessageWithRecipientsAsync((RecipientStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(-1), 0));

        var recovery = new SpoolBootRecovery(_fixture.Scanner);
        var scheduler = new RecordingScheduler();
        var pendingIndex = new PendingRetryIndex();

        var recovered = await recovery.RecoverAsync(scheduler, pendingIndex);

        Assert.Equal(1, recovered);
        Assert.Equal(0, pendingIndex.Count);
        Assert.Equal(1, scheduler.Count);
    }

    [Fact]
    public async Task RecoverAsync_TransientWithFutureNextAttempt_GoesToPendingIndex_NotQueue()
    {
        await WriteMessageWithRecipientsAsync((RecipientStatus.Transient, DateTimeOffset.UtcNow.AddHours(1), 2));

        var recovery = new SpoolBootRecovery(_fixture.Scanner);
        var scheduler = new RecordingScheduler();
        var pendingIndex = new PendingRetryIndex();

        var recovered = await recovery.RecoverAsync(scheduler, pendingIndex);

        Assert.Equal(1, recovered);
        Assert.Equal(1, pendingIndex.Count);
        Assert.Equal(0, scheduler.Count);
    }

    [Fact]
    public async Task RecoverAsync_TransientOverdue_GoesDirectlyToQueue()
    {
        await WriteMessageWithRecipientsAsync((RecipientStatus.Transient, DateTimeOffset.UtcNow.AddMinutes(-10), 3));

        var recovery = new SpoolBootRecovery(_fixture.Scanner);
        var scheduler = new RecordingScheduler();
        var pendingIndex = new PendingRetryIndex();

        await recovery.RecoverAsync(scheduler, pendingIndex);

        Assert.Equal(0, pendingIndex.Count);
        var item = Assert.Single(scheduler.Items);
        Assert.Equal(3, item.AttemptCount);
    }

    [Fact]
    public async Task RecoverAsync_InFlight_TreatedAsImmediatelyRetryable()
    {
        // simula crash durante a tentativa de entrega: o worker nunca chegou a persistir um status terminal
        await WriteMessageWithRecipientsAsync((RecipientStatus.InFlight, DateTimeOffset.UtcNow.AddHours(2), 1));

        var recovery = new SpoolBootRecovery(_fixture.Scanner);
        var scheduler = new RecordingScheduler();
        var pendingIndex = new PendingRetryIndex();

        await recovery.RecoverAsync(scheduler, pendingIndex);

        Assert.Equal(0, pendingIndex.Count);
        Assert.Equal(1, scheduler.Count); // destinatário InFlight interrompido por crash deveria voltar a ser elegível para retry imediato
    }

    [Theory]
    [InlineData(RecipientStatus.Delivered)]
    [InlineData(RecipientStatus.Bounced)]
    [InlineData(RecipientStatus.Suppressed)]
    [InlineData(RecipientStatus.Paused)]
    public async Task RecoverAsync_TerminalStatuses_AreSkipped(RecipientStatus terminalStatus)
    {
        await WriteMessageWithRecipientsAsync((terminalStatus, DateTimeOffset.UtcNow.AddMinutes(-1), 1));

        var recovery = new SpoolBootRecovery(_fixture.Scanner);
        var scheduler = new RecordingScheduler();
        var pendingIndex = new PendingRetryIndex();

        var recovered = await recovery.RecoverAsync(scheduler, pendingIndex);

        Assert.Equal(0, recovered);
        Assert.Equal(0, pendingIndex.Count);
        Assert.Equal(0, scheduler.Count);
    }

    [Fact]
    public async Task RecoverAsync_MixOfStatusesAcrossOneMessage_DistributesCorrectly()
    {
        await WriteMessageWithRecipientsAsync(
            (RecipientStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(-1), 0),     // -> queue
            (RecipientStatus.Transient, DateTimeOffset.UtcNow.AddHours(1), 1),      // -> pendingIndex
            (RecipientStatus.Delivered, DateTimeOffset.UtcNow.AddMinutes(-1), 1),   // -> skip
            (RecipientStatus.Bounced, DateTimeOffset.UtcNow.AddMinutes(-1), 1));    // -> skip

        var recovery = new SpoolBootRecovery(_fixture.Scanner);
        var scheduler = new RecordingScheduler();
        var pendingIndex = new PendingRetryIndex();

        var recovered = await recovery.RecoverAsync(scheduler, pendingIndex);

        Assert.Equal(2, recovered);
        Assert.Equal(1, pendingIndex.Count);
        Assert.Equal(1, scheduler.Count);
    }
}
