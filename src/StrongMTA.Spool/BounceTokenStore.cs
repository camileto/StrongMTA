using System.Text.Json;

namespace StrongMTA.Spool;

internal sealed class BounceTokenFileData
{
    public required Guid MessageId { get; init; }
}

/// <summary>
/// Índice persistido token VERP (= RecipientId) -> MessageId, escrito na submissão e consultado
/// pelo listener de bounce/FBL para correlacionar um DSN/ARF recebido de volta ao destinatário
/// original. Sobrevive a restart porque vive em disco, sharded como o resto do spool. Um arquivo
/// por destinatário evita qualquer contenção de escrita entre submissões concorrentes.
/// </summary>
public sealed class BounceTokenStore(SpoolPaths paths)
{
    public Task RegisterAsync(Guid recipientId, Guid messageId, CancellationToken cancellationToken = default)
    {
        paths.GetBounceTokenDirectory(recipientId, createIfMissing: true);
        return AtomicFile.WriteAsync(paths.GetBounceTokenFilePath(recipientId),
            (stream, ct) => JsonSerializer.SerializeAsync(stream, new BounceTokenFileData { MessageId = messageId }, cancellationToken: ct),
            cancellationToken);
    }

    public async Task<Guid?> ResolveMessageIdAsync(Guid recipientId, CancellationToken cancellationToken = default)
    {
        var filePath = paths.GetBounceTokenFilePath(recipientId);
        if (!File.Exists(filePath))
            return null;

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var data = await JsonSerializer.DeserializeAsync<BounceTokenFileData>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return data?.MessageId;
    }
}
