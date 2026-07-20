using StrongMTA.Core;
using StrongMTA.Spool;

namespace StrongMTA.Engine;

/// <summary>
/// Reconstrói a fila in-memory a partir do spool no boot do daemon: para cada destinatário
/// em estado não-terminal, reenfileira para entrega imediata (se já venceu o NextAttemptAt)
/// ou agenda no <see cref="PendingRetryIndex"/>. Destinatários InFlight (interrompidos por um
/// crash no meio da entrega) voltam a ser elegíveis para retry imediato — preferimos o risco
/// de uma entrega duplicada ao risco de perder a mensagem.
/// </summary>
public sealed class SpoolBootRecovery(SpoolScanner scanner)
{
    public async Task<int> RecoverAsync(
        IDeliveryScheduler scheduler,
        PendingRetryIndex pendingRetryIndex,
        CancellationToken cancellationToken = default)
    {
        var recoveredCount = 0;

        await foreach (var record in scanner.ScanAsync(cancellationToken))
        {
            var recipientsByid = record.Envelope.Recipients.ToDictionary(r => r.RecipientId);

            foreach (var recipientState in record.State.Recipients)
            {
                if (ShouldSkipOnRecovery(recipientState.Status))
                    continue;

                if (!recipientsByid.TryGetValue(recipientState.RecipientId, out var recipientEnvelope))
                    continue; // inconsistência defensiva: não deveria ocorrer com escrita atômica

                var now = DateTimeOffset.UtcNow;
                var nextAttemptAt = recipientState.Status == RecipientStatus.InFlight ? now : recipientState.NextAttemptAt;

                var item = new RecipientWorkItem
                {
                    MessageId = record.Envelope.MessageId,
                    RecipientId = recipientEnvelope.RecipientId,
                    MsgFilePath = record.MsgFilePath,
                    StateFilePath = record.StateFilePath,
                    EnvelopeFrom = recipientEnvelope.EnvelopeFrom,
                    RecipientAddress = recipientEnvelope.Address,
                    DestinationDomain = recipientEnvelope.DestinationDomain,
                    VirtualMtaName = recipientEnvelope.VirtualMtaName,
                    SubmittedAt = record.Envelope.SubmittedAt,
                    AttemptCount = recipientState.AttemptCount,
                    NextAttemptAt = nextAttemptAt
                };

                recoveredCount++;
                if (nextAttemptAt <= now)
                    scheduler.Enqueue(item);
                else
                    pendingRetryIndex.Add(item);
            }
        }

        return recoveredCount;
    }

    /// <summary>Terminal de fato (Delivered/Bounced/Expired/Suppressed) ou pausado administrativamente — em ambos os casos não deve voltar à fila no boot.</summary>
    private static bool ShouldSkipOnRecovery(RecipientStatus status) =>
        status is RecipientStatus.Delivered or RecipientStatus.Bounced or RecipientStatus.Expired or RecipientStatus.Suppressed or RecipientStatus.Paused;
}
