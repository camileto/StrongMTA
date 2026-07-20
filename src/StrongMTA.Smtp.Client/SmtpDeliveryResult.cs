namespace StrongMTA.Smtp.Client;

public enum SmtpDeliveryOutcome
{
    Delivered,
    Bounced,
    Transient,

    /// <summary>Falha antes de qualquer código SMTP ser obtido (timeout, conexão recusada, DNS, TLS).</summary>
    ConnectionFailed,

    /// <summary>Nenhuma tentativa de conexão foi feita — decidido antes de abrir o socket (ex.: destinatário pausado).</summary>
    Skipped
}

public sealed class SmtpDeliveryResult
{
    public required SmtpDeliveryOutcome Outcome { get; init; }
    public int? SmtpCode { get; init; }
    public string? ResponseText { get; init; }
    public string? ErrorDetail { get; init; }
    public bool UsedStartTls { get; init; }

    public static SmtpDeliveryResult FromResponse(SmtpDeliveryOutcome outcome, SmtpResponse response, bool usedStartTls) => new()
    {
        Outcome = outcome,
        SmtpCode = response.Code,
        ResponseText = response.Text,
        UsedStartTls = usedStartTls
    };

    public static SmtpDeliveryResult ConnectionFailure(string errorDetail) => new()
    {
        Outcome = SmtpDeliveryOutcome.ConnectionFailed,
        ErrorDetail = errorDetail
    };
}
