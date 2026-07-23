using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StrongMTA.Smtp.Client;

namespace StrongMTA.Smtp.Client.Tests;

/// <summary>
/// Testes de integração de TLS no <see cref="SmtpDeliveryClient"/> contra o <see cref="FakeSmtpServer"/>.
/// Verificam que a política TLS (RequiredVerified, DANE com hash correto/errado) é aplicada corretamente.
/// </summary>
public class SmtpDeliveryClientTlsTests
{
    private static SmtpDeliveryRequest BuildRequest(int port, TlsPolicy tlsPolicy) => new()
    {
        TargetHost = "127.0.0.1",
        TargetPort = port,
        HeloHostName = "test.local",
        EnvelopeFrom = "from@test.local",
        RecipientAddress = "to@fake.test",
        OpenBodyStream = _ => Task.FromResult<Stream>(new MemoryStream("test body"u8.ToArray())),
        TlsPolicy = tlsPolicy
    };

    [Fact]
    public async Task RequiredVerified_FailsAgainstSelfSignedCert()
    {
        // Certificado auto-assinado não passa na validação PKI padrão → Transient
        await using var server = new FakeSmtpServer { SupportsStartTls = true };
        server.Start();

        var result = await new SmtpDeliveryClient().SendAsync(BuildRequest(server.Port, TlsPolicy.RequiredVerified));

        Assert.Equal(SmtpDeliveryOutcome.Transient, result.Outcome);
        Assert.Contains("STARTTLS", result.ErrorDetail ?? "");
    }

    [Fact]
    public async Task DaneWithCorrectHash_DeliversSuccessfully()
    {
        // Cert conhecido de antemão → hash SHA-256 do RawData → TLSA DANE-EE FullCert SHA-256 → entrega OK
        using var rsa = RSA.Create(2048);
        var certReq = new CertificateRequest("CN=test.local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(10));

        await using var server = new FakeSmtpServer { SupportsStartTls = true, Certificate = cert };
        server.Start();

        var hash = SHA256.HashData(cert.RawData);
        var tlsaRecord = new DaneTlsaAssociation
        {
            Usage = DaneTlsaAssociation.UsageDaneEe,
            Selector = DaneTlsaAssociation.SelectorFullCert,
            MatchingType = DaneTlsaAssociation.MatchingSha256,
            CertificateAssociationData = hash
        };

        var policy = new TlsPolicy { Mode = TlsEnforcementMode.Required, DaneTlsaRecords = [tlsaRecord] };
        var result = await new SmtpDeliveryClient().SendAsync(BuildRequest(server.Port, policy));

        Assert.Equal(SmtpDeliveryOutcome.Delivered, result.Outcome);
        Assert.True(result.UsedStartTls);
    }

    [Fact]
    public async Task DaneWithWrongHash_FailsTlsHandshake()
    {
        // Hash incorreto → callback retorna false → AuthenticationException → Transient
        await using var server = new FakeSmtpServer { SupportsStartTls = true };
        server.Start();

        var wrongHash = new byte[32]; // todos zeros
        var tlsaRecord = new DaneTlsaAssociation
        {
            Usage = DaneTlsaAssociation.UsageDaneEe,
            Selector = DaneTlsaAssociation.SelectorFullCert,
            MatchingType = DaneTlsaAssociation.MatchingSha256,
            CertificateAssociationData = wrongHash
        };

        var policy = new TlsPolicy { Mode = TlsEnforcementMode.Required, DaneTlsaRecords = [tlsaRecord] };
        var result = await new SmtpDeliveryClient().SendAsync(BuildRequest(server.Port, policy));

        Assert.Equal(SmtpDeliveryOutcome.Transient, result.Outcome);
    }

    [Fact]
    public async Task Opportunistic_StillDeliversWithSelfSignedCert()
    {
        // TlsPolicy padrão não altera o comportamento oportunista existente
        await using var server = new FakeSmtpServer { SupportsStartTls = true };
        server.Start();

        var result = await new SmtpDeliveryClient().SendAsync(BuildRequest(server.Port, TlsPolicy.Opportunistic));

        Assert.Equal(SmtpDeliveryOutcome.Delivered, result.Outcome);
        Assert.True(result.UsedStartTls);
    }
}
