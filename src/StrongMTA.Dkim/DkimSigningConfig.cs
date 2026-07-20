using MimeKit;

namespace StrongMTA.Dkim;

/// <summary>Configuração de assinatura DKIM para um domínio remetente (não por VirtualMta — DKIM valida contra o header From:).</summary>
public sealed class DkimSigningConfig
{
    public required string Domain { get; init; }
    public required string Selector { get; init; }

    /// <summary>Caminho do arquivo de chave privada (PEM, PKCS#1 ou PKCS#8).</summary>
    public required string PrivateKeyPath { get; init; }

    public IReadOnlyList<HeaderId> HeadersToSign { get; init; } =
        [HeaderId.From, HeaderId.To, HeaderId.Subject, HeaderId.Date, HeaderId.MessageId];
}

public interface IDkimKeyProvider
{
    bool TryGetConfig(string senderDomain, out DkimSigningConfig config);
}

/// <summary>Provider em memória: domínio remetente -> configuração de assinatura. Domínios sem entrada não são assinados.</summary>
public sealed class StaticDkimKeyProvider(IReadOnlyDictionary<string, DkimSigningConfig> configs) : IDkimKeyProvider
{
    public bool TryGetConfig(string senderDomain, out DkimSigningConfig config) =>
        configs.TryGetValue(senderDomain, out config!);
}
