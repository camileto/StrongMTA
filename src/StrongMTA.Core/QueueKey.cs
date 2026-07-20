namespace StrongMTA.Core;

/// <summary>
/// Chave de particionamento de toda a engine: rate limiting, estado de backoff e
/// workers de entrega são indexados por (domínio destino, VirtualMta).
/// </summary>
public sealed class QueueKey : IEquatable<QueueKey>
{
    public required string DestinationDomain { get; init; }
    public required string VirtualMtaName { get; init; }

    public bool Equals(QueueKey? other)
    {
        if (other is null) return false;
        return string.Equals(DestinationDomain, other.DestinationDomain, StringComparison.OrdinalIgnoreCase)
            && string.Equals(VirtualMtaName, other.VirtualMtaName, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => Equals(obj as QueueKey);

    public override int GetHashCode() =>
        HashCode.Combine(DestinationDomain.ToLowerInvariant(), VirtualMtaName);

    public override string ToString() => $"{DestinationDomain}/{VirtualMtaName}";
}

public enum QueueState
{
    Normal,
    Backoff
}

/// <summary>
/// Estado em memória de uma fila (domínio, vmta). É derivado/reconstruído a partir
/// do spool no boot — não é a fonte de verdade durável por si só.
/// </summary>
public sealed class QueueRuntimeState
{
    public required QueueKey Key { get; init; }
    public QueueState State { get; set; } = QueueState.Normal;
    public DateTimeOffset? BackoffUntil { get; set; }
    public int InFlightConnections { get; set; }
    public int ConsecutiveConnectionFailures { get; set; }
}
