namespace StrongMTA.Engine;

/// <summary>Promove periodicamente destinatários cujo NextAttemptAt já venceu de volta para a fila de entrega.</summary>
public sealed class RetryScheduler(PendingRetryIndex pendingIndex, IDeliveryScheduler scheduler)
{
    public Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var due = pendingIndex.DequeueDue(DateTimeOffset.UtcNow);
        foreach (var item in due)
            scheduler.Enqueue(item);
        return Task.FromResult(due.Count);
    }

    public async Task RunAsync(TimeSpan pollInterval, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
