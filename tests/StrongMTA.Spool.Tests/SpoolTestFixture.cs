using StrongMTA.Core;

namespace StrongMTA.Spool.Tests;

/// <summary>Diretório temporário isolado por teste, removido ao final.</summary>
public sealed class SpoolTestFixture : IDisposable
{
    public string RootDirectory { get; } = Path.Combine(Path.GetTempPath(), "strongmta-spool-tests-" + Guid.NewGuid().ToString("N"));
    public SpoolPaths Paths { get; }
    public SpoolWriter Writer { get; }
    public SpoolReader Reader { get; } = new();

    public SpoolTestFixture()
    {
        Paths = new SpoolPaths(RootDirectory);
        Writer = new SpoolWriter(Paths);
    }

    public static MessageEnvelopeData CreateEnvelope(Guid? messageId = null, params Guid[] recipientIds)
    {
        var msgId = messageId ?? Guid.NewGuid();
        var recipients = (recipientIds.Length == 0 ? [Guid.NewGuid()] : recipientIds)
            .Select(id => new RecipientEnvelopeData
            {
                RecipientId = id,
                Address = $"user-{id:N}@example.com",
                EnvelopeFrom = $"bounce-{id:N}@strongmta.test",
                DestinationDomain = "example.com",
                VirtualMtaName = "vmta-01"
            })
            .ToList();

        return new MessageEnvelopeData
        {
            MessageId = msgId,
            JobId = "job-1",
            SubmittedAt = DateTimeOffset.UtcNow,
            Recipients = recipients
        };
    }

    public static MessageStateData CreateDefaultState(MessageEnvelopeData envelope) => new()
    {
        MessageId = envelope.MessageId,
        Recipients = envelope.Recipients.Select(r => new RecipientStateData
        {
            RecipientId = r.RecipientId,
            NextAttemptAt = envelope.SubmittedAt
        }).ToList()
    };

    public void Dispose()
    {
        if (Directory.Exists(RootDirectory))
        {
            try { Directory.Delete(RootDirectory, recursive: true); }
            catch (IOException) { }
        }
    }
}
