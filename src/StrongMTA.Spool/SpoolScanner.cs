namespace StrongMTA.Spool;

public sealed record SpoolMessageRecord(MessageEnvelopeData Envelope, MessageStateData State, string MsgFilePath, string StateFilePath);

/// <summary>
/// Reconstrói o índice em memória a partir do spool no boot: percorre queue/*/*/*.msg,
/// lê só o cabeçalho de cada um, casa com o .state correspondente (reconstruindo um
/// estado default se ausente — crash entre escrever .msg e o .state inicial), e limpa
/// arquivos ".tmp-*" órfãos deixados por uma escrita atômica interrompida por crash.
/// </summary>
public sealed class SpoolScanner(SpoolPaths paths, SpoolReader reader)
{
    public async IAsyncEnumerable<SpoolMessageRecord> ScanAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.QueueDirectory))
            yield break;

        CleanOrphanTempFiles(paths.QueueDirectory);

        foreach (var msgFilePath in Directory.EnumerateFiles(paths.QueueDirectory, "*.msg", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            MessageEnvelopeData envelope;
            try
            {
                envelope = await reader.ReadEnvelopeAsync(msgFilePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                // .msg corrompido/truncado (não deveria acontecer com escrita atômica, mas não derruba o boot por isso)
                continue;
            }

            var statePath = Path.ChangeExtension(msgFilePath, ".state");
            var state = await reader.ReadStateAsync(statePath, cancellationToken).ConfigureAwait(false)
                ?? BuildDefaultState(envelope);

            yield return new SpoolMessageRecord(envelope, state, msgFilePath, statePath);
        }
    }

    private static MessageStateData BuildDefaultState(MessageEnvelopeData envelope) => new()
    {
        MessageId = envelope.MessageId,
        Recipients = envelope.Recipients.Select(r => new RecipientStateData
        {
            RecipientId = r.RecipientId,
            NextAttemptAt = envelope.SubmittedAt
        }).ToList()
    };

    private static void CleanOrphanTempFiles(string queueDirectory)
    {
        foreach (var tempFile in Directory.EnumerateFiles(queueDirectory, ".tmp-*", SearchOption.AllDirectories))
        {
            try { File.Delete(tempFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
