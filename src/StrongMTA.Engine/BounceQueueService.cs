using StrongMTA.Accounting;
using StrongMTA.Core;
using StrongMTA.Spool;

namespace StrongMTA.Engine;

/// <summary>
/// Bounça em massa todos os destinatários pendentes (Pending/Transient/InFlight) de um
/// domínio — equivalente ao <c>bounce-queue</c> do PowerMTA, disparado por uma
/// <see cref="ResponseRule"/>. Operação administrativa rara: percorre o spool inteiro
/// (O(tamanho total do spool), mesmo caveat já aceito pelos comandos <c>queue pause/resume</c>
/// da CLI), por isso sempre é chamada em fire-and-forget pelo worker, nunca bloqueando a
/// entrega que a disparou. Nunca propaga exceção: um registro problemático não deve abortar
/// o restante do bounce em massa.
/// </summary>
public sealed class BounceQueueService(SpoolScanner scanner, SpoolStateUpdater stateUpdater, IAccountingSink accountingSink)
{
    public async Task<int> BounceQueueAsync(string destinationDomain, CancellationToken cancellationToken = default)
    {
        var count = 0;

        await foreach (var record in scanner.ScanAsync(cancellationToken))
        {
            var recipientIdsInDomain = record.Envelope.Recipients
                .Where(r => string.Equals(r.DestinationDomain, destinationDomain, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.RecipientId)
                .ToHashSet();

            if (recipientIdsInDomain.Count == 0)
                continue;

            foreach (var recipientState in record.State.Recipients)
            {
                if (!recipientIdsInDomain.Contains(recipientState.RecipientId))
                    continue;
                if (recipientState.Status is not (RecipientStatus.Pending or RecipientStatus.Transient or RecipientStatus.InFlight))
                    continue;

                try
                {
                    await stateUpdater.UpdateRecipientAsync(record.Envelope.MessageId, recipientState.RecipientId, recipient =>
                    {
                        recipient.Status = RecipientStatus.Bounced;
                        recipient.LastSmtpResponse = "Bounçado via bounce-queue (regra de resposta SMTP).";
                    }, cancellationToken).ConfigureAwait(false);

                    await accountingSink.RecordAsync(new AccountingEvent
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        Type = AccountingEventType.Bounced,
                        MessageId = record.Envelope.MessageId,
                        RecipientId = recipientState.RecipientId,
                        DestinationDomain = destinationDomain,
                        SmtpResponseText = "bounce-queue"
                    }, cancellationToken).ConfigureAwait(false);

                    count++;
                }
                catch
                {
                    // best-effort — segue para o próximo registro
                }
            }
        }

        return count;
    }
}
