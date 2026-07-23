using System.Threading.RateLimiting;
using StrongMTA.Core;

namespace StrongMTA.Engine;

/// <summary>
/// Backlog + teto de concorrência de uma única <see cref="QueueKey"/> (domínio × VirtualMta).
/// <see cref="TryPump"/> é não-bloqueante: tenta adquirir o slot global, o slot por VirtualMta
/// e o slot próprio (por domínio×VirtualMta) e, quando configurado, um token de rate limiting
/// — todos via tentativa imediata. Se qualquer um falhar, desiste sem esperar; a próxima
/// tentativa vem do próximo evento (novo item ou liberação de slot ao terminar uma entrega).
/// Quando o rate limiter nega o token, um re-pump é agendado automaticamente.
/// </summary>
internal sealed class QueueLane(QueueKey key, int maxConcurrent, FairShareDeliveryScheduler owner, RateLimiter? rateLimiter = null)
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

        // 1. Slot global
        if (!owner.TryAcquireGlobalSlot())
        {
            owner.RegisterWaiting(key);
            return;
        }

        // 2. Slot por VirtualMta (teto somado de todos os domínios do mesmo VMTA)
        if (!owner.TryAcquireVmtaSlot(key.VirtualMtaName))
        {
            owner.ReleaseGlobalSlot();
            owner.RegisterVmtaWaiting(key.VirtualMtaName, key);
            return;
        }

        // 3. Slot por domínio×VirtualMta (teto por QueueKey)
        if (!_keySlots.Wait(0))
        {
            owner.ReleaseVmtaSlot(key.VirtualMtaName);
            owner.ReleaseGlobalSlot();
            return;
        }

        // 4. Rate limit check após adquirir os três semáforos: evita um token ser consumido
        //    enquanto não há slot disponível; se negado, libera tudo e agenda re-pump.
        if (rateLimiter is not null && !rateLimiter.AttemptAcquire().IsAcquired)
        {
            _keySlots.Release();
            owner.ReleaseVmtaSlot(key.VirtualMtaName);
            owner.ReleaseGlobalSlot();
            ScheduleRateLimitRetry();
            return;
        }

        RecipientWorkItem item;
        lock (_gate)
        {
            if (_backlog.Count == 0)
            {
                // backlog esvaziou entre a checagem inicial e agora — devolve os três slots
                _keySlots.Release();
                owner.ReleaseVmtaSlot(key.VirtualMtaName);
                owner.ReleaseGlobalSlot();
                return;
            }

            item = _backlog.Dequeue();
        }

        _ = DispatchAsync(item);
    }

    private void ScheduleRateLimitRetry()
    {
        // estima quando o próximo token estará disponível via estatísticas do rate limiter;
        // cai num intervalo fixo de 500ms como fallback conservador.
        var stats = rateLimiter!.GetStatistics();
        var delayMs = stats?.CurrentAvailablePermits == 0 ? 500 : 100;
        _ = Task.Delay(delayMs).ContinueWith(_ => TryPump(), TaskScheduler.Default);
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
            // best-effort: SpoolDeliveryWorker trata os erros esperados; isto é só rede de segurança.
        }
        finally
        {
            _keySlots.Release();
            owner.ReleaseVmtaSlot(key.VirtualMtaName);
            owner.ReleaseGlobalSlot();
            owner.OnDispatchFinished();
            TryPump();
        }
    }
}
