using System.Security.Cryptography;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;

namespace StrongMTA.Dkim.Tests;

/// <summary>
/// Locator de chave pública 100% offline para testes: em vez de consultar DNS (TXT record
/// do selector), devolve diretamente a chave pública correspondente à privada usada para
/// assinar — permite verificar a corretude criptográfica da assinatura sem rede/DNS.
/// </summary>
internal sealed class InMemoryDkimPublicKeyLocator(RSA rsa) : DkimPublicKeyLocatorBase
{
    public override AsymmetricKeyParameter LocatePublicKey(string methodQuery, string domain, string selector, CancellationToken cancellationToken)
    {
        var parameters = rsa.ExportParameters(false);
        var modulus = new BigInteger(1, parameters.Modulus);
        var exponent = new BigInteger(1, parameters.Exponent);
        return new RsaKeyParameters(isPrivate: false, modulus, exponent);
    }

    public override Task<AsymmetricKeyParameter> LocatePublicKeyAsync(string methodQuery, string domain, string selector, CancellationToken cancellationToken) =>
        Task.FromResult(LocatePublicKey(methodQuery, domain, selector, cancellationToken));
}
