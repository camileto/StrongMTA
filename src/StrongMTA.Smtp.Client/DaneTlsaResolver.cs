using DnsClient;
using DnsClient.Protocol;

namespace StrongMTA.Smtp.Client;

public interface IDaneTlsaResolver
{
    /// <summary>
    /// Resolve registros TLSA para o host MX informado via consulta DNS a <c>_25._tcp.{mxHostName}</c>.
    /// Retorna lista vazia se não há registros, se a resposta não possui a flag AD (DNSSEC), ou em
    /// caso de falha de rede — o chamador deve tratar lista vazia como "sem DANE, usar fallback".
    /// </summary>
    Task<IReadOnlyList<DaneTlsaAssociation>> ResolveAsync(string mxHostName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementação produção de <see cref="IDaneTlsaResolver"/> usando DnsClient 1.8.0.
/// Exige que a resposta DNS contenha a flag AD (Authentic Data) para garantir que os registros
/// foram validados via DNSSEC — sem ela, registros TLSA não podem ser confiados.
/// </summary>
public sealed class DaneTlsaResolver : IDaneTlsaResolver
{
    private readonly ILookupClient _client;

    public DaneTlsaResolver() : this(new LookupClient()) { }

    public DaneTlsaResolver(ILookupClient lookupClient)
        => _client = lookupClient;

    public async Task<IReadOnlyList<DaneTlsaAssociation>> ResolveAsync(string mxHostName, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.QueryAsync($"_25._tcp.{mxHostName}", QueryType.TLSA, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Sem validação DNSSEC (AD flag) os registros não são confiáveis — ignorar.
            if (!response.Header.IsAuthenticData)
                return [];

            return response.Answers
                .OfType<TlsaRecord>()
                .Select(r => new DaneTlsaAssociation
                {
                    Usage = (byte)r.CertificateUsage,
                    Selector = (byte)r.Selector,
                    MatchingType = (byte)r.MatchingType,
                    CertificateAssociationData = r.CertificateAssociationData
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
