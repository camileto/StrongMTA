using System.Security.Cryptography;
using System.Text;
using MimeKit;
using MimeKit.Cryptography;

namespace StrongMTA.Dkim.Tests;

public class DkimSigningServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "strongmta-dkim-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _rsa = RSA.Create(2048);

    public DkimSigningServiceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        _rsa.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteKeyFile()
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, _rsa.ExportRSAPrivateKeyPem());
        return path;
    }

    private const string RawMessage =
        "From: Sender <sender@example.com>\r\n" +
        "To: dest@other.com\r\n" +
        "Subject: Teste DKIM\r\n" +
        "Date: Thu, 25 Jun 2026 12:00:00 +0000\r\n" +
        "Message-Id: <abc123@example.com>\r\n" +
        "\r\n" +
        "Corpo de teste do StrongMTA.\r\n";

    [Fact]
    public async Task SignAsync_DomainConfigured_AddsDkimSignatureHeader_ThatVerifiesOfflineAgainstThePublicKey()
    {
        var config = new DkimSigningConfig { Domain = "example.com", Selector = "default", PrivateKeyPath = WriteKeyFile() };
        var provider = new StaticDkimKeyProvider(new Dictionary<string, DkimSigningConfig> { ["example.com"] = config });
        var service = new DkimSigningService(provider);

        using var input = new MemoryStream(Encoding.ASCII.GetBytes(RawMessage));
        await using var signedStream = await service.SignAsync(input);
        var signedMessage = await MimeMessage.LoadAsync(signedStream);

        var dkimHeader = signedMessage.Headers.FirstOrDefault(h => h.Id == HeaderId.DkimSignature);
        Assert.NotNull(dkimHeader);
        Assert.Contains("d=example.com", dkimHeader!.Value);
        Assert.Contains("s=default", dkimHeader.Value);

        var verifier = new DkimVerifier(new InMemoryDkimPublicKeyLocator(_rsa));
        var valid = await verifier.VerifyAsync(signedMessage, dkimHeader, CancellationToken.None);

        Assert.True(valid, "a assinatura deveria validar contra a chave pública correspondente à privada usada para assinar");
    }

    [Fact]
    public async Task SignAsync_DomainNotConfigured_ReturnsMessageUnmodified_NoSignatureAdded()
    {
        var provider = new StaticDkimKeyProvider(new Dictionary<string, DkimSigningConfig>()); // sem nenhum domínio configurado
        var service = new DkimSigningService(provider);

        using var input = new MemoryStream(Encoding.ASCII.GetBytes(RawMessage));
        await using var resultStream = await service.SignAsync(input);
        var resultMessage = await MimeMessage.LoadAsync(resultStream);

        Assert.DoesNotContain(resultMessage.Headers, h => h.Id == HeaderId.DkimSignature);
        Assert.Equal("Teste DKIM", resultMessage.Subject);
    }

    [Fact]
    public async Task SignAsync_TamperedBodyAfterSigning_FailsVerification()
    {
        var config = new DkimSigningConfig { Domain = "example.com", Selector = "default", PrivateKeyPath = WriteKeyFile() };
        var provider = new StaticDkimKeyProvider(new Dictionary<string, DkimSigningConfig> { ["example.com"] = config });
        var service = new DkimSigningService(provider);

        using var input = new MemoryStream(Encoding.ASCII.GetBytes(RawMessage));
        await using var signedStream = await service.SignAsync(input);
        var signedMessage = await MimeMessage.LoadAsync(signedStream);
        var dkimHeader = signedMessage.Headers.First(h => h.Id == HeaderId.DkimSignature);

        // adultera o corpo depois de assinado - a verificação deve detectar a violação de integridade
        signedMessage.Body = new TextPart("plain") { Text = "Corpo adulterado depois da assinatura." };

        var verifier = new DkimVerifier(new InMemoryDkimPublicKeyLocator(_rsa));
        var valid = await verifier.VerifyAsync(signedMessage, dkimHeader, CancellationToken.None);

        Assert.False(valid, "adulterar o corpo depois de assinado deveria invalidar a assinatura DKIM");
    }

    [Fact]
    public async Task SignAsync_WrongPublicKey_FailsVerification()
    {
        var config = new DkimSigningConfig { Domain = "example.com", Selector = "default", PrivateKeyPath = WriteKeyFile() };
        var provider = new StaticDkimKeyProvider(new Dictionary<string, DkimSigningConfig> { ["example.com"] = config });
        var service = new DkimSigningService(provider);

        using var input = new MemoryStream(Encoding.ASCII.GetBytes(RawMessage));
        await using var signedStream = await service.SignAsync(input);
        var signedMessage = await MimeMessage.LoadAsync(signedStream);
        var dkimHeader = signedMessage.Headers.First(h => h.Id == HeaderId.DkimSignature);

        using var anotherRsa = RSA.Create(2048); // chave diferente da usada para assinar
        var verifier = new DkimVerifier(new InMemoryDkimPublicKeyLocator(anotherRsa));
        var valid = await verifier.VerifyAsync(signedMessage, dkimHeader, CancellationToken.None);

        Assert.False(valid);
    }
}
