namespace StrongMTA.Engine;

/// <summary>
/// Destinatários em estado Transient aguardando o próximo horário de retry, fora do
/// Channel principal (que é só para trabalho imediato). O <see cref="RetryScheduler"/>
/// promove periodicamente os que já venceram de volta para a fila de entrega.
/// </summary>
public sealed class PendingRetryIndex
{
    private readonly List<RecipientWorkItem> _items = [];
    private readonly object _gate = new();

    public void Add(RecipientWorkItem item)
    {
        lock (_gate) _items.Add(item);
    }

    public IReadOnlyList<RecipientWorkItem> DequeueDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_items.Count == 0) return [];
            var due = _items.Where(i => i.NextAttemptAt <= now).ToList();
            foreach (var item in due) _items.Remove(item);
            return due;
        }
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }
}
