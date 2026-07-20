using MimeKit;

namespace StrongMTA.Bounce;

/// <summary>
/// Codifica/decodifica o token VERP usado como local-part do EnvelopeFrom gerado pelo
/// <c>SubmissionService</c>: <c>bounce-&lt;recipientId:N&gt;@bounceDomain</c>. O token É o
/// próprio RecipientId (sem indireção extra) — basta resolvê-lo contra o
/// <c>BounceTokenStore</c> para achar a mensagem correspondente.
/// </summary>
public static class VerpToken
{
    private const string Prefix = "bounce-";

    public static string Format(Guid recipientId, string bounceDomain) =>
        $"{Prefix}{recipientId:N}@{bounceDomain}";

    /// <summary>Aceita tanto um endereço puro quanto um valor de cabeçalho RFC 822 (com nome/colchetes).</summary>
    public static bool TryExtractRecipientId(string? address, out Guid recipientId)
    {
        recipientId = default;
        if (string.IsNullOrWhiteSpace(address))
            return false;

        if (!MailboxAddress.TryParse(address, out var mailbox))
            return false;

        var localPart = mailbox.Address.Split('@')[0];
        if (!localPart.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return Guid.TryParseExact(localPart[Prefix.Length..], "N", out recipientId);
    }
}
