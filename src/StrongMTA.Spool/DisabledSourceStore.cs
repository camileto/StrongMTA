using System.Text.Json;

namespace StrongMTA.Spool;

internal sealed class DisabledSourceFileData
{
    public Dictionary<string, DateTimeOffset?> DisabledUntil { get; set; } = new();
}

/// <summary>
/// Persiste, em <c>cold/disabled-sources.json</c>, quais endereços IP de origem estão
/// temporariamente desabilitados (e até quando) — equivalente ao <c>disable-source-ip</c>/
/// <c>reenable-after</c> do PowerMTA. A chave é o endereço IP como string (ex.: "203.0.113.10"),
/// não o nome do VirtualMta — isso permite desabilitar um IP específico de um pool sem afetar
/// os outros IPs do mesmo VirtualMta. Expiração lazy, mesmo padrão dos outros stores do spool.
/// </summary>
public sealed class DisabledSourceStore(SpoolPaths paths, Func<DateTimeOffset>? nowProvider = null)
{
    private readonly Func<DateTimeOffset> _now = nowProvider ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Desabilita o IP de origem. <paramref name="reenableAfter"/> null significa "nunca" (precisa de reabilitação manual).</summary>
    public async Task DisableAsync(string sourceKey, TimeSpan? reenableAfter, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var filePath = GetFilePath();
            var data = await ReadAsync(filePath, cancellationToken).ConfigureAwait(false);
            data.DisabledUntil[sourceKey] = reenableAfter is { } delay ? _now() + delay : (DateTimeOffset?)null;
            await WriteAsync(filePath, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsDisabledAsync(string sourceKey, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var filePath = GetFilePath();
            var data = await ReadAsync(filePath, cancellationToken).ConfigureAwait(false);

            if (!data.DisabledUntil.TryGetValue(sourceKey, out var until))
                return false;

            if (until is { } when && _now() >= when)
            {
                data.DisabledUntil.Remove(sourceKey);
                await WriteAsync(filePath, data, cancellationToken).ConfigureAwait(false);
                return false;
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetFilePath() => Path.Combine(paths.ColdDirectory, "disabled-sources.json");

    private static async Task<DisabledSourceFileData> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            return new DisabledSourceFileData();

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<DisabledSourceFileData>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? new DisabledSourceFileData();
    }

    private static Task WriteAsync(string filePath, DisabledSourceFileData data, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        return AtomicFile.WriteAsync(filePath,
            (stream, ct) => JsonSerializer.SerializeAsync(stream, data, cancellationToken: ct),
            cancellationToken);
    }
}
