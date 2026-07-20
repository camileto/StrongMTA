using StrongMTA.Core;
using StrongMTA.Spool;

namespace StrongMTA.Engine;

/// <summary>
/// Decide, no momento da submissão, se um destinatário deve ser desviado para o
/// VirtualMta frio de warm-up — fixando a decisão no envelope (não reavaliada em
/// retries futuros desta mesma mensagem).
/// </summary>
public sealed class WarmupRouter(WarmupCounterStore counterStore)
{
    public async Task<string> ResolveVirtualMtaNameAsync(VirtualMta requestedVmta, string destinationDomain, CancellationToken cancellationToken = default)
    {
        if (!requestedVmta.HasWarmup)
            return requestedVmta.Name;

        var divertToCold = await counterStore.TryReserveColdSlotAsync(
            requestedVmta.Name, destinationDomain, requestedVmta.ColdVmtaDailyLimitPerDomain!.Value, cancellationToken).ConfigureAwait(false);

        return divertToCold ? requestedVmta.ColdVmtaName! : requestedVmta.Name;
    }
}
