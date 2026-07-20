using StrongMTA.Core;

namespace StrongMTA.Accounting;

/// <summary>
/// Destino de eventos de accounting. A implementação JSONL completa (rotação diária,
/// todos os 6 tipos de evento) é escopo do M4 — por enquanto só os sinks abaixo existem.
/// </summary>
public interface IAccountingSink
{
    Task RecordAsync(AccountingEvent accountingEvent, CancellationToken cancellationToken = default);
}

public sealed class ConsoleAccountingSink : IAccountingSink
{
    public Task RecordAsync(AccountingEvent accountingEvent, CancellationToken cancellationToken = default)
    {
        Console.WriteLine(
            $"[accounting] {accountingEvent.Timestamp:O} {accountingEvent.Type} " +
            $"msg={accountingEvent.MessageId} domain={accountingEvent.DestinationDomain} " +
            $"code={accountingEvent.SmtpCode} text=\"{accountingEvent.SmtpResponseText}\"");
        return Task.CompletedTask;
    }
}

public sealed class InMemoryAccountingSink : IAccountingSink
{
    private readonly List<AccountingEvent> _events = [];
    public IReadOnlyList<AccountingEvent> Events => _events;

    public Task RecordAsync(AccountingEvent accountingEvent, CancellationToken cancellationToken = default)
    {
        _events.Add(accountingEvent);
        return Task.CompletedTask;
    }
}
