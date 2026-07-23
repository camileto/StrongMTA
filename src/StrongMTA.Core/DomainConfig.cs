namespace StrongMTA.Core;

/// <summary>
/// Política de entrega para um domínio de destino: limites de taxa, conexões e
/// a escada de retry/backoff até o bounce definitivo.
/// </summary>
public sealed class DomainConfig
{
    public required string DomainName { get; init; }
    /// <summary>Máximo de mensagens por minuto para esta fila (domínio × VirtualMta). Zero = sem limite (padrão).</summary>
public int MaxMessagesPerMinute { get; init; } = 0;
    public int MaxConcurrentConnections { get; init; } = 5;

    /// <summary>Intervalos progressivos de retry (ex: 10m, 30m, 1h, 4h, ...). O último repete até BounceAfter.</summary>
    public required IReadOnlyList<TimeSpan> RetryIntervals { get; init; }

    /// <summary>TTL máximo na fila antes de desistir (status Expired) por excesso de tentativas sem veredito explícito do remoto.</summary>
    public required TimeSpan BounceAfter { get; init; }

    /// <summary>Intervalos de retry usados enquanto a fila (domínio × VirtualMta) está em modo de backoff. Null = usa RetryIntervals normalmente.</summary>
    public IReadOnlyList<TimeSpan>? BackoffRetryIntervals { get; init; }

    /// <summary>Regras de override sobre o texto da resposta SMTP/diagnóstico de DSN — vazio (padrão) significa nenhuma regra e nenhuma mudança de comportamento.</summary>
    public IReadOnlyList<ResponseRule> ResponseRules { get; init; } = [];

    /// <summary>Retorna o intervalo de retry para a N-ésima tentativa (1-based), saturando no último valor da lista.</summary>
    public TimeSpan GetRetryInterval(int attemptNumber) => GetIntervalFrom(RetryIntervals, attemptNumber);

    /// <summary>Mesma lógica de GetRetryInterval, mas usando BackoffRetryIntervals (ou RetryIntervals se não configurado).</summary>
    public TimeSpan GetBackoffRetryInterval(int attemptNumber) => GetIntervalFrom(BackoffRetryIntervals ?? RetryIntervals, attemptNumber);

    private static TimeSpan GetIntervalFrom(IReadOnlyList<TimeSpan> intervals, int attemptNumber)
    {
        if (intervals.Count == 0)
            return TimeSpan.FromMinutes(30);

        var index = Math.Min(attemptNumber - 1, intervals.Count - 1);
        return intervals[Math.Max(index, 0)];
    }
}
