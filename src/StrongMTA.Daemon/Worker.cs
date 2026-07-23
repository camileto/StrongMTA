using StrongMTA.Engine;
using StrongMTA.Smtp.Server;

namespace StrongMTA.Daemon;

/// <summary>
/// Host real do daemon: recupera o spool no boot, liga o scheduler de entrega (paralelo,
/// respeitando teto global e por domínio×VirtualMta), o promotor periódico de retries, o
/// purge periódico de arquivos terminais e o listener de bounce/FBL.
/// </summary>
public sealed class Worker(
    ILogger<Worker> logger,
    SpoolBootRecovery bootRecovery,
    FairShareDeliveryScheduler scheduler,
    PendingRetryIndex pendingRetryIndex,
    RetryScheduler retryScheduler,
    SpoolPurgeService spoolPurgeService,
    MtaConfigWatcher configWatcher,
    BounceListener bounceListener) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = await bootRecovery.RecoverAsync(scheduler, pendingRetryIndex, stoppingToken).ConfigureAwait(false);
        logger.LogInformation("Recuperação de boot: {Count} destinatário(s) retomado(s) do spool.", recovered);

        configWatcher.Start();
        bounceListener.Start();
        logger.LogInformation("Listener de bounce/FBL escutando na porta {Port}.", bounceListener.Port);

        try
        {
            await Task.WhenAll(
                scheduler.RunAsync(stoppingToken),
                retryScheduler.RunAsync(TimeSpan.FromSeconds(5), stoppingToken),
                spoolPurgeService.RunAsync(stoppingToken)
            ).ConfigureAwait(false);
        }
        finally
        {
            bounceListener.Stop();
            await bounceListener.WaitForShutdownAsync().ConfigureAwait(false);
        }
    }
}
