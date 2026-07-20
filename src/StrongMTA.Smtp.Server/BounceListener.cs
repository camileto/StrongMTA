using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;
using StrongMTA.Bounce;

namespace StrongMTA.Smtp.Server;

public sealed class BounceListenerOptions
{
    /// <summary>Domínio sob o qual endereços <c>bounce-&lt;recipientId&gt;@dominio</c> (gerados pelo SubmissionService) são aceitos.</summary>
    public required string BounceDomain { get; init; }

    /// <summary>Endereços fixos de feedback loop (ex.: "fbl@dominio") configurados com os provedores — comparação exata, case-insensitive.</summary>
    public IReadOnlySet<string> FeedbackLoopAddresses { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public string HostName { get; init; } = "localhost";

    /// <summary>Porta a escutar. 0 (padrão) deixa o SO escolher — útil em testes; produção deve fixar uma porta conhecida (ex.: 25).</summary>
    public int ListenPort { get; init; }
}

/// <summary>
/// Listener SMTP inbound minimalista, dedicado exclusivamente a receber DSN (bounces) e ARF
/// (feedback loop) — não é um MTA de recepção genérico: qualquer RCPT TO que não bata com os
/// padrões configurados é rejeitado com 550. Sempre responde 250 após DATA mesmo quando a
/// correlação falha (token desconhecido, relatório malformado) — loga para investigação em vez
/// de forçar o remoto a re-tentar, que é o comportamento esperado para mensagens que já são,
/// elas mesmas, notificações de erro.
/// </summary>
public sealed class BounceListener(BounceListenerOptions options, BounceCorrelationService correlationService)
{
    private readonly TcpListener _listener = new(IPAddress.Any, options.ListenPort);
    private Task? _acceptLoop;

    public int Port { get; private set; }

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public void Stop() => _listener.Stop();

    public async Task WaitForShutdownAsync()
    {
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (Exception) { /* listener parado via Stop() */ }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            _ = HandleConnectionAsync(client);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                await WriteAsync(stream, $"220 {options.HostName} ESMTP StrongMTA-Bounce\r\n").ConfigureAwait(false);

                var dsnRecipients = new List<Guid>();
                var hasFblRecipient = false;

                while (true)
                {
                    var line = await ReadLineAsync(stream).ConfigureAwait(false);
                    if (line is null) return;

                    if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(stream, $"250 {options.HostName}\r\n").ConfigureAwait(false);
                    }
                    else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(stream, "250 2.1.0 OK\r\n").ConfigureAwait(false);
                    }
                    else if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                    {
                        var address = ExtractAddress(line);
                        if (TryClassifyRecipient(address, out var recipientId, out var isFbl))
                        {
                            if (isFbl) hasFblRecipient = true;
                            else dsnRecipients.Add(recipientId);
                            await WriteAsync(stream, "250 2.1.5 OK\r\n").ConfigureAwait(false);
                        }
                        else
                        {
                            await WriteAsync(stream, "550 5.1.1 Mailbox unavailable\r\n").ConfigureAwait(false);
                        }
                    }
                    else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        if (dsnRecipients.Count == 0 && !hasFblRecipient)
                        {
                            await WriteAsync(stream, "554 5.5.1 No valid recipients\r\n").ConfigureAwait(false);
                            continue;
                        }

                        await WriteAsync(stream, "354 End data with <CRLF>.<CRLF>\r\n").ConfigureAwait(false);
                        var body = await ReadDotStuffedBodyAsync(stream).ConfigureAwait(false);

                        await ProcessReportAsync(body, dsnRecipients, hasFblRecipient).ConfigureAwait(false);

