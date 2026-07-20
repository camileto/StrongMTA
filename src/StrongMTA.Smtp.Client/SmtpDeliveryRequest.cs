using System.Net;

namespace StrongMTA.Smtp.Client;

public sealed class SmtpDeliveryRequest
{
    public required string TargetHost { get; init; }
    public int TargetPort { get; init; } = 25;
    public required string HeloHostName { get; init; }
    public required string EnvelopeFrom { get; init; }
    public required string RecipientAddress { get; init; }
    public required Func<CancellationToken, Task<Stream>> OpenBodyStream { get; init; }

    /// <summary>IP de origem a usar no bind do socket (identidade do VirtualMta). Null = padrão do SO.</summary>
    public IPAddress? LocalIpAddress { get; init; }

    /// <summary>Se true, aborta a entrega (Transient) quando STARTTLS não é aceito ou falha. Default: oportunista.</summary>
    public bool RequireStartTls { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan DataTimeout { get; init; } = TimeSpan.FromMinutes(5);
}
