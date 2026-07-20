using System.Text;

namespace StrongMTA.Smtp.Client;

/// <summary>
/// Leitor de linhas/respostas SMTP sobre um <see cref="Stream"/> bruto. Não usa
/// StreamReader porque o stream subjacente é trocado no upgrade STARTTLS (NetworkStream
/// -&gt; SslStream) e um StreamReader manteria buffer interno inválido após a troca.
/// </summary>
public sealed class SmtpResponseReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[4096];
    private int _bufferStart;
    private int _bufferLength;

    /// <summary>Lê uma linha terminada em CRLF (ou LF tolerado), sem o terminador.</summary>
    public async Task<string> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_bufferStart >= _bufferLength)
            {
                _bufferLength = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                _bufferStart = 0;
                if (_bufferLength == 0)
                    throw new IOException("Conexão SMTP encerrada pelo remoto antes do fim da linha.");
            }

            var b = _buffer[_bufferStart++];
            if (b == (byte)'\n')
            {
                if (line.Length > 0 && line[^1] == '\r')
                    line.Length--;
                return line.ToString();
            }

            line.Append((char)b);
        }
    }

    /// <summary>
    /// Lê uma resposta SMTP completa, possivelmente multilinha (RFC 5321 4.2.1):
    /// linhas de continuação têm "-" após o código, a última linha tem espaço.
    /// </summary>
    public async Task<SmtpResponse> ReadResponseAsync(CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        while (true)
        {
            var raw = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (raw.Length < 4 || !int.TryParse(raw.AsSpan(0, 3), out var code))
                throw new IOException($"Resposta SMTP malformada: \"{raw}\"");

            lines.Add(raw[4..]);

            var isLast = raw[3] == ' ';
            if (isLast)
                return new SmtpResponse(code, lines);
        }
    }

    /// <summary>Descarta qualquer dado ainda bufferizado (necessário antes do upgrade STARTTLS).</summary>
    public void DiscardBuffer()
    {
        _bufferStart = 0;
        _bufferLength = 0;
    }
}
