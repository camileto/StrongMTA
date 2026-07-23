using StrongMTA.Core;
using StrongMTA.Spool;

namespace StrongMTA.Engine;

/// <summary>
/// Remove periodicamente do spool arquivos .msg + .state cujos destinatários estão todos em
/// status terminal (Delivered, Bounced, Expired, Suppressed) há mais tempo que
/// <see cref="SpoolPurgeOptions.RetainAfterTerminal"/>. Ordem de deleção: .msg primeiro,
/// .state depois — um .state órfão é inofensivo (o scanner só percorre .msg files), enquanto
/// o inverso reprocessaria a mensagem no próximo boot.
/// </summary>
public sealed class SpoolPurgeService(SpoolScanner scanner, SpoolPurgeOptions options)
{
    private static readonly RecipientStatus[] TerminalStatuses =
        [RecipientStatus.Delivered, RecipientStatus.Bounced, RecipientStatus.Expired, RecipientStatus.Suppressed];

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PurgeOnceAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<int> PurgeOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var purged = 0;

        await foreach (var record in scanner.ScanAsync(cancellationToken).ConfigureAwait(false))
        {
            var recipients = record.State.Recipients;

            if (!recipients.All(r => TerminalStatuses.Contains(r.Status)))
                continue;

            var lastActivity = recipients
                .Select(r => r.LastAttemptAt)
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Max();

            if (now - lastActivity < options.RetainAfterTerminal)
                continue;

            TryDelete(record.MsgFilePath);
            TryDelete(record.StateFilePath);
            purged++;
        }

        return purged;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
