using DnsClient;

namespace StrongMTA.Smtp.Client;

/// <summary>Um host candidato para entrega, em ordem de preferência (menor = preferido).</summary>
public sealed record MxHost(string HostName, int Preference);

public interface IMxResolver
{
    /// <summary>
    /// Resolve os hosts MX de um domínio, em ordem de preferência. Se não houver
    /// registro MX, retorna o próprio domínio como único alvo (RFC 5321 §5.1) —
    /// a resolução A/AAAA fica a cargo do socket connect.
    /// </summary>
    Task<IReadOnlyList<MxHost>> ResolveAsync(string domain, CancellationToken cancellationToken = default);
}

public sealed class DnsClientMxResolver(ILookupClient lookupClient) : IMxResolver
{
    public DnsClientMxResolver() : this(new LookupClient())
    {
    }

    public async Task<IReadOnlyList<MxHost>> ResolveAsync(string domain, CancellationToken cancellationToken = default)
    {
        var result = await lookupClient.QueryAsync(domain, QueryType.MX, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var records = result.Answers.MxRecords()
            .Select(r => (Exchange: r.Exchange.Value.TrimEnd('.'), Preference: (int)r.Preference));

        return SelectHosts(domain, records);
    }

    /// <summary>
    /// Lógica pura de ordenação/fallback, isolada do I/O de DNS para ser testável sem rede:
    /// ordena por preferência (menor primeiro); se não houver registros MX, usa o próprio
    /// domínio como único alvo (RFC 5321 §5.1).
    /// </summary>
    internal static IReadOnlyList<MxHost> SelectHosts(string domain, IEnumerable<(string Exchange, int Preference)> records)
    {
        var hosts = records
            .OrderBy(r => r.Preference)
            .Select(r => new MxHost(r.Exchange, r.Preference))
            .ToList();

        if (hosts.Count == 0)
            return [new MxHost(domain, 0)];

        return hosts;
    }
}
