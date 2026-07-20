namespace StrongMTA.Spool;

/// <summary>
/// Escrita atômica via write-temp (mesmo diretório) + fsync + rename. Garante que, em caso
/// de crash a qualquer momento, o arquivo final está sempre em um de dois estados válidos
/// (o antigo ou o novo) — nunca parcialmente escrito.
/// </summary>
public static class AtomicFile
{
    public static async Task WriteAsync(string finalPath, Func<Stream, CancellationToken, Task> writeBody, CancellationToken cancellationToken = default)
    {
        var tempPath = SpoolPaths.GetTempFilePath(finalPath);
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.WriteThrough))
            {
                await writeBody(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            // melhor esforço: limpa o .tmp se o processo não foi morto no meio do caminho.
            // se foi (kill -9), o scanner de boot remove órfãos "."tmp-*" remanescentes.
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
