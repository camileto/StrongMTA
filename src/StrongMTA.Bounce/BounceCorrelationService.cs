using MimeKit;
using StrongMTA.Accounting;
using StrongMTA.Core;
using StrongMTA.Spool;

namespace StrongMTA.Bounce;

/// <summary>
/// Ponto único de correlação para mensagens de bounce/FBL recebidas: resolve o token VERP
/// contra o <see cref="BounceTokenStore"/>, atualiza o .state (apenas para DSN com Action=failed
/// — o resto é só informativo) e emite o <see cref="AccountingEvent"/> correspondente. Nunca
/// lança por causa de correlação ausente/malformada — o chamador (listener SMTP) sempre deve
/// responder 250 ao remoto independente do resultado, então aqui só retornamos bool/logamos.
/// </summary>
public sealed class BounceCorrelationService(
    BounceTokenStore bounceTokenStore,
    SpoolStateUpdater stateUpdater,
    IAccountingSink accountingSink,
    BounceCategoryClassifier categoryClassifier,
    SpoolPaths paths,
    SpoolReader spoolReader,
    IDomainConfigProvider domainConfigProvider,
    ResponseRuleEngine ruleEngine)
{
    /// <summary>
    /// Processa um DSN cujo RecipientId já foi identificado pelo listener a partir do RCPT TO
    /// (<c>bounce-&lt;recipientId&gt;@bounceDomain</c>) — não precisamos extrair nada do corpo
    /// para a correlação, só para a categoria/diagnóstico.
    /// </summary>
    public async Task<bool> ProcessDsnAsync(Guid recipientId, MimeMessage dsnMessage, CancellationToken cancellationToken = default)
    {
        var messageId = await bounceTokenStore.ResolveMessageIdAsync(recipientId, cancellationToken).ConfigureAwait(false);
        if (messageId is null)
            return false;

        var parsed = DsnReportParser.TryParse(dsnMessage);
        if (parsed is null || parsed.Action != "failed")
            return true; // delayed/relayed/delivered: notificação informativa, sem mudança de estado

        var category = categoryClassifier.Classify(parsed.Status, parsed.DiagnosticCode);
        var diagnosticText = parsed.DiagnosticCode ?? parsed.Status;
        var newStatus = await DecideStatusAsync(messageId.Value, recipientId, parsed.Status, diagnosticText, cancellationToken).ConfigureAwait(false);

        await stateUpdater.UpdateRecipientAsync(messageId.Value, recipientId, recipient =>
        {
            recipient.Status = newStatus;
            recipient.LastAttemptAt = DateTimeOffset.UtcNow;
            recipient.LastSmtpResponse = diagnosticText;
        }, cancellationToken).ConfigureAwait(false);

        await accountingSink.RecordAsync(new AccountingEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Type = AccountingEventType.RemoteBounce,
            MessageId = messageId.Value,
            RecipientId = recipientId,
            SmtpResponseText = diagnosticText,
            Category = category
        }, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Bounced exige veredito explícito de permanente: regra ForceBounce, ou Status do DSN
    /// começando em "5". Status "4.x.x" (ou ausente) vira Expired — é uma falha temporária que
    /// o remoto decidiu (ou foi configurado a) desistir de tentar, não um veredito nosso.
    /// </summary>
    private async Task<RecipientStatus> DecideStatusAsync(
        Guid messageId, Guid recipientId, string? dsnStatus, string? diagnosticText, CancellationToken cancellationToken)
    {
        var domain = await ResolveDestinationDomainAsync(messageId, recipientId, cancellationToken).ConfigureAwait(false);
        var rules = domain is null ? Array.Empty<ResponseRule>() : domainConfigProvider.GetConfig(domain).ResponseRules;
        var matchedRule = ruleEngine.Evaluate(rules, diagnosticText);

        if (matchedRule?.Has(ResponseRuleAction.ForceBounce) == true)
            return RecipientStatus.Bounced;
        if (matchedRule?.Has(ResponseRuleAction.ForceExpire) == true)
            return RecipientStatus.Expired;

        return dsnStatus is not null && dsnStatus.StartsWith('5') ? RecipientStatus.Bounced : RecipientStatus.Expired;
    }

    private async Task<string?> ResolveDestinationDomainAsync(Guid messageId, Guid recipientId, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await spoolReader.ReadEnvelopeAsync(paths.GetMsgFilePath(messageId), cancellationToken).ConfigureAwait(false);
            return envelope.Recipients.FirstOrDefault(r => r.RecipientId == recipientId)?.DestinationDomain;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    /// Processa um ARF recebido num endereço fixo de FBL — aqui SIM precisamos extrair o token
    /// de dentro do corpo (campo Original-Mail-From), porque o RCPT TO do relatório não é o
    /// nosso VERP, é o endereço de feed configurado com o provedor.
    /// </summary>
    public async Task<bool> ProcessArfAsync(MimeMessage arfMessage, CancellationToken cancellationToken = default)
    {
        var parsed = ArfReportParser.TryParse(arfMessage);
        if (parsed is null)
            return false;

        if (!VerpToken.TryExtractRecipientId(parsed.OriginalMailFrom, out var recipientId))
            return false;

        var messageId = await bounceTokenStore.ResolveMessageIdAsync(recipientId, cancellationToken).ConfigureAwait(false);
        if (messageId is null)
            return false;

        var category = parsed.FeedbackType is "abuse" or "fraud" ? BounceCategory.SpamRelated : BounceCategory.Other;

        await accountingSink.RecordAsync(new AccountingEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Type = AccountingEventType.RemoteFeedback,
            MessageId = messageId.Value,
            RecipientId = recipientId,
            SmtpResponseText = parsed.FeedbackType,
            Category = category
        }, cancellationToken).ConfigureAwait(false);

        return true;
    }
}
