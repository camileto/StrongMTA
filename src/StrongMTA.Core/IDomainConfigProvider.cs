namespace StrongMTA.Core;

public interface IDomainConfigProvider
{
    DomainConfig GetConfig(string domain);
}

/// <summary>Provider em memória com um default global e overrides opcionais por domínio.</summary>
public sealed class StaticDomainConfigProvider(DomainConfig defaultConfig, IReadOnlyDictionary<string, DomainConfig>? overrides = null) : IDomainConfigProvider
{
    public DomainConfig GetConfig(string domain) =>
        overrides is not null && overrides.TryGetValue(domain, out var config) ? config : defaultConfig;
}
