using System.Text.Json;

namespace StrongMTA.Spool;

/// <summary>
/// Escreve os arquivos .msg (envelope + corpo, imutável) e .state (estado mutável por
/// destinatário) do spool, sempre via escrita atômica (<see cref="AtomicFile"/>).
/// </summary>
public sealed class SpoolWriter(SpoolPaths paths)
{
    /// <summary>
    /// Escreve o arquivo .msg: magic + tamanho do envelope JSON + envelope JSON + corpo RFC822.
    /// O corpo nunca é reescrito depois — apenas o .state muda entre tentativas.
    /// </summary>
    public async Task<string> WriteMessageAsync(MessageEnvelopeData envelope, Stream body, CancellationToken cancellationToken = default)
    {
        var finalPath = paths.GetMsgFilePath(envelope.MessageId);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        var envelopeJson = JsonSerializer.SerializeToUtf8Bytes(envelope);

        await AtomicFile.WriteAsync(finalPath, async (stream, ct) =>
        {
            await stream.WriteAsync(SpoolFormat.MsgMagic, ct).ConfigureAwait(false);

            var lengthPrefix = BitConverter.GetBytes((uint)envelopeJson.Length);
            await stream.WriteAsync(lengthPrefix, ct).ConfigureAwait(false);

            await stream.WriteAsync(envelopeJson, ct).ConfigureAwait(false);

            await body.CopyToAsync(stream, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return finalPath;
    }

    /// <summary>Reescreve o .state inteiro (todos os destinatários) de forma atômica.</summary>
    public async Task WriteStateAsync(MessageStateData state, CancellationToken cancellationToken = default)
    {
        var finalPath = paths.GetStateFilePath(state.MessageId);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        var json = JsonSerializer.SerializeToUtf8Bytes(state);

        await AtomicFile.WriteAsync(finalPath, (stream, ct) => stream.WriteAsync(json, ct).AsTask(), cancellationToken)
            .ConfigureAwait(false);
    }
}
