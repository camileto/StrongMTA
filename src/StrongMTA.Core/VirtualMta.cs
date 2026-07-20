using System.Net;

namespace StrongMTA.Core;

/// <summary>
/// Identidade de envio: um IP de origem fixo + hostname HELO + selector DKIM.
/// No MVP não há pool/round-robin — cada VirtualMta tem exatamente um IP.
/// </summary>
public sealed class VirtualMta
{
    public required string Name { get; init; }
    public required IPAddress SourceIp { get; init; }
    public required string HostName { get; init; }
    public required string DkimSelector { get; init; }

    /// <summary>Nome do VirtualMta "frio" para warm-up. Null = sem warm-up.</summary>
    public string? ColdVmtaName { get; init; }

    /// <summary>Mensagens/dia/domínio permitidas antes de desviar para o VirtualMta frio.</summary>
    public int? ColdVmtaDailyLimitPerDomain { get; init; }

    public bool HasWarmup => ColdVmtaName is not null && ColdVmtaDailyLimitPerDomain is not null;
}
