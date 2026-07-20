using System.Collections.Concurrent;

namespace StrongMTA.Engine.Tests;

/// <summary>Duplo de teste de <see cref="IDeliveryScheduler"/>: só captura os itens enfileirados, nunca dispara entrega — equivalente ao papel que o <c>DeliveryChannel</c> sem consumidor desempenhava nos testes antes do M7.</summary>
public sealed class RecordingScheduler : IDeliveryScheduler
{
    private readonly ConcurrentQueue<RecipientWorkItem> _items = new();

    public IReadOnlyList<RecipientWorkItem> Items => _items.ToArray();
    public int Count => _items.Count;

    public void Enqueue(RecipientWorkItem item) => _items.Enqueue(item);

    public bool TryDequeue(out RecipientWorkItem item) => _items.TryDequeue(out item!);
}
