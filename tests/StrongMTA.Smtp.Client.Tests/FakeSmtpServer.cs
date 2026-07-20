using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace StrongMTA.Smtp.Client.Tests;

/// <summary>
/// Servidor SMTP minimalista para testes: aceita uma conexão, segue um script fixo de
/// respostas para banner/EHLO/MAIL FROM/RCPT TO/DATA, e opcionalmente suporta STARTTLS.
/// Captura o corpo recebido durante DATA para asserções de dot-stuffing.
/// </summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private Task? _acceptTask;

    public string BannerResponse { get; set; } = "220 fake.test ESMTP\r\n";
    public string EhloResponse { get; set; } = "250-fake.test\r\n250 OK\r\n";
    public string MailFromResponse { get; set; } = "250 2.1.0 OK\r\n";
    public string RcptToResponse { get; set; } = "250 2.1.5 OK\r\n";
    public string DataStartResponse { get; set; } = "354 End data with <CRLF>.<CRLF>\r\n";
    public string FinalResponse { get; set; } = "250 2.0.0 Accepted\r\n";
    public string QuitResponse { get; set; } = "221 2.0.0 Bye\r\n";
    public bool SupportsStartTls { get; set; }
    public byte[]? ReceivedDataBody { get; private set; }
    public string? ReceivedEhloArgument { get; private set; }
    public IPEndPoint? RemoteEndPoint { get; private set; }

    /// <summary>Atraso artificial antes de responder ao FinalResponse — sem isso, conexões completam tão rápido que nunca chegam a se sobrepor de fato num teste de concorrência.</summary>
    public TimeSpan ArtificialDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Maior número de conexões simultaneamente ativas observado desde o Start() — usado para provar (ou refutar) que o scheduler respeitou um teto de concorrência.</summary>
    public int MaxConcurrentSeen => _maxConcurrentSeen;

    private int _activeConnections;
    private int _maxConcurrentSeen;
    private readonly List<Task> _connectionTasks = [];
    private readonly object _tasksGate = new();

    public int Port { get; }

    /// <summary>
    /// <paramref name="bindAddress"/>/<paramref name="port"/> permitem simular múltiplos "MX hosts"
    /// distintos em testes (várias instâncias, cada uma num endereço de loopback diferente,
    /// opcionalmente compartilhando o mesmo número de porta — necessário para skip-mx, onde o
    /// worker usa uma única porta fixa para todos os hosts candidatos).
    /// </summary>
    public FakeSmtpServer(IPAddress? bindAddress = null, int port = 0)
    {
        _listener = new TcpListener(bindAddress ?? IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Aceita conexões em loop (até o Dispose) — necessário para testes de retry, onde o
    /// mesmo destinatário gera várias tentativas/conexões sequenciais para o mesmo servidor,
    /// e para testes de concorrência, onde várias conexões simultâneas precisam ser atendidas
    /// em paralelo (cada conexão aceita dispara seu próprio atendimento fire-and-forget — um
    /// `await` aqui sequencializaria o atendimento e impediria qualquer overlap real).
    /// </summary>
    public void Start() => _acceptTask = AcceptLoopAsync();

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
                return; // listener parado (Dispose) ou erro fatal de socket: encerra o loop
            }

            var connectionTask = ServeConnectionAsync(client);
            lock (_tasksGate) _connectionTasks.Add(connectionTask);
        }
    }

    private async Task ServeConnectionAsync(TcpClient client)
    {
        var concurrent = Interlocked.Increment(ref _activeConnections);
        InterlockedMax(ref _maxConcurrentSeen, concurrent);
        using (client)
        {
            await ServeConnectionBodyAsync(client).ConfigureAwait(false);
        }
        Interlocked.Decrement(ref _activeConnections);
    }

    private static void InterlockedMax(ref int target, int candidate)
    {
        var current = target;
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private async Task ServeConnectionBodyAsync(TcpClient client)
    {
        RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        Stream stream = client.GetStream();
        try
        {
            await WriteAsync(stream, BannerResponse).ConfigureAwait(false);

            var line = await ReadLineAsync(stream).ConfigureAwait(false);
            if (line is null || !line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                return;
            ReceivedEhloArgument = line[5..].Trim();

            var ehlo = SupportsStartTls
                ? "250-fake.test\r\n250-STARTTLS\r\n250 OK\r\n"
                : EhloResponse;
            await WriteAsync(stream, ehlo).ConfigureAwait(false);

            line = await ReadLineAsync(stream).ConfigureAwait(false);
            if (SupportsStartTls && line is not null && line.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAsync(stream, "220 2.0.0 Ready to start TLS\r\n").ConfigureAwait(false);

                using var cert = CreateSelfSignedCertificate();
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(cert).ConfigureAwait(false);
                stream = ssl;

                line = await ReadLineAsync(stream).ConfigureAwait(false);
                if (line is null || !line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    return;
                await WriteAsync(stream, EhloResponse).ConfigureAwait(false);

                line = await ReadLineAsync(stream).ConfigureAwait(false);
            }

            if (line is null || !line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase))
                return;
            await WriteAsync(stream, MailFromResponse).ConfigureAwait(false);
            if (!MailFromResponse.StartsWith('2')) { await DrainQuitAsync(stream).ConfigureAwait(false); return; }

            line = await ReadLineAsync(stream).ConfigureAwait(false);
            if (line is null || !line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                return;
            await WriteAsync(stream, RcptToResponse).ConfigureAwait(false);
            if (!RcptToResponse.StartsWith('2')) { await DrainQuitAsync(stream).ConfigureAwait(false); return; }

            line = await ReadLineAsync(stream).ConfigureAwait(false);
            if (line is null || !line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                return;
            await WriteAsync(stream, DataStartResponse).ConfigureAwait(false);
            if (!DataStartResponse.StartsWith('3')) { await DrainQuitAsync(stream).ConfigureAwait(false); return; }

            ReceivedDataBody = await ReadUntilDotTerminatorAsync(stream).ConfigureAwait(false);
            if (ArtificialDelay > TimeSpan.Zero)
                await Task.Delay(ArtificialDelay).ConfigureAwait(false);
            await WriteAsync(stream, FinalResponse).ConfigureAwait(false);

            await DrainQuitAsync(stream).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // conexão encerrada pelo cliente antes do esperado - irrelevante para os testes que abortam cedo
        }
    }

    private async Task DrainQuitAsync(Stream stream)
    {
        var line = await ReadLineAsync(stream).ConfigureAwait(false);
        if (line is not null && line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
            await WriteAsync(stream, QuitResponse).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadUntilDotTerminatorAsync(Stream stream)
    {
        var buffer = new List<byte>();
        var terminator = "\r\n.\r\n"u8.ToArray();
        var tail = new byte[terminator.Length];
        var oneByte = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(oneByte, CancellationToken.None).ConfigureAwait(false);
            if (read == 0) break;
            buffer.Add(oneByte[0]);

            if (buffer.Count >= terminator.Length)
            {
                buffer.CopyTo(buffer.Count - terminator.Length, tail, 0, terminator.Length);
                if (tail.AsSpan().SequenceEqual(terminator))
                {
                    buffer.RemoveRange(buffer.Count - terminator.Length, terminator.Length);
                    break;
                }
            }
        }
        return buffer.ToArray();
    }

    private static async Task<string?> ReadLineAsync(Stream stream)
    {
        var line = new StringBuilder();
        var oneByte = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(oneByte, CancellationToken.None).ConfigureAwait(false);
            if (read == 0) return line.Length == 0 ? null : line.ToString();
            if (oneByte[0] == (byte)'\n')
            {
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                return line.ToString();
            }
            line.Append((char)oneByte[0]);
        }
    }

    private static async Task WriteAsync(Stream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=fake.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch { /* ignorar falhas de conexão já tratadas/abortadas no teste */ }
        }

        Task[] pending;
        lock (_tasksGate) pending = _connectionTasks.ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch { /* ignorar falhas de conexão já tratadas/abortadas no teste */ }
    }
}
