using StrongMTA.Core;

namespace StrongMTA.Engine;

/// <summary>
/// Backlog + teto de concorrência de uma única <see cref="QueueKey"/> (domínio × VirtualMta).
/// <see cref="TryPump"/> é não-bloqueante: tenta adquirir o slot global e o slot próprio
/// (ambos via <c>Wait(0)</c>) e, se conseguir os dois, despacha um item em fire-and-forget;
/// se falhar em qualquer um, desiste sem esperar — a próxima tentativa vem do próximo evento
/// (novo item, ou liberação de slot ao terminar uma entrega anterior).
/// </summary>
internal sealed class QueueLane(QueueKey key, int maxConcurrent, FairShareDeliveryScheduler owner)
{
    private readonly Queue<RecipientWorkItem> _backlog = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _keySlots = new(maxConcurrent, maxConcurrent);

    public void Enqueue(RecipientWorkItem item)
    {
        lock (_gate) _backlog.Enqueue(item);
        TryPump();
    }

    public void TryPump()
    {
        lock (_gate)
        {
            if (_backlog.Count == 0)
                return;
        }

        if (!owner.TryAcquireGlobalSlot())
        {
            owner.RegisterWaiting(key);
            return;
        }

        if (!_keySlots.Wait(0))
        {
            owner.ReleaseGlobalSlot(); // não precisamos dele agora — devolve pro pool global
            return;
        }

        RecipientWorkItem item;
        lock (_gate)
        {
            if (_backlog.Count == 0)
            {
                // backlog esvaziou entre a checagem inicial e agora (outra TryPump concorrente venceu) — devolve os dois slots
                _keySlots.Release();
                owner.ReleaseGlobalSlot();
                return;
            }

            item = _backlog.Dequeue();
        }

        _ = DispatchAsync(item);
    }

    private async Task DispatchAsync(RecipientWorkItem item)
    {
        owner.OnDispatchStarted();
        try
        {
            await owner.DeliverOneAsync(item, owner.CancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // best-effort: uma falha inesperada numa entrega não deve travar a lane inteira;
            // SpoolDeliveryWorker.DeliverOneAsync já trata os erros esperados internamente —
            // isto é só uma rede de segurança contra exceções não previstas.
        }
        finally
        {
            _keySlots.Release();
            owner.ReleaseGlobalSlot();
            owner.OnDispatchFinished();
            TryPump(); // tenta drenar mais backlog desta lane imediatamente
        }
    }
}
