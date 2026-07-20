using System.Text.Json;

namespace StrongMTA.Spool;

internal sealed class WarmupCounterFileData
{
    public DateOnly Date { get; set; }
    public Dictionary<string, int> Counters { get; set; } = new();
}

/// <summary>
/// Persiste, em <c>cold/warmup-counters.json</c>, quantas mensagens já foram desviadas hoje
/// para o VirtualMta frio, por (VirtualMta quente, domínio destino). Reset de dia é "lazy":
/// avaliado no momento do uso, comparando contra a data armazenada — sobrevive a restart
/// sem precisar de um timer dedicado. Um único gate em memória serializa todo acesso ao
/// arquivo (compartilhado entre todos os pares vmta/domínio); aceitável dado o baixo volume
/// de escritas (uma por destinatário com warm-up habilitado, não por mensagem em geral).
/// </summary>
public sealed class WarmupCounterStore(SpoolPaths paths, Func<DateTimeOffset>? nowProvider = null)
{
    private readonly Func<DateTimeOffset> _now = nowProvider ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Tenta reservar uma "vaga" de desvio para o VirtualMta frio hoje. Retorna true (e
    /// incrementa o contador) se ainda não atingiu <paramref name="dailyLimit"/>; caso
    /// contrário retorna false sem incrementar — o chamador deve então usar o VirtualMta quente.
    /// </summary>
    public async Task<bool> TryReserveColdSlotAsync(string warmVmtaName, string domain, int dailyLimit, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var filePath = GetFilePath();
            var data = await ReadAsync(filePath, cancellationToken).ConfigureAwait(false);
            data = ResetIfNewDay(data);

            var key = BuildKey(warmVmtaName, domain);
            data.Counters.TryGetValue(key, out var current);

            if (current >= dailyLimit)
            {
                await WriteAsync(filePath, data, cancellationToken).ConfigureAwait(false);
                return false;
            }

            data.Counters[key] = current + 1;
            await WriteAsync(filePath, data, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Contagem atual (do dia corrente) de desvios para o VirtualMta frio — útil para CLI/monitoramento.</summary>
    public async Task<int> GetCountAsync(string warmVmtaName, string domain, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = ResetIfNewDay(await ReadAsync(GetFilePath(), cancellationToken).ConfigureAwait(false));
            return data.Counters.GetValueOrDefault(BuildKey(warmVmtaName, domain));
        }
        finally
        {
            _gate.Release();
        }
    }

    private WarmupCounterFileData ResetIfNewDay(WarmupCounterFileData data)
    {
        var today = DateOnly.FromDateTime(_now().UtcDateTime);
        return data.Date == today ? data : new WarmupCounterFileData { Date = today, Counters = new() };
    }

    private string GetFilePath() => Path.Combine(paths.ColdDirectory, "warmup-counters.json");

    private static string BuildKey(string warmVmtaName, string domain) => $"{warmVmtaName}|{domain.ToLowerInvariant()}";

    private static async Task<WarmupCounterFileData> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            return new WarmupCounterFileData();

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<WarmupCounterFileData>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? new WarmupCounterFileData();
    }

    private static Task WriteAsync(string filePath, WarmupCounterFileData data, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        return AtomicFile.WriteAsync(filePath,
            (stream, ct) => JsonSerializer.SerializeAsync(stream, data, cancellationToken: ct),
            cancellationToken);
    }
}
