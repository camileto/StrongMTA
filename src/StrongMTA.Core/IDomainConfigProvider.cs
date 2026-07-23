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

/// <summary>Provider com swap atômico — permite hot-reload do mta-config.json sem reiniciar o daemon.</summary>
public sealed class LiveDomainConfigProvider : IDomainConfigProvider
{
    private volatile StaticDomainConfigProvider _inner;

    public LiveDomainConfigProvider(DomainConfig defaultConfig, IReadOnlyDictionary<string, DomainConfig>? overrides = null)
        => _inner = new StaticDomainConfigProvider(defaultConfig, overrides);

    public DomainConfig GetConfig(string domain) => _inner.GetConfig(domain);

    public void Reload(DomainConfig defaultConfig, IReadOnlyDictionary<string, DomainConfig>? overrides = null)
        => _inner = new StaticDomainConfigProvider(defaultConfig, overrides);
}
