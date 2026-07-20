using System.Text.Json;

namespace StrongMTA.Spool;

/// <summary>
/// Lê os arquivos do spool. <see cref="ReadEnvelopeAsync"/> lê só o cabeçalho (sem o corpo) —
/// essencial para o scanner de boot não carregar megabytes de corpo por mensagem na fila.
/// </summary>
public sealed class SpoolReader
{
    public async Task<MessageEnvelopeData> ReadEnvelopeAsync(string msgFilePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(msgFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var (envelope, _) = await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
        return envelope;
    }

    /// <summary>Abre o corpo RFC822 já posicionado após o envelope — o chamador deve descartar o stream.</summary>
    public async Task<Stream> OpenBodyStreamAsync(string msgFilePath, CancellationToken cancellationToken = default)
    {
        var stream = new FileStream(msgFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<MessageStateData?> ReadStateAsync(string stateFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(stateFilePath))
            return null;

        await using var stream = new FileStream(stateFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<MessageStateData>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<(MessageEnvelopeData Envelope, long BodyOffset)> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var magicBuffer = new byte[SpoolFormat.MsgMagic.Length];
        await ReadExactAsync(stream, magicBuffer, cancellationToken).ConfigureAwait(false);
        if (!magicBuffer.AsSpan().SequenceEqual(SpoolFormat.MsgMagic))
            throw new InvalidDataException("Arquivo .msg corrompido: magic header inválido.");

        var lengthBuffer = new byte[4];
        await ReadExactAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
        var envelopeLength = BitConverter.ToUInt32(lengthBuffer);

        var envelopeBuffer = new byte[envelopeLength];
        await ReadExactAsync(stream, envelopeBuffer, cancellationToken).ConfigureAwait(false);

        var envelope = JsonSerializer.Deserialize<MessageEnvelopeData>(envelopeBuffer)
            ?? throw new InvalidDataException("Envelope JSON vazio/inválido no .msg.");

        return (envelope, stream.Position);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("Arquivo .msg truncado (escrita incompleta ou corrupção).");
            read += n;
        }
    }
}
