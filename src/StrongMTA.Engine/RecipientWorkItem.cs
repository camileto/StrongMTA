namespace StrongMTA.Engine;

/// <summary>
/// Unidade de trabalho de entrega: um destinatário de uma mensagem, referenciando os
/// arquivos .msg/.state no spool (não carrega o corpo em memória).
/// </summary>
public sealed class RecipientWorkItem
{
    public required Guid MessageId { get; init; }
    public required Guid RecipientId { get; init; }
    public required string MsgFilePath { get; init; }
    public required string StateFilePath { get; init; }
    public required string EnvelopeFrom { get; init; }
    public required string RecipientAddress { get; init; }
    public required string DestinationDomain { get; init; }
    /// <summary>VirtualMta já resolvido (quente ou frio, decidido pelo warm-up na submissão) — HELO/IP de origem são derivados dele na entrega.</summary>
    public required string VirtualMtaName { get; init; }

    /// <summary>Necessário para avaliar o TTL de bounce-after a cada nova tentativa.</summary>
    public required DateTimeOffset SubmittedAt { get; init; }

    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
}
