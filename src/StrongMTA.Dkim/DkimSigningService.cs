using MimeKit;
using MimeKit.Cryptography;

namespace StrongMTA.Dkim;

public interface IDkimSigningService
{
    /// <summary>
    /// Lê uma mensagem RFC822 crua, assina com DKIM se houver configuração para o domínio
    /// do remetente (header From:), e retorna o resultado serializado (assinado ou, se não
    /// houver chave configurada para o domínio, idêntico ao original — pass-through silencioso).
    /// </summary>
    Task<Stream> SignAsync(Stream rawRfc822, CancellationToken cancellationToken = default);
}

public sealed class DkimSigningService(IDkimKeyProvider keyProvider) : IDkimSigningService
{
    public async Task<Stream> SignAsync(Stream rawRfc822, CancellationToken cancellationToken = default)
    {
        var message = await MimeMessage.LoadAsync(rawRfc822, cancellationToken).ConfigureAwait(false);

        var senderDomain = ExtractSenderDomain(message);
        if (senderDomain is not null && keyProvider.TryGetConfig(senderDomain, out var config))
        {
            var signer = new DkimSigner(config.PrivateKeyPath, config.Domain, config.Selector, DkimSignatureAlgorithm.RsaSha256)
            {
                HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
                BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
            };
            signer.Sign(message, config.HeadersToSign.ToList());
        }

        var output = new MemoryStream();
        await message.WriteToAsync(output, cancellationToken).ConfigureAwait(false);
        output.Position = 0;
        return output;
    }

    private static string? ExtractSenderDomain(MimeMessage message)
    {
        var mailbox = message.From.Mailboxes.FirstOrDefault();
        if (mailbox is null)
            return null;

        var at = mailbox.Address.LastIndexOf('@');
        return at >= 0 && at < mailbox.Address.Length - 1 ? mailbox.Address[(at + 1)..] : null;
    }
}
