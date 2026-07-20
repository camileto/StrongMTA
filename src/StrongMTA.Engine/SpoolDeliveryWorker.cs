using StrongMTA.Accounting;
using StrongMTA.Bounce;
using StrongMTA.Core;
using StrongMTA.Smtp.Client;
using StrongMTA.Spool;

namespace StrongMTA.Engine;

/// <summary>
/// Consome a fila e tenta entregar cada destinatário. Ao final de cada tentativa,
/// atualiza o .state no spool (via <see cref="SpoolStateUpdater"/>, seguro para
/// concorrência entre destinatários da mesma mensagem) e decide entre marcar estado
/// terminal (Delivered/Bounced/Expired) ou reagendar via <see cref="PendingRetryIndex"/>,
/// respeitando o TTL de bounce-after por domínio. As <see cref="ResponseRule"/> configuradas
/// no domínio (via <see cref="DomainConfig.ResponseRules"/>) podem sobrepor essa classificação
/// default — lista vazia (o padrão) significa nenhuma regra e nenhuma mudança de comportamento.
/// </summary>
public sealed class SpoolDeliveryWorker(
    SpoolReader spoolReader,
    SpoolStateUpdater stateUpdater,
    IMxResolver mxResolver,
    IAccountingSink accountingSink,
    IDomainConfigProvider domainConfigProvider,
    IVirtualMtaProvider virtualMtaProvider,
    PendingRetryIndex pendingRetryIndex,
    ResponseRuleEngine ruleEngine,
    BackoffStateStore backoffStateStore,
    DisabledSourceStore disabledSourceStore,
    BounceQueueService bounceQueueService,
    int smtpPort = 25)
{
    public async Task<SmtpDeliveryResult> DeliverOneAsync(RecipientWorkItem item, CancellationToken cancellationToken)
    {
        // checagem de pausa ANTES de qualquer outra coisa: um operador pode ter pausado este
        // destinatário (CLI, por JobId) entre o enfileiramento e esta tentativa — não tocamos
        // o estado nem abrimos conexão alguma se for o caso.
        var currentState = await spoolReader.ReadStateAsync(item.StateFilePath, cancellationToken).ConfigureAwait(false);
        var currentRecipient = currentState?.Recipients.FirstOrDefault(r => r.RecipientId == item.RecipientId);
        if (currentRecipient?.Status == RecipientStatus.Paused)
            return new SmtpDeliveryResult { Outcome = SmtpDeliveryOutcome.Skipped };

        var virtualMta = virtualMtaProvider.GetVirtualMta(item.VirtualMtaName);

        // disable-source-ip (regra de resposta SMTP): mesmo tratamento de uma falha de conexão
        // normal, sem nem tentar abrir socket — decisão administrativa nossa, vai pro caminho
        // Transient/retry já existente em ApplyResultAsync.
        if (await disabledSourceStore.IsDisabledAsync(virtualMta.Name, cancellationToken).ConfigureAwait(false))
        {
            var disabledResult = SmtpDeliveryResult.ConnectionFailure(
                $"VirtualMta '{virtualMta.Name}' temporariamente desabilitado (disable-source-ip).");
            await ApplyResultAsync(item, disabledResult, matchedRule: null, cancellationToken).ConfigureAwait(false);
            return disabledResult;
        }

        // marca InFlight ANTES de conectar: se o processo morrer durante a tentativa, o boot
        // seguinte enxerga esse status (em vez do Pending/Transient anterior, que poderia ter
        // um NextAttemptAt no futuro) e sabe que deve tornar o destinatário retryable de imediato.
        await stateUpdater.UpdateRecipientAsync(item.MessageId, item.RecipientId, recipient =>
        {
            recipient.Status = RecipientStatus.InFlight;
            recipient.LastAttemptAt = DateTimeOffset.UtcNow;
        }, cancellationToken).ConfigureAwait(false);

        var mxHosts = await mxResolver.ResolveAsync(item.DestinationDomain, cancellationToken).ConfigureAwait(false);
        var domainConfig = domainConfigProvider.GetConfig(item.DestinationDomain);

        SmtpDeliveryResult result;
        ResponseRule? matchedRule;
        var hostIndex = 0;
        while (true)
        {
            var request = new SmtpDeliveryRequest
            {
                TargetHost = mxHosts[hostIndex].HostName,
                TargetPort = smtpPort,
                HeloHostName = virtualMta.HostName,
                LocalIpAddress = virtualMta.SourceIp,
                EnvelopeFrom = item.EnvelopeFrom,
                RecipientAddress = item.RecipientAddress,
                OpenBodyStream = ct => spoolReader.OpenBodyStreamAsync(item.MsgFilePath, ct)
            };

            result = await new SmtpDeliveryClient().SendAsync(request, cancellationToken).ConfigureAwait(false);
            matchedRule = ruleEngine.Evaluate(domainConfig.ResponseRules, result.ResponseText ?? result.ErrorDetail);

            // skip-mx: tenta o próximo MX dentro da MESMA tentativa (sem round-trip pela fila
            // de retry) — só continua o loop se ainda houver outro host candidato.
            var skipToNextHost = matchedRule?.Has(ResponseRuleAction.SkipMx) == true && hostIndex + 1 < mxHosts.Count;
            if (!skipToNextHost)
                break;

            hostIndex++;
        }

        await ApplyResultAsync(item, result, matchedRule, cancellationToken).ConfigureAwait(false);

        return result;
    }

    private async Task ApplyResultAsync(RecipientWorkItem item, SmtpDeliveryResult result, ResponseRule? matchedRule, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var responseText = result.ResponseText ?? result.ErrorDetail;
        var domainConfig = domainConfigProvider.GetConfig(item.DestinationDomain);
        var queueKey = new QueueKey { DestinationDomain = item.DestinationDomain, VirtualMtaName = item.VirtualMtaName };

        RecipientStatus newStatus;
        var newAttemptCount = item.AttemptCount;
        var newNextAttemptAt = item.NextAttemptAt;
        var shouldRetry = false;

        if (matchedRule?.Has(ResponseRuleAction.ForceBounce) == true)
        {
            newStatus = RecipientStatus.Bounced;
        }
        else if (matchedRule?.Has(ResponseRuleAction.ForceExpire) == true)
        {
            newStatus = RecipientStatus.Expired;
        }
        else if (matchedRule?.Has(ResponseRuleAction.ForceRetry) == true)
        {
            newAttemptCount++;
            (newStatus, newNextAttemptAt, shouldRetry, responseText) =
                await DecideRetryOrExpireAsync(item, domainConfig, queueKey, newAttemptCount, now, responseText, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            switch (result.Outcome)
            {
                case SmtpDeliveryOutcome.Delivered:
                    newStatus = RecipientStatus.Delivered;
                    break;

                case SmtpDeliveryOutcome.Bounced:
                    newStatus = RecipientStatus.Bounced;
                    break;

                default: // Transient ou ConnectionFailed
                    newAttemptCount++;
                    (newStatus, newNextAttemptAt, shouldRetry, responseText) =
                        await DecideRetryOrExpireAsync(item, domainConfig, queueKey, newAttemptCount, now, responseText, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        await stateUpdater.UpdateRecipientAsync(item.MessageId, item.RecipientId, recipient =>
        {
            recipient.Status = newStatus;
            recipient.AttemptCount = newAttemptCount;
            recipient.NextAttemptAt = newNextAttemptAt;
            recipient.LastAttemptAt = now;
            recipient.LastSmtpResponse = responseText;
        }, cancellationToken).ConfigureAwait(false);

        if (shouldRetry)
        {
            item.AttemptCount = newAttemptCount;
            item.NextAttemptAt = newNextAttemptAt;
            pendingRetryIndex.Add(item);
        }

        await accountingSink.RecordAsync(new AccountingEvent
        {
            Timestamp = now,
            Type = ToEventType(newStatus),
            MessageId = item.MessageId,
            RecipientId = item.RecipientId,
            DestinationDomain = item.DestinationDomain,
            VirtualMtaName = item.VirtualMtaName,
            SmtpCode = result.SmtpCode,
            SmtpResponseText = responseText
        }, cancellationToken).ConfigureAwait(false);

        if (matchedRule is not null)
            await ApplySideEffectsAsync(matchedRule, queueKey, item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>TTL (BounceAfter) esgotado → Expired (decisão nossa, sem veredito explícito do remoto). Senão, agenda retry usando o intervalo normal ou de backoff conforme o estado atual da fila.</summary>
    private async Task<(RecipientStatus Status, DateTimeOffset NextAttemptAt, bool ShouldRetry, string? ResponseText)> DecideRetryOrExpireAsync(
        RecipientWorkItem item, DomainConfig domainConfig, QueueKey queueKey, int attemptNumber, DateTimeOffset now, string? responseText, CancellationToken cancellationToken)
    {
        if (now - item.SubmittedAt >= domainConfig.BounceAfter)
        {
            return (RecipientStatus.Expired, item.NextAttemptAt, false,
                $"Excedeu bounce-after ({domainConfig.BounceAfter}) na fila. Última resposta: {responseText}");
        }

        var inBackoff = await backoffStateStore.IsInBackoffAsync(queueKey, cancellationToken).ConfigureAwait(false);
        var interval = inBackoff ? domainConfig.GetBackoffRetryInterval(attemptNumber) : domainConfig.GetRetryInterval(attemptNumber);
        return (RecipientStatus.Transient, now + interval, true, responseText);
    }

    private async Task ApplySideEffectsAsync(ResponseRule rule, QueueKey queueKey, RecipientWorkItem item, CancellationToken cancellationToken)
    {
        if (rule.Has(ResponseRuleAction.EnterBackoff))
            await backoffStateStore.EnterBackoffAsync(queueKey, rule.BackoffToNormalAfter, cancellationToken).ConfigureAwait(false);

        if (rule.Has(ResponseRuleAction.ExitBackoff))
            await backoffStateStore.ExitBackoffAsync(queueKey, cancellationToken).ConfigureAwait(false);

        if (rule.Has(ResponseRuleAction.DisableSourceIp))
            await disabledSourceStore.DisableAsync(item.VirtualMtaName, rule.ReenableAfter, cancellationToken).ConfigureAwait(false);

        if (rule.Has(ResponseRuleAction.BounceQueue))
            _ = bounceQueueService.BounceQueueAsync(item.DestinationDomain, CancellationToken.None); // fire-and-forget: não bloqueia a escrita do .state deste destinatário
    }

    private static AccountingEventType ToEventType(RecipientStatus status) => status switch
    {
        RecipientStatus.Delivered => AccountingEventType.Delivered,
        RecipientStatus.Bounced => AccountingEventType.Bounced,
        RecipientStatus.Expired => AccountingEventType.Expired,
        _ => AccountingEventType.Transient
    };
}
