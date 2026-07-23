using System.Net;

namespace StrongMTA.Core;

/// <summary>
/// Identidade de envio: pool de IPs de origem + hostname HELO + selector DKIM.
/// O IP efetivamente usado em cada entrega é escolhido via round-robin sobre <see cref="SourceIps"/>,
/// pulando os temporariamente desabilitados (disable-source-ip).
/// </summary>
public sealed class VirtualMta
{
    public required string Name { get; init; }
    public required IReadOnlyList<IPAddress> SourceIps { get; init; }
    public required string HostName { get; init; }
    public required string DkimSelector { get; init; }

    /// <summary>Nome do VirtualMta "frio" para warm-up. Null = sem warm-up.</summary>
    public string? ColdVmtaName { get; init; }

    /// <summary>Mensagens/dia/domínio permitidas antes de desviar para o VirtualMta frio.</summary>
    public int? ColdVmtaDailyLimitPerDomain { get; init; }

    public bool HasWarmup => ColdVmtaName is not null && ColdVmtaDailyLimitPerDomain is not null;
}
