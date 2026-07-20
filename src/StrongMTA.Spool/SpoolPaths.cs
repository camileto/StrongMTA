namespace StrongMTA.Spool;

/// <summary>
/// Calcula o layout de diretórios do spool: sharding em 2 níveis (4 primeiros hex chars
/// do próprio GUID da mensagem, sem rehash) para evitar que um único diretório acumule
/// milhões de entradas — mesmo motivo por trás do layout de fila do Postfix.
/// </summary>
public sealed class SpoolPaths(string rootDirectory)
{
    public string RootDirectory => rootDirectory;

    public string QueueDirectory => Path.Combine(rootDirectory, "queue");
    public string BounceTokensDirectory => Path.Combine(rootDirectory, "bounce-tokens");
    public string ColdDirectory => Path.Combine(rootDirectory, "cold");
    public string AccountingDirectory => Path.Combine(rootDirectory, "accounting");

    /// <summary>Diretório sharded onde os arquivos .msg/.state de uma mensagem residem (criado se necessário).</summary>
    public string GetMessageDirectory(Guid messageId, bool createIfMissing = false)
    {
        var hex = messageId.ToString("N");
        var dir = Path.Combine(QueueDirectory, hex[..2], hex[2..4]);
        if (createIfMissing)
            Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetMsgFilePath(Guid messageId) =>
        Path.Combine(GetMessageDirectory(messageId), $"{messageId:N}.msg");

    public string GetStateFilePath(Guid messageId) =>
        Path.Combine(GetMessageDirectory(messageId), $"{messageId:N}.state");

    /// <summary>Diretório sharded (mesmo esquema de 2 níveis, agora pela hex do RecipientId/token) onde o índice de bounce token vive.</summary>
    public string GetBounceTokenDirectory(Guid recipientId, bool createIfMissing = false)
    {
        var hex = recipientId.ToString("N");
        var dir = Path.Combine(BounceTokensDirectory, hex[..2], hex[2..4]);
        if (createIfMissing)
            Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetBounceTokenFilePath(Guid recipientId) =>
        Path.Combine(GetBounceTokenDirectory(recipientId), $"{recipientId:N}.json");

    /// <summary>Caminho de arquivo temporário no MESMO diretório do destino final (requisito do rename atômico).</summary>
    public static string GetTempFilePath(string finalPath) =>
        Path.Combine(Path.GetDirectoryName(finalPath)!, $".tmp-{Guid.NewGuid():N}{Path.GetExtension(finalPath)}");
}
