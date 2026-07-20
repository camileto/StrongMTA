using System.Collections.Concurrent;
using StrongMTA.Core;

namespace StrongMTA.Engine;

/// <summary>
/// Substitui o <c>DeliveryChannel</c> plano: uma "lane" por <see cref="QueueKey"/> (domínio ×
/// VirtualMta), cada uma com seu próprio teto de concorrência (<see cref="DomainConfig.MaxConcurrentConnections"/>),
/// mais um teto global (<see cref="SchedulerOptions.GlobalMaxConcurrency"/>) compartilhado por
/// todas as lanes. Totalmente reativo — não há loop coordenador nem scan periódico: cada
/// "TryPump" é disparado diretamente por (a) chegada de item novo ou (b) liberação de um slot
/// ao terminar uma entrega. A fairness entre domínio grande e pequeno sai de graça: o teto por
/// chave é o único limite artificial que impede um domínio de tomar 100% do pool global; abaixo
/// dele, toda lane elegível (com backlog e os dois slots livres) é despachada imediatamente.
/// A disputa pelo slot global quando há mais lanes elegíveis do que slots livres é resolvida por
/// um round-robin simples de lanes em espera (ver <see cref="ReleaseGlobalSlot"/>), não por
/// sorteio ponderado — a ponderação por volume já é coberta pelo teto por chave.
/// </summary>
public sealed class FairShareDeliveryScheduler(
    IDomainConfigProvider domainConfigProvider,
    SchedulerOptions options,
    Func<RecipientWorkItem, CancellationToken, Task> deliverOneAsync) : IDeliveryScheduler
{
    // null = teto global zero/negativo: nenhuma entrega é despachada, itens só se acumulam nas
    // lanes (uso legítimo: comandos administrativos que só precisam escrever no spool, sem
    // rodar nenhum worker no próprio processo — ver StrongMTA.Cli.SubmitCommand). SemaphoreSlim
    // não aceita maxCount=0, por isso o caminho precisa de um sentinel em vez de só passar 0.
    private readonly SemaphoreSlim? _globalGate = options.GlobalMaxConcurrency > 0
        ? new SemaphoreSlim(options.GlobalMaxConcurrency, options.GlobalMaxConcurrency)
        : null;
    private readonly ConcurrentDictionary<QueueKey, QueueLane> _lanes = new();
    private readonly ConcurrentQueue<QueueKey> _globalWaiters = new();
    private readonly ConcurrentDictionary<QueueKey, byte> _waitingSet = new();
    private int _inFlightCount;
    private CancellationToken _cancellationToken;

    public void Enqueue(RecipientWorkItem item)
    {
        var key = new QueueKey { DestinationDomain = item.DestinationDomain, VirtualMtaName = item.VirtualMtaName };
        var lane = _lanes.GetOrAdd(key, CreateLane);
        lane.Enqueue(item);
    }

    /// <summary>Mantém o host vivo até o cancelamento, e então espera as entregas em voo terminarem antes de retornar (drenagem simples, sem suporte a timeout configurável nesta milestone).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // esperado no shutdown
        }

        while (Volatile.Read(ref _inFlightCount) > 0)
            await Task.Delay(50).ConfigureAwait(false);
    }

    private QueueLane CreateLane(QueueKey key)
    {
        var maxConcurrent = domainConfigProvider.GetConfig(key.DestinationDomain).MaxConcurrentConnections;
        return new QueueLane(key, maxConcurrent, this);
    }

    internal bool TryAcquireGlobalSlot() => _globalGate?.Wait(0) ?? false;

    internal void ReleaseGlobalSlot()
    {
        _globalGate?.Release();

        while (_globalWaiters.TryDequeue(out var waitingKey))
        {
            _waitingSet.TryRemove(waitingKey, out _);
            if (_lanes.TryGetValue(waitingKey, out var lane))
            {
                lane.TryPump();
                break;
            }
        }
    }

    internal void RegisterWaiting(QueueKey key)
    {
        if (_waitingSet.TryAdd(key, 0))
            _globalWaiters.Enqueue(key);
    }

    internal Func<RecipientWorkItem, CancellationToken, Task> DeliverOneAsync => deliverOneAsync;

    internal CancellationToken CancellationToken => _cancellationToken;

    internal void OnDispatchStarted() => Interlocked.Increment(ref _inFlightCount);

    internal void OnDispatchFinished() => Interlocked.Decrement(ref _inFlightCount);
}
