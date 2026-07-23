namespace StrongMTA.Core;

public sealed class SpoolPurgeOptions
{
    public required TimeSpan PollInterval { get; init; }

    /// <summary>Tempo mínimo que um arquivo permanece no spool após todos os seus destinatários atingirem status terminal antes de ser elegível para deleção.</summary>
    public required TimeSpan RetainAfterTerminal { get; init; }

    public static SpoolPurgeOptions CreateDefault() => new()
    {
        PollInterval = TimeSpan.FromHours(1),
        RetainAfterTerminal = TimeSpan.FromHours(24)
    };
}
