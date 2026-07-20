using StrongMTA.Smtp.Client;

namespace StrongMTA.Engine.Tests;

/// <summary>Resolver de MX fake para testes: por padrão resolve para 127.0.0.1, sem rede. Aceita uma lista customizada de hosts (em ordem de preferência) para simular múltiplos MX candidatos, ex.: testes de skip-mx.</summary>
public sealed class TestMxResolver(params string[] hostNames) : IMxResolver
{
    public Task<IReadOnlyList<MxHost>> ResolveAsync(string domain, CancellationToken cancellationToken = default)
    {
        var hosts = hostNames.Length == 0 ? new[] { "127.0.0.1" } : hostNames;
        return Task.FromResult<IReadOnlyList<MxHost>>(hosts.Select((h, i) => new MxHost(h, i)).ToList());
    }
}
