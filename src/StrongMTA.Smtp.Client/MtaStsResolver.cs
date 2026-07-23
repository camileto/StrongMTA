using System.Collections.Concurrent;
using DnsClient;

namespace StrongMTA.Smtp.Client;

public enum MtaStsPolicyMode
{
    /// <summary>Sem política (DNS/TXT ausente ou inacessível).</summary>
    None,
    /// <summary>Modo de teste: valida mas não bloqueia entrega.</summary>
    Testing,
    /// <summary>Modo enforce: entrega exige TLS com cert PKI válido para os MX listados.</summary>
    Enforce
}

/// <summary>
/// Política MTA-STS de um domínio destino (RFC 8461).
/// </summary>
public sealed class MtaStsPolicy
{
    public static readonly MtaStsPolicy None = new() { Mode = MtaStsPolicyMode.None };

    public MtaStsPolicyMode Mode { get; init; } = MtaStsPolicyMode.None;
    public IReadOnlyList<string> MxPatterns { get; init; } = [];
    public int MaxAge { get; init; }

    /// <summary>
    /// Verifica se o host MX corresponde a qualquer padrão da política.
    /// Curingas (<c>*.example.com</c>) casam exatamente um rótulo de prefixo.
    /// </summary>
    public bool MxMatches(string mxHost)
    {
        foreach (var pattern in MxPatterns)
        {
            if (MatchesPattern(mxHost, pattern))
                return true;
        }
        return false;
    }

    private static bool MatchesPattern(string host, string pattern)
    {
        if (!pattern.StartsWith("*.", StringComparison.Ordinal))
            return host.Equals(pattern, StringComparison.OrdinalIgnoreCase);

        // *.example.com case: suffix = ".example.com", prefix must be a single label (no dots)
        var suffix = pattern[1..]; // ".example.com"
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;
        var prefix = host[..^suffix.Length];
        return prefix.Length > 0 && !prefix.Contains('.');
    }
}

public interface IMtaStsResolver
{
    /// <summary>
    /// Retorna a política MTA-STS do domínio. Resultado é cacheado pelo <c>max_age</c> da política.
    /// Retorna <see cref="MtaStsPolicy.None"/> em caso de erro ou ausência de política.
    /// </summary>
    Task<MtaStsPolicy> ResolveAsync(string domain, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementação produção de <see cref="IMtaStsResolver"/>.
/// Consulta <c>_mta-sts.{domain}</c> via DNS/TXT, depois busca a policy via HTTPS.
/// Cache em memória respeitando o <c>max_age</c> da política (mín 60s, máx 86400s).
/// </summary>
public sealed class MtaStsResolver : IMtaStsResolver
{
    private readonly HttpClient _httpClient;
    private readonly ILookupClient _dnsClient;
    private readonly ConcurrentDictionary<string, (MtaStsPolicy Policy, DateTimeOffset ExpiresAt)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public MtaStsResolver() : this(null, null) { }

    public MtaStsResolver(HttpClient? httpClient, ILookupClient? dnsClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _dnsClient = dnsClient ?? new LookupClient();
    }

    public async Task<MtaStsPolicy> ResolveAsync(string domain, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(domain, out var cached) && DateTimeOffset.UtcNow < cached.ExpiresAt)
            return cached.Policy;

        var policy = await FetchPolicyAsync(domain, cancellationToken).ConfigureAwait(false);
        var ttl = TimeSpan.FromSeconds(Math.Clamp(policy.MaxAge, 60, 86400));
        _cache[domain] = (policy, DateTimeOffset.UtcNow + ttl);
        return policy;
    }

    private async Task<MtaStsPolicy> FetchPolicyAsync(string domain, CancellationToken cancellationToken)
    {
        // 1. Verificar presença do TXT _mta-sts.<domain>
        try
        {
            var dns = await _dnsClient.QueryAsync($"_mta-sts.{domain}", QueryType.TXT, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var hasSts = dns.Answers.TxtRecords()
                .SelectMany(r => r.Text)
                .Any(t => t.StartsWith("v=STSv1", StringComparison.OrdinalIgnoreCase));
            if (!hasSts) return MtaStsPolicy.None;
        }
        catch { return MtaStsPolicy.None; }

        // 2. Buscar o arquivo de política via HTTPS
        try
        {
            var url = $"https://mta-sts.{domain}/.well-known/mta-sts.txt";
            var text = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            return ParsePolicy(text);
        }
        catch { return MtaStsPolicy.None; }
    }

    internal static MtaStsPolicy ParsePolicy(string policyText)
    {
        var mode = MtaStsPolicyMode.None;
        var mxPatterns = new List<string>();
        var maxAge = 0;

        foreach (var rawLine in policyText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("version:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("mode:", StringComparison.OrdinalIgnoreCase))
            {
                mode = line[5..].Trim() switch
                {
                    "enforce" => MtaStsPolicyMode.Enforce,
                    "testing" => MtaStsPolicyMode.Testing,
                    _ => MtaStsPolicyMode.None
                };
            }
            else if (line.StartsWith("mx:", StringComparison.OrdinalIgnoreCase))
            {
                var pattern = line[3..].Trim();
                if (pattern.Length > 0) mxPatterns.Add(pattern);
            }
            else if (line.StartsWith("max_age:", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(line[8..].Trim(), out var age))
            {
                maxAge = age;
            }
        }

        if (mode == MtaStsPolicyMode.None) return MtaStsPolicy.None;
        return new MtaStsPolicy { Mode = mode, MxPatterns = mxPatterns, MaxAge = maxAge };
    }
}
