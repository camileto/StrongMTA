using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using StrongMTA.Core;

namespace StrongMTA.Engine;

/// <summary>
/// Substitui o <c>DeliveryChannel</c> plano: uma "lane" por <see cref="QueueKey"/> (domínio ×
/// VirtualMta), cada uma com seu próprio teto de concorrência (<see cref="DomainConfig.MaxConcurrentConnections"/>),
/// mais um teto global (<see cref="SchedulerOptions.GlobalMaxConcurrency"/>) e um teto opcional
/// por VirtualMta (<see cref="VirtualMta.MaxConcurrentConnections"/>) compartilhados. Totalmente
/// reativo — não há loop coordenador nem scan periódico: cada "TryPump" é disparado diretamente
/// por (a) chegada de item novo ou (b) liberação de um slot ao terminar uma entrega. A fairness
/// entre domínio grande e pequeno sai de graça: o teto por chave é o único limite artificial que
/// impede um domínio de tomar 100% do pool global; abaixo dele, toda lane elegível (com backlog e
/// os slots livres) é despachada imediatamente. A disputa pelo slot global é resolvida por
/// round-robin (ver <see cref="ReleaseGlobalSlot"/>); a disputa pelo slot por VMTA é resolvida
/// da mesma forma (ver <see cref="ReleaseVmtaSlot"/>).
/// </summary>
public sealed class FairShareDeliveryScheduler(
    IDomainConfigProvider domainConfigProvider,
    SchedulerOptions options,
    Func<RecipientWorkItem, CancellationToken, Task> deliverOneAsync,
    IVirtualMtaProvider? vmtaProvider = null) : IDeliveryScheduler
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

    // semáforos por VirtualMta (null = sem teto para esse VMTA)
    private readonly ConcurrentDictionary<string, SemaphoreSlim?> _vmtaGates = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<QueueKey>> _vmtaWaiterQueues = new();
    private readonly ConcurrentDictionary<QueueKey, byte> _vmtaWaitingSet = new();

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
        var config = domainConfigProvider.GetConfig(key.DestinationDomain);
        var rateLimiter = config.MaxMessagesPerMinute > 0
            ? new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = config.MaxMessagesPerMinute,
                TokensPerPeriod = config.MaxMessagesPerMinute,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                AutoReplenishment = true,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            })
            : null;
        return new QueueLane(key, config.MaxConcurrentConnections, this, rateLimiter);
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

    internal bool TryAcquireVmtaSlot(string vmtaName)
    {
        var gate = _vmtaGates.GetOrAdd(vmtaName, CreateVmtaGate);
        return gate?.Wait(0) ?? true; // null = sem teto por VMTA → sempre passa
    }

    internal void ReleaseVmtaSlot(string vmtaName)
    {
        if (!_vmtaGates.TryGetValue(vmtaName, out var gate) || gate is null)
            return; // sem teto: sem semáforo a liberar, sem waiters a acordar

        gate.Release();

        if (!_vmtaWaiterQueues.TryGetValue(vmtaName, out var waiters))
            return;

        while (waiters.TryDequeue(out var waitingKey))
        {
            _vmtaWaitingSet.TryRemove(waitingKey, out _);
            if (_lanes.TryGetValue(waitingKey, out var lane))
            {
                lane.TryPump();
                break;
            }
        }
    }

    internal void RegisterVmtaWaiting(string vmtaName, QueueKey key)
    {
        var waiters = _vmtaWaiterQueues.GetOrAdd(vmtaName, _ => new ConcurrentQueue<QueueKey>());
        if (_vmtaWaitingSet.TryAdd(key, 0))
            waiters.Enqueue(key);
    }

    private SemaphoreSlim? CreateVmtaGate(string vmtaName)
    {
        if (vmtaProvider is null) return null;
        try
        {
            var vmta = vmtaProvider.GetVirtualMta(vmtaName);
            return vmta.MaxConcurrentConnections is { } max && max > 0
                ? new SemaphoreSlim(max, max)
                : null;
        }
        catch (KeyNotFoundException)
        {
            return null; // VMTA desconhecido = sem teto
        }
    }

    internal Func<RecipientWorkItem, CancellationToken, Task> DeliverOneAsync => deliverOneAsync;

    internal CancellationToken CancellationToken => _cancellationToken;

    internal void OnDispatchStarted() => Interlocked.Increment(ref _inFlightCount);

    internal void OnDispatchFinished() => Interlocked.Decrement(ref _inFlightCount);
}
