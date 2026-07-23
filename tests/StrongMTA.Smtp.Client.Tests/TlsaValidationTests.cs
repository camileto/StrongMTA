using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StrongMTA.Smtp.Client;

namespace StrongMTA.Smtp.Client.Tests;

/// <summary>
/// Testes unitários puros de <see cref="TlsaValidation"/>: sem rede, sem DnsClient,
/// criando certs e registros TLSA diretamente.
/// </summary>
public class TlsaValidationTests
{
    private static X509Certificate2 CreateTestCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=test.local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(10));
    }

    [Fact]
    public void Matches_DaneEe_FullCert_Sha256_CorrectHash_ReturnsTrue()
    {
        using var cert = CreateTestCert();
        var hash = SHA256.HashData(cert.RawData);
        var record = new DaneTlsaAssociation
        {
            Usage = DaneTlsaAssociation.UsageDaneEe,
            Selector = DaneTlsaAssociation.SelectorFullCert,
            MatchingType = DaneTlsaAssociation.MatchingSha256,
            CertificateAssociationData = hash
        };

        Assert.True(TlsaValidation.Matches(cert, [record]));
    }

    [Fact]
    public void Matches_DaneEe_FullCert_Sha256_WrongHash_ReturnsFalse()
    {
        using var cert = CreateTestCert();
        var wrongHash = new byte[32]; // todos zeros — definitivamente errado
        var record = new DaneTlsaAssociation
        {
            Usage = DaneTlsaAssociation.UsageDaneEe,
            Selector = DaneTlsaAssociation.SelectorFullCert,
            MatchingType = DaneTlsaAssociation.MatchingSha256,
            CertificateAssociationData = wrongHash
        };

        Assert.False(TlsaValidation.Matches(cert, [record]));
    }

    [Fact]
    public void Matches_DaneEe_Spki_Sha256_CorrectHash_ReturnsTrue()
    {
        using var cert = CreateTestCert();
        using var rsa = cert.GetRSAPublicKey()!;
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(spki);

        var record = new DaneTlsaAssociation
        {
            Usage = DaneTlsaAssociation.UsageDaneEe,
            Selector = DaneTlsaAssociation.SelectorSpki,
            MatchingType = DaneTlsaAssociation.MatchingSha256,
            CertificateAssociationData = hash
        };

        Assert.True(TlsaValidation.Matches(cert, [record]));
    }

    [Fact]
    public void Matches_DaneEe_FullCert_ExactMatch_ReturnsTrue()
    {
        using var cert = CreateTestCert();
        var record = new DaneTlsaAssociation
        {
            Usage = DaneTlsaAssociation.UsageDaneEe,
            Selector = DaneTlsaAssociation.SelectorFullCert,
            MatchingType = DaneTlsaAssociation.MatchingExact,
            CertificateAssociationData = cert.RawData
        };

        Assert.True(TlsaValidation.Matches(cert, [record]));
    }

    [Fact]
    public void Matches_NonDaneEeUsage_Ignored_ReturnsFalse()
    {
        // PKIX-TA (usage=0), PKIX-EE (usage=1) e DANE-TA (usage=2) não são implementados —
        // registros com esses usos são ignorados e não contam como match.
        using var cert = CreateTestCert();
        var hash = SHA256.HashData(cert.RawData);

        foreach (var usage in new byte[] { 0, 1, 2 })
        {
            var record = new DaneTlsaAssociation
            {
                Usage = usage,
                Selector = DaneTlsaAssociation.SelectorFullCert,
                MatchingType = DaneTlsaAssociation.MatchingSha256,
                CertificateAssociationData = hash
            };
            Assert.False(TlsaValidation.Matches(cert, [record]));
        }
    }

    [Fact]
    public void Matches_EmptyRecordList_ReturnsFalse()
    {
        using var cert = CreateTestCert();
        Assert.False(TlsaValidation.Matches(cert, []));
    }

    [Fact]
    public void Matches_MultipleRecords_ReturnsTrueIfAnyMatches()
    {
        using var cert = CreateTestCert();
        var correctHash = SHA256.HashData(cert.RawData);
        var wrongHash = new byte[32];

        var records = new List<DaneTlsaAssociation>
        {
            new() { Usage = DaneTlsaAssociation.UsageDaneEe, Selector = DaneTlsaAssociation.SelectorFullCert, MatchingType = DaneTlsaAssociation.MatchingSha256, CertificateAssociationData = wrongHash },
            new() { Usage = DaneTlsaAssociation.UsageDaneEe, Selector = DaneTlsaAssociation.SelectorFullCert, MatchingType = DaneTlsaAssociation.MatchingSha256, CertificateAssociationData = correctHash }
        };

        Assert.True(TlsaValidation.Matches(cert, records));
    }
}
