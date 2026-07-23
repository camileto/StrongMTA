using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace StrongMTA.Smtp.Client;

/// <summary>
/// Nível de exigência TLS aplicado a uma tentativa de entrega.
/// </summary>
public enum TlsEnforcementMode
{
    /// <summary>TLS se disponível, qualquer certificado aceito (comportamento padrão).</summary>
    Opportunistic = 0,
    /// <summary>TLS obrigatório; fallback para texto puro é abortado. Certificado não é validado por PKI (usado com DANE).</summary>
    Required = 1,
    /// <summary>TLS obrigatório + validação PKI padrão (cadeia confiável + nome). Usado para MTA-STS enforce.</summary>
    RequiredVerified = 2
}

/// <summary>
/// Política TLS a aplicar durante o handshake SMTP de saída. Gerada pelo <c>SpoolDeliveryWorker</c>
/// com base nos registros DANE (TLSA) e/ou na política MTA-STS do domínio destino.
/// </summary>
public sealed class TlsPolicy
{
    public static readonly TlsPolicy Opportunistic = new() { Mode = TlsEnforcementMode.Opportunistic };
    public static readonly TlsPolicy Required = new() { Mode = TlsEnforcementMode.Required };
    public static readonly TlsPolicy RequiredVerified = new() { Mode = TlsEnforcementMode.RequiredVerified };

    public TlsEnforcementMode Mode { get; init; } = TlsEnforcementMode.Opportunistic;

    /// <summary>
    /// Registros TLSA validados via DNSSEC (flag AD = true). Quando não-vazio, ativa validação DANE
    /// em vez de PKI padrão — o certificado do servidor deve coincidir com ao menos um registro.
    /// </summary>
    public IReadOnlyList<DaneTlsaAssociation>? DaneTlsaRecords { get; init; }

    /// <summary>True quando TLS é exigido e uma falha deve ser retornada como Transient.</summary>
    public bool RequiresTls => Mode != TlsEnforcementMode.Opportunistic;
}

/// <summary>
/// Representação interna de um registro TLSA (RFC 6698): desacoplada do DnsClient para
/// ser construída facilmente em testes e convertida pelo <see cref="DaneTlsaResolver"/>.
/// </summary>
public sealed class DaneTlsaAssociation
{
    // RFC 6698 §2.1 — valores numéricos alinhados ao padrão
    public const byte UsagePkixTa = 0;
    public const byte UsagePkixEe = 1;
    public const byte UsageDaneTa = 2;
    public const byte UsageDaneEe = 3;

    public const byte SelectorFullCert = 0;
    public const byte SelectorSpki = 1;

    public const byte MatchingExact = 0;
    public const byte MatchingSha256 = 1;
    public const byte MatchingSha512 = 2;

    public required byte Usage { get; init; }
    public required byte Selector { get; init; }
    public required byte MatchingType { get; init; }
    public required IReadOnlyList<byte> CertificateAssociationData { get; init; }
}

/// <summary>
/// Valida certificados X.509 contra registros DANE TLSA.
/// Suporta apenas DANE-EE (usage=3) — PKIXTA/PKIXEE/DANETA requerem validação de cadeia completa
/// e não estão implementados nesta versão.
/// </summary>
public static class TlsaValidation
{
    /// <summary>
    /// Retorna true se o certificado coincidir com ao menos um registro TLSA DANE-EE.
    /// Registros com outros usos (PKIXTA, PKIXEE, DANETA) são ignorados.
    /// </summary>
    public static bool Matches(X509Certificate2 cert, IReadOnlyList<DaneTlsaAssociation> records)
    {
        foreach (var r in records)
        {
            if (r.Usage != DaneTlsaAssociation.UsageDaneEe)
                continue;

            if (MatchesRecord(cert, r))
                return true;
        }
        return false;
    }

    private static bool MatchesRecord(X509Certificate2 cert, DaneTlsaAssociation record)
    {
        var data = record.Selector switch
        {
            DaneTlsaAssociation.SelectorFullCert => cert.RawData,
            DaneTlsaAssociation.SelectorSpki => GetSpki(cert),
            _ => null
        };

        if (data is null) return false;

        var expected = record.CertificateAssociationData.ToArray();
        return record.MatchingType switch
        {
            DaneTlsaAssociation.MatchingExact => data.AsSpan().SequenceEqual(expected),
            DaneTlsaAssociation.MatchingSha256 => SHA256.HashData(data).AsSpan().SequenceEqual(expected),
            DaneTlsaAssociation.MatchingSha512 => SHA512.HashData(data).AsSpan().SequenceEqual(expected),
            _ => false
        };
    }

    private static byte[]? GetSpki(X509Certificate2 cert)
    {
        try
        {
            using var rsa = cert.GetRSAPublicKey();
            if (rsa is not null) return rsa.ExportSubjectPublicKeyInfo();
            using var ecdsa = cert.GetECDsaPublicKey();
            if (ecdsa is not null) return ecdsa.ExportSubjectPublicKeyInfo();
        }
        catch { }
        return null;
    }
}
