using StrongMTA.Smtp.Client;

namespace StrongMTA.Engine.Tests;

/// <summary>Resolver de MX fake que mapeia cada domínio a um host fixo (tipicamente um endereço de loopback distinto representando um "servidor remoto" próprio em testes de concorrência) — sem rede, sem DNS.</summary>
public sealed class DomainMappedMxResolver(IReadOnlyDictionary<string, string> hostByDomain) : IMxResolver
{
    public Task<IReadOnlyList<MxHost>> ResolveAsync(string domain, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MxHost>>([new MxHost(hostByDomain[domain], 0)]);
}