                        // sempre 250: este e-mail JÁ É uma notificação de falha/complaint — não queremos
                        // que o remoto reenvie por achar que falhou.
                        await WriteAsync(stream, "250 2.0.0 Message accepted\r\n").ConfigureAwait(false);
                        dsnRecipients.Clear();
                        hasFblRecipient = false;
                    }
                    else if (line.StartsWith("RSET", StringComparison.OrdinalIgnoreCase))
                    {
                        dsnRecipients.Clear();
                        hasFblRecipient = false;
                        await WriteAsync(stream, "250 2.0.0 OK\r\n").ConfigureAwait(false);
                    }
                    else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(stream, "221 2.0.0 Bye\r\n").ConfigureAwait(false);
                        return;
                    }
                    else
                    {
                        await WriteAsync(stream, "502 5.5.2 Command not recognized\r\n").ConfigureAwait(false);
                    }
                }
            }
            catch (IOException)
            {
                // conexão encerrada pelo remoto antes do esperado — sem ação corretiva possível
            }
        }
    }

    private async Task ProcessReportAsync(byte[] body, List<Guid> dsnRecipients, bool hasFblRecipient)
    {
        MimeMessage message;
        try
        {
            message = await MimeMessage.LoadAsync(new MemoryStream(body)).ConfigureAwait(false);
        }
        catch (FormatException)
        {
            return; // corpo malformado: loga (best-effort) e segue respondendo 250 ao remoto
        }

        foreach (var recipientId in dsnRecipients)
            await correlationService.ProcessDsnAsync(recipientId, message).ConfigureAwait(false);

        if (hasFblRecipient)
            await correlationService.ProcessArfAsync(message).ConfigureAwait(false);
    }

    private bool TryClassifyRecipient(string? address, out Guid recipientId, out bool isFbl)
    {
        recipientId = default;
        isFbl = false;

        if (address is null)
            return false;

        if (options.FeedbackLoopAddresses.Contains(address))
        {
            isFbl = true;
            return true;
        }

        if (VerpToken.TryExtractRecipientId(address, out recipientId))
        {
            var at = address.LastIndexOf('@');
            var domain = at >= 0 ? address[(at + 1)..] : "";
            return string.Equals(domain, options.BounceDomain, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string? ExtractAddress(string rcptToLine)
    {
        var start = rcptToLine.IndexOf('<');
        var end = rcptToLine.IndexOf('>');
        if (start >= 0 && end > start)
            return rcptToLine[(start + 1)..end];

        // sem colchetes: pega o token depois de "TO:" até o primeiro espaço (parâmetros ESMTP)
        var colon = rcptToLine.IndexOf(':');
        if (colon < 0) return null;
        var rest = rcptToLine[(colon + 1)..].Trim();
        var space = rest.IndexOf(' ');
        return space >= 0 ? rest[..space] : rest;
    }

    private static async Task<byte[]> ReadDotStuffedBodyAsync(Stream stream)
    {
        using var bodyStream = new MemoryStream();
        while (true)
        {
            var lineBytes = await ReadLineBytesAsync(stream).ConfigureAwait(false);
            if (lineBytes is null) break;

            if (lineBytes.Length == 1 && lineBytes[0] == (byte)'.')
                break;

            if (lineBytes.Length > 0 && lineBytes[0] == (byte)'.')
                await bodyStream.WriteAsync(lineBytes.AsMemory(1)).ConfigureAwait(false);
            else
                await bodyStream.WriteAsync(lineBytes).ConfigureAwait(false);

            await bodyStream.WriteAsync("\r\n"u8.ToArray()).ConfigureAwait(false);
        }
        return bodyStream.ToArray();
    }

    private static async Task<byte[]?> ReadLineBytesAsync(Stream stream)
    {
        var buffer = new List<byte>();
        var oneByte = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(oneByte, CancellationToken.None).ConfigureAwait(false);
            if (read == 0) return buffer.Count == 0 ? null : buffer.ToArray();
            if (oneByte[0] == (byte)'\n')
            {
                if (buffer.Count > 0 && buffer[^1] == (byte)'\r') buffer.RemoveAt(buffer.Count - 1);
                return buffer.ToArray();
            }
            buffer.Add(oneByte[0]);
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream)
    {
        var bytes = await ReadLineBytesAsync(stream).ConfigureAwait(false);
        return bytes is null ? null : Encoding.ASCII.GetString(bytes);
    }

    private static async Task WriteAsync(Stream stream, string text)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text)).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }
}
