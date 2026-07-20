namespace StrongMTA.Core;

public enum AccountingEventType
{
    Received,
    Delivered,
    Bounced,
    Transient,
    RemoteBounce,
    RemoteFeedback,

    /// <summary>TTL (BounceAfter) esgotado sem veredito explícito de permanente do remoto — distinto de Bounced.</summary>
    Expired
}

/// <summary>
/// Taxonomia simplificada de categorias de bounce (subconjunto das ~16 categorias
/// do PowerMTA), calculada via regex configurável sobre a resposta/diagnóstico SMTP.
/// </summary>
public enum BounceCategory
{
    BadMailbox,
    SpamRelated,
    QuotaIssues,
    PolicyRelated,
    BadConnection,
    Other
}

/// <summary>
/// Registro de evento de accounting. Um por destinatário por transição relevante
/// de estado — escrito em arquivo JSONL append-only, nunca reescrito.
/// </summary>
public sealed class AccountingEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public required AccountingEventType Type { get; init; }
    public required Guid MessageId { get; init; }
    public Guid? RecipientId { get; init; }
    public string? JobId { get; init; }
    public string? VirtualMtaName { get; init; }
    public string? DestinationDomain { get; init; }
    public int? SmtpCode { get; init; }
    public string? SmtpResponseText { get; init; }
    public BounceCategory? Category { get; init; }
}

/// <summary>Resultado da análise de um DSN (RFC 3464) ou ARF (RFC 5965) recebido no inbound.</summary>
public sealed class BounceRecord
{
    public required Guid RecipientId { get; init; }
    public required bool IsFeedbackLoop { get; init; }
    public string? DiagnosticCode { get; init; }
    public required BounceCategory Category { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
}
