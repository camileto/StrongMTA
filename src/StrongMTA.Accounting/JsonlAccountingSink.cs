using System.Text;
using System.Text.Json;
using StrongMTA.Core;

namespace StrongMTA.Accounting;

/// <summary>
/// Escreve cada evento como uma linha JSON em <c>accounting/YYYY-MM-DD.jsonl</c> (UTC),
/// append-only — nunca reescreve uma linha já gravada. Rotação diária por nome de
/// arquivo: nenhum processo de "fechamento" é necessário, o arquivo do dia anterior
/// simplesmente para de receber escritas quando a data UTC vira. Um gate serializa as
/// escritas porque <see cref="FileStream"/> com <c>FileShare.Read</c> não é thread-safe
/// para apends concorrentes de múltiplos workers de entrega.
/// </summary>
public sealed class JsonlAccountingSink(string accountingDirectory) : IAccountingSink, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _openStream;
    private DateOnly? _openDate;

    public async Task RecordAsync(AccountingEvent accountingEvent, CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(accountingEvent);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = GetStreamForDate(DateOnly.FromDateTime(accountingEvent.Timestamp.UtcDateTime));
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private FileStream GetStreamForDate(DateOnly date)
    {
        if (_openStream is not null && _openDate == date)
            return _openStream;

        _openStream?.Dispose();

        Directory.CreateDirectory(accountingDirectory);
        var path = Path.Combine(accountingDirectory, $"{date:yyyy-MM-dd}.jsonl");
        _openStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _openDate = date;
        return _openStream;
    }

    public void Dispose()
    {
        _openStream?.Dispose();
        _gate.Dispose();
    }
}
