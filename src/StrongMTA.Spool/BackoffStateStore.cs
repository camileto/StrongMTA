using System.Text.Json;
using StrongMTA.Core;

namespace StrongMTA.Spool;

internal sealed class BackoffStateFileData
{
    public Dictionary<string, BackoffEntry> Queues { get; set; } = new();
}

internal sealed class BackoffEntry
{
    public QueueState State { get; set; } = QueueState.Normal;
    public DateTimeOffset? BackoffUntil { get; set; }
}

/// <summary>
/// Persiste, em <c>cold/backoff-state.json</c>, se uma fila (domínio × VirtualMta) está em
/// modo de backoff e até quando — equivalente ao <c>QueueRuntimeState</c> (em memória, definido
/// desde M0 mas nunca usado) só que durável. Expiração lazy: <see cref="IsInBackoffAsync"/>
/// sai do backoff automaticamente se <c>BackoffUntil</c> já passou, sem precisar de um timer
/// dedicado — mesmo padrão do reset de meia-noite do <see cref="WarmupCounterStore"/>.
/// </summary>
public sealed class BackoffStateStore(SpoolPaths paths, Func<DateTimeOffset>? nowProvider = null)
{
    private readonly Func<DateTimeOffset> _now = nowProvider ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task EnterBackoffAsync(QueueKey key, TimeSpan? autoNormalAfter, CancellationToken cancellationToken = default) =>
        MutateAsync(key, entry =>
        {
            entry.State = QueueState.Backoff;
            entry.BackoffUntil = autoNormalAfter is { } delay ? _now() + delay : null;
        }, cancellationToken);

    public Task ExitBackoffAsync(QueueKey key, CancellationToken cancellationToken = default) =>
        MutateAsync(key, entry =>
        {
            entry.State = QueueState.Normal;
            entry.BackoffUntil = null;
        }, cancellationToken);

    public async Task<bool> IsInBackoffAsync(QueueKey key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var filePath = GetFilePath();
            var data = await ReadAsync(filePath, cancellationToken).ConfigureAwait(false);

            if (!data.Queues.TryGetValue(BuildKey(key), out var entry) || entry.State != QueueState.Backoff)
                return false;

            if (entry.BackoffUntil is { } until && _now() >= until)
            {
                entry.State = QueueState.Normal;
                entry.BackoffUntil = null;
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

    private async Task MutateAsync(QueueKey key, Action<BackoffEntry> mutate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var filePath = GetFilePath();
            var data = await ReadAsync(filePath, cancellationToken).ConfigureAwait(false);

            var compositeKey = BuildKey(key);
            if (!data.Queues.TryGetValue(compositeKey, out var entry))
            {
                entry = new BackoffEntry();
                data.Queues[compositeKey] = entry;
            }

            mutate(entry);
            await WriteAsync(filePath, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetFilePath() => Path.Combine(paths.ColdDirectory, "backoff-state.json");

    private static string BuildKey(QueueKey key) => $"{key.VirtualMtaName}|{key.DestinationDomain.ToLowerInvariant()}";

    private static async Task<BackoffStateFileData> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
            return new BackoffStateFileData();

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<BackoffStateFileData>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? new BackoffStateFileData();
    }

    private static Task WriteAsync(string filePath, BackoffStateFileData data, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        return AtomicFile.WriteAsync(filePath,
            (stream, ct) => JsonSerializer.SerializeAsync(stream, data, cancellationToken: ct),
            cancellationToken);
    }
}
