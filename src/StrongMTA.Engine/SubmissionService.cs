using StrongMTA.Accounting;
using StrongMTA.Bounce;
using StrongMTA.Core;
using StrongMTA.Dkim;
using StrongMTA.Spool;

namespace StrongMTA.Engine;

public sealed record SubmissionRecipient(string Address, string VirtualMtaName);

/// <summary>
/// Escreve uma nova mensagem no spool (.msg + .state inicial) e enfileira cada
/// destinatário para a primeira tentativa de entrega imediata. Resolve o warm-up
/// (quente/frio) e aplica a assinatura DKIM (por domínio remetente) antes de persistir —
/// ambas as decisões ficam fixas no envelope, não são reavaliadas em retries futuros.
/// Gera um EnvelopeFrom VERP (<c>bounce-&lt;recipientId&gt;@bounceDomain</c>) por destinatário
/// e registra o token no <see cref="BounceTokenStore"/>, permitindo correlacionar bounces/FBL
/// recebidos de volta sem depender do corpo do DSN/ARF.
/// </summary>
public sealed class SubmissionService(
    SpoolPaths paths,
    SpoolWriter writer,
    IDeliveryScheduler scheduler,
    IDkimSigningService dkimSigningService,
    IVirtualMtaProvider virtualMtaProvider,
    WarmupRouter warmupRouter,
    BounceTokenStore bounceTokenStore,
    IAccountingSink accountingSink,
    string bounceDomain)
{
    public async Task<Guid> SubmitAsync(
        string? jobId,
        IReadOnlyList<SubmissionRecipient> recipients,
        Func<CancellationToken, Task<Stream>> openBodyStream,
        CancellationToken cancellationToken = default)
    {
        if (recipients.Count == 0)
            throw new ArgumentException("É necessário ao menos um destinatário.", nameof(recipients));

        var messageId = Guid.NewGuid();
        var submittedAt = DateTimeOffset.UtcNow;

        var recipientEnvelopes = new List<RecipientEnvelopeData>(recipients.Count);
        foreach (var r in recipients)
        {
            var domain = ExtractDomain(r.Address);
            var requestedVmta = virtualMtaProvider.GetVirtualMta(r.VirtualMtaName);
            var actualVmtaName = await warmupRouter.ResolveVirtualMtaNameAsync(requestedVmta, domain, cancellationToken).ConfigureAwait(false);
            var recipientId = Guid.NewGuid();

            await bounceTokenStore.RegisterAsync(recipientId, messageId, cancellationToken).ConfigureAwait(false);

            recipientEnvelopes.Add(new RecipientEnvelopeData
            {
                RecipientId = recipientId,
                Address = r.Address,
                EnvelopeFrom = VerpToken.Format(recipientId, bounceDomain),
                DestinationDomain = domain,
                VirtualMtaName = actualVmtaName
            });
        }

        var envelope = new MessageEnvelopeData
        {
            MessageId = messageId,
            JobId = jobId,
            SubmittedAt = submittedAt,
            Recipients = recipientEnvelopes
        };

        string msgFilePath;
        await using (var rawBody = await openBodyStream(cancellationToken).ConfigureAwait(false))
        await using (var signedBody = await dkimSigningService.SignAsync(rawBody, cancellationToken).ConfigureAwait(false))
        {
            msgFilePath = await writer.WriteMessageAsync(envelope, signedBody, cancellationToken).ConfigureAwait(false);
        }

        var state = new MessageStateData
        {
            MessageId = messageId,
            Recipients = recipientEnvelopes.Select(r => new RecipientStateData
            {
                RecipientId = r.RecipientId,
                NextAttemptAt = submittedAt
            }).ToList()
        };
        await writer.WriteStateAsync(state, cancellationToken).ConfigureAwait(false);

        var stateFilePath = paths.GetStateFilePath(messageId);
        foreach (var r in recipientEnvelopes)
        {
            scheduler.Enqueue(new RecipientWorkItem
            {
                MessageId = messageId,
                RecipientId = r.RecipientId,
                MsgFilePath = msgFilePath,
                StateFilePath = stateFilePath,
                EnvelopeFrom = r.EnvelopeFrom,
                RecipientAddress = r.Address,
                DestinationDomain = r.DestinationDomain,
                VirtualMtaName = r.VirtualMtaName,
                SubmittedAt = submittedAt,
                AttemptCount = 0,
                NextAttemptAt = submittedAt
            });

            await accountingSink.RecordAsync(new AccountingEvent
            {
                Timestamp = submittedAt,
                Type = AccountingEventType.Received,
                MessageId = messageId,
                RecipientId = r.RecipientId,
                JobId = jobId,
                VirtualMtaName = r.VirtualMtaName,
                DestinationDomain = r.DestinationDomain
            }, cancellationToken).ConfigureAwait(false);
        }

        return messageId;
    }

    private static string ExtractDomain(string address)
    {
        var at = address.LastIndexOf('@');
        if (at < 0 || at == address.Length - 1)
            throw new ArgumentException($"Endereço de destinatário inválido: \"{address}\".", nameof(address));
        return address[(at + 1)..];
    }
}
