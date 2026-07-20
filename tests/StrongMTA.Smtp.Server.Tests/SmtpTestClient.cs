using System.Net.Sockets;
using System.Text;

namespace StrongMTA.Smtp.Server.Tests;

/// <summary>Cliente SMTP cru minimalista para exercitar o <see cref="BounceListener"/> via socket real (sem MimeKit.Net no lado cliente).</summary>
public sealed class SmtpTestClient : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private NetworkStream? _stream;

    public async Task ConnectAsync(int port)
    {
        await _client.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
        _stream = _client.GetStream();
        await ReadLineAsync().ConfigureAwait(false); // banner
    }

    public async Task<string> SendCommandAsync(string command)
    {
        await WriteAsync(command + "\r\n").ConfigureAwait(false);
        return await ReadLineAsync().ConfigureAwait(false) ?? "";
    }

    /// <summary>Envia o corpo (aplicando dot-stuffing, como um cliente SMTP real faria) + terminador. Pressupõe que "DATA" já foi enviado via <see cref="SendDataStartAsync"/>. Retorna a resposta final.</summary>
    public async Task<string> SendBodyAsync(string rawBody)
    {
        var stuffed = StuffDots(rawBody);
        await WriteAsync(stuffed).ConfigureAwait(false);
        await WriteAsync(".\r\n").ConfigureAwait(false);
        return await ReadLineAsync().ConfigureAwait(false) ?? "";
    }

    public Task<string> SendDataStartAsync() => SendCommandAsync("DATA");

    private static string StuffDots(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var stuffed = lines.Select(l => l.StartsWith('.') ? "." + l : l);
        return string.Join("\r\n", stuffed) + "\r\n";
    }

    private async Task WriteAsync(string text) =>
        await _stream!.WriteAsync(Encoding.ASCII.GetBytes(text)).ConfigureAwait(false);

    private async Task<string?> ReadLineAsync()
    {
        var buffer = new List<byte>();
        var oneByte = new byte[1];
        while (true)
        {
            var read = await _stream!.ReadAsync(oneByte).ConfigureAwait(false);
            if (read == 0) return buffer.Count == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());
            if (oneByte[0] == (byte)'\n')
            {
                if (buffer.Count > 0 && buffer[^1] == (byte)'\r') buffer.RemoveAt(buffer.Count - 1);
                return Encoding.ASCII.GetString(buffer.ToArray());
            }
            buffer.Add(oneByte[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
            await _stream.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}
