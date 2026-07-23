using System.Text;
using StrongMTA.Core;
using StrongMTA.Spool;

namespace StrongMTA.Engine.Tests;

public class SpoolPurgeServiceTests : IDisposable
{
    private readonly EngineTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private SpoolPurgeService CreateService(TimeSpan retainAfterTerminal) =>
        new(_fixture.Scanner, new SpoolPurgeOptions
        {
            PollInterval = TimeSpan.FromHours(1),
            RetainAfterTerminal = retainAfterTerminal
        });

    private async Task<Guid> WriteMessageWithStatusAsync(RecipientStatus status, DateTimeOffset lastAttemptAt)
    {
        var msgId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();

        var envelope = new MessageEnvelopeData
        {
            MessageId = msgId,
            SubmittedAt = DateTimeOffset.UtcNow.AddHours(-48),
            Recipients = [new RecipientEnvelopeData
            {
                RecipientId = recipientId,
                Address = "r@example.com",
                EnvelopeFrom = "bounce@strongmta.test",
                DestinationDomain = "example.com",
                VirtualMtaName = "vmta-01"
            }]
        };

        using var body = new MemoryStream(Encoding.UTF8.GetBytes("body"));
        await _fixture.Writer.WriteMessageAsync(envelope, body);

        var state = new MessageStateData
        {
            MessageId = msgId,
            Recipients = [new RecipientStateData
            {
                RecipientId = recipientId,
                Status = status,
                LastAttemptAt = lastAttemptAt
            }]
        };
        await _fixture.Writer.WriteStateAsync(state);

        return msgId;
    }

    [Fact]
    public async Task PurgeOnceAsync_OldTerminalMessages_DeletesBothFiles()
    {
        var service = CreateService(TimeSpan.FromHours(24));
        var msgId = await WriteMessageWithStatusAsync(RecipientStatus.Delivered, DateTimeOffset.UtcNow.AddHours(-48));

        var purged = await service.PurgeOnceAsync();

        Assert.Equal(1, purged);
        Assert.False(File.Exists(_fixture.Paths.GetMsgFilePath(msgId)));
        Assert.False(File.Exists(_fixture.Paths.GetStateFilePath(msgId)));
    }

    [Fact]
    public async Task PurgeOnceAsync_RecentTerminalMessages_KeepsBothFiles()
    {
        var service = CreateService(TimeSpan.FromHours(24));
        var msgId = await WriteMessageWithStatusAsync(RecipientStatus.Bounced, DateTimeOffset.UtcNow.AddMinutes(-30));

        var purged = await service.PurgeOnceAsync();

        Assert.Equal(0, purged);
        Assert.True(File.Exists(_fixture.Paths.GetMsgFilePath(msgId)));
        Assert.True(File.Exists(_fixture.Paths.GetStateFilePath(msgId)));
    }

    [Fact]
    public async Task PurgeOnceAsync_NonTerminalRecipient_PreservesFilesRegardlessOfAge()
    {
        var service = CreateService(TimeSpan.FromHours(24));
        var msgId = await WriteMessageWithStatusAsync(RecipientStatus.Transient, DateTimeOffset.UtcNow.AddHours(-72));

        var purged = await service.PurgeOnceAsync();

        Assert.Equal(0, purged);
        Assert.True(File.Exists(_fixture.Paths.GetMsgFilePath(msgId)));
        Assert.True(File.Exists(_fixture.Paths.GetStateFilePath(msgId)));
    }
}
