using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using StrongMTA.Core;

namespace StrongMTA.Daemon;

/// <summary>
/// Carrega <c>mta-config.json</c> (lista de domínios + VirtualMtas). JSON inválido/incompleto
/// lança e o daemon falha alto e claro ao iniciar. <see cref="TimeSpan"/>/<see cref="IPAddress"/>
/// são lidos como string e convertidos manualmente — System.Text.Json não tem conversor nativo pra eles.
/// </summary>
internal static class MtaConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Carrega o arquivo e devolve providers estáticos (boot inicial ou testes).</summary>
    public static (IDomainConfigProvider DomainConfigs, IVirtualMtaProvider VirtualMtas) Load(string path)
    {
        var (defaultConfig, overrides, vmtas) = ParseConfig(path);
        return (new StaticDomainConfigProvider(defaultConfig, overrides), new StaticVirtualMtaProvider(vmtas));
    }

    /// <summary>Carrega o arquivo e devolve providers "vivos" que suportam hot-reload via <c>Reload()</c>.</summary>
    public static (LiveDomainConfigProvider DomainConfigs, LiveVirtualMtaProvider VirtualMtas) LoadLive(string path)
    {
        var (defaultConfig, overrides, vmtas) = ParseConfig(path);
        return (new LiveDomainConfigProvider(defaultConfig, overrides), new LiveVirtualMtaProvider(vmtas));
    }

    /// <summary>
    /// Lê e parseia o arquivo, separando o domínio catch-all (<c>"*"</c>) do default global dos overrides
    /// por domínio explícito. Reutilizado tanto no boot quanto em cada reload.
    /// </summary>
    internal static (DomainConfig DefaultConfig, IReadOnlyDictionary<string, DomainConfig> Overrides, IReadOnlyDictionary<string, VirtualMta> VirtualMtas) ParseConfig(string path)
    {
        var json = File.ReadAllText(path);
        var file = JsonSerializer.Deserialize<MtaConfigFile>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Config inválido ou vazio: {path}");

        // Se há uma entrada "*" no JSON ela vira o catch-all real; sem ela usa os valores embutidos.
        var wildcardDto = file.Domains.FirstOrDefault(d => d.DomainName == "*");
        var defaultConfig = wildcardDto?.ToDomainConfig() ?? new DomainConfig
        {
            DomainName = "*",
            RetryIntervals = [TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(4)],
            BounceAfter = TimeSpan.FromHours(48)
        };

        var overrides = file.Domains
            .Where(d => d.DomainName != "*")
            .ToDictionary(d => d.DomainName, d => d.ToDomainConfig(), StringComparer.OrdinalIgnoreCase);

        var vmtas = file.VirtualMtas.ToDictionary(v => v.Name, v => v.ToVirtualMta());
        return (defaultConfig, overrides, vmtas);
    }

    private sealed class MtaConfigFile
    {
        public List<DomainConfigDto> Domains { get; set; } = [];
        public List<VirtualMtaDto> VirtualMtas { get; set; } = [];
    }

    private sealed class DomainConfigDto
    {
        public string DomainName { get; set; } = "";
        public int? MaxConcurrentConnections { get; set; }
        public int? MaxMessagesPerMinute { get; set; }
        public List<string> RetryIntervals { get; set; } = [];
        public string? BounceAfter { get; set; }

        public DomainConfig ToDomainConfig() => new()
        {
            DomainName = DomainName,
            MaxConcurrentConnections = MaxConcurrentConnections ?? 5,
            MaxMessagesPerMinute = MaxMessagesPerMinute ?? 0,
            RetryIntervals = RetryIntervals.Count == 0
                ? [TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(4)]
                : RetryIntervals.Select(TimeSpan.Parse).ToList(),
            BounceAfter = TimeSpan.Parse(BounceAfter ?? "2.00:00:00")
        };
    }

    private sealed class VirtualMtaDto
    {
        public string Name { get; set; } = "";
        /// <summary>Singular (compat): aceita um único IP sem precisar de lista.</summary>
        public string? SourceIp { get; set; }
        /// <summary>Pool de IPs para round-robin. Prevalece sobre <see cref="SourceIp"/> quando ambos forem especificados.</summary>
        public List<string>? SourceIps { get; set; }
        public string HostName { get; set; } = "";
        public string DkimSelector { get; set; } = "default";
        public string? ColdVmtaName { get; set; }
        public int? ColdVmtaDailyLimitPerDomain { get; set; }
        public int? MaxConcurrentConnections { get; set; }

        public VirtualMta ToVirtualMta()
        {
            List<IPAddress> ips = SourceIps is { Count: > 0 }
                ? SourceIps.Select(IPAddress.Parse).ToList()
                : SourceIp is not null
                    ? [IPAddress.Parse(SourceIp)]
                    : throw new InvalidOperationException($"VirtualMta '{Name}': é necessário ao menos um IP em 'sourceIp' ou 'sourceIps'.");

            return new VirtualMta
            {
                Name = Name,
                SourceIps = ips,
                HostName = HostName,
                DkimSelector = DkimSelector,
                ColdVmtaName = ColdVmtaName,
                ColdVmtaDailyLimitPerDomain = ColdVmtaDailyLimitPerDomain,
                MaxConcurrentConnections = MaxConcurrentConnections
            };
        }
    }
}
