using StrongMTA.Core;

namespace StrongMTA.Spool;

/// <summary>
/// Cabeçalho imutável persistido no arquivo .msg (junto com o corpo RFC822). Nunca é
/// reescrito após criado — distinto do estado mutável por destinatário, que vive no .state.
/// </summary>
public sealed class MessageEnvelopeData
{
    public required Guid MessageId { get; init; }
    public string? JobId { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public required IReadOnlyList<RecipientEnvelopeData> Recipients { get; init; }
}

/// <summary>
/// EnvelopeFrom é por destinatário (não por mensagem): é o endereço VERP usado como MAIL FROM
/// na entrega desse destinatário específico, permitindo correlacionar um bounce/FBL recebido
/// de volta a este RecipientId sem depender do corpo do DSN/ARF conter o endereço original.
/// </summary>
public sealed class RecipientEnvelopeData
{
    public required Guid RecipientId { get; init; }
    public required string Address { get; init; }
    public required string EnvelopeFrom { get; init; }
    public required string DestinationDomain { get; init; }
    public required string VirtualMtaName { get; init; }
}

/// <summary>Estado mutável de todos os destinatários de uma mensagem — arquivo .state, reescrito a cada tentativa.</summary>
public sealed class MessageStateData
{
    public int Version { get; init; } = 1;
    public required Guid MessageId { get; init; }
    public required List<RecipientStateData> Recipients { get; init; }
}

public sealed class RecipientStateData
{
    public required Guid RecipientId { get; init; }
    public RecipientStatus Status { get; set; } = RecipientStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastSmtpResponse { get; set; }
}
