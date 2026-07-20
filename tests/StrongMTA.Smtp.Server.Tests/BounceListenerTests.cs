using StrongMTA.Accounting;
using StrongMTA.Bounce;
using StrongMTA.Core;
using StrongMTA.Spool;
using Xunit;

namespace StrongMTA.Smtp.Server.Tests;

public class BounceListenerTests : IDisposable
{
    private const string BounceDomain = "bounce.strongmta.test";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "strongmta-smtpserver-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SpoolPaths _paths;
    private readonly SpoolWriter _writer;
    private readonly SpoolReader _reader = new();
    private readonly SpoolStateUpdater _stateUpdater;
    private readonly BounceTokenStore _bounceTokenStore;
    private readonly InMemoryAccountingSink _accounting = new();
    private readonly BounceListener _listener;

    public BounceListenerTests()
    {
        _paths = new SpoolPaths(_root);
        _writer = new SpoolWriter(_paths);
        _stateUpdater = new SpoolStateUpdater(_paths, _reader, _writer);
        _bounceTokenStore = new BounceTokenStore(_paths);
        var domainConfigProvider = new StaticDomainConfigProvider(new DomainConfig
        {
            DomainName = "example.com",
            RetryIntervals = [TimeSpan.FromMinutes(1)],
            BounceAfter = TimeSpan.FromHours(1)
        });
        var correlationService = new BounceCorrelationService(_bounceTokenStore, _stateUpdater, _accounting, new BounceCategoryClassifier(),
            _paths, _reader, domainConfigProvider, new ResponseRuleEngine());

        _listener = new BounceListener(
            new BounceListenerOptions
            {
                BounceDomain = BounceDomain,
                FeedbackLoopAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"fbl@{BounceDomain}" },
                HostName = "bounce-test.strongmta.test"
            },
            correlationService);
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Stop();
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }

    private async Task<(Guid MessageId, Guid RecipientId)> SeedMessageAsync()
    {
        var messageId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();

        var envelope = new MessageEnvelopeData
        {
            MessageId = messageId,
            SubmittedAt = DateTimeOffset.UtcNow,
            Recipients = [new RecipientEnvelopeData
            {
                RecipientId = recipientId,
                Address = "destino@example.com",
                EnvelopeFrom = VerpToken.Format(recipientId, BounceDomain),
                DestinationDomain = "example.com",
                VirtualMtaName = "vmta-01"
            }]
        };
        using var body = new MemoryStream("Subject: x\r\n\r\ncorpo\r\n"u8.ToArray());
        await _writer.WriteMessageAsync(envelope, body);

        await _writer.WriteStateAsync(new MessageStateData
        {
            MessageId = messageId,
            Recipients = [new RecipientStateData { RecipientId = recipientId, Status = RecipientStatus.Transient, NextAttemptAt = DateTimeOffset.UtcNow }]
        });

        await _bounceTokenStore.RegisterAsync(recipientId, messageId);
        return (messageId, recipientId);
    }

    private static string BuildDsn(string verpAddress, string action = "failed", string status = "5.1.1", string diagnostic = "smtp; 550 5.1.1 User unknown") =>
        "From: Mail Delivery Subsystem <mailer-daemon@mx.example.com>\r\n" +
        $"To: <{verpAddress}>\r\nSubject: DSN\r\n" +
        "Content-Type: multipart/report; report-type=delivery-status; boundary=\"B1\"\r\nMIME-Version: 1.0\r\n\r\n" +
        "--B1\r\nContent-Type: text/plain\r\n\r\nfalhou. Esta linha comeca com ponto:\r\n.escondida\r\n\r\n" +
        "--B1\r\nContent-Type: message/delivery-status\r\n\r\nReporting-MTA: dns; mx.example.com\r\n\r\n" +
        $"Final-Recipient: rfc822; destino@example.com\r\nAction: {action}\r\nStatus: {status}\r\nDiagnostic-Code: {diagnostic}\r\n\r\n" +
        "--B1--\r\n";

    private static string BuildArf(string feedbackType, string originalMailFrom) =>
        "From: <abuse@example.com>\r\nTo: <fbl@bounce.strongmta.test>\r\nSubject: complaint\r\n" +
        "Content-Type: multipart/report; report-type=feedback-report; boundary=\"B2\"\r\nMIME-Version: 1.0\r\n\r\n" +
        "--B2\r\nContent-Type: text/plain\r\n\r\nreport.\r\n\r\n" +
        "--B2\r\nContent-Type: message/feedback-report\r\n\r\n" +
        $"Feedback-Type: {feedbackType}\r\nOriginal-Mail-From: <{originalMailFrom}>\r\nOriginal-Rcpt-To: <destino@example.com>\r\n\r\n" +
        "--B2--\r\n";

    [Fact]
    public async Task FullSession_ValidDsnRecipient_CorrelatesAndMarksBounced_AndAlwaysReturns250()
    {
        var (messageId, recipientId) = await SeedMessageAsync();
        var verpAddress = VerpToken.Format(recipientId, BounceDomain);

        await using var client = new SmtpTestClient();
        await client.ConnectAsync(_listener.Port);

        Assert.StartsWith("250", await client.SendCommandAsync("EHLO mx.example.com"));
        Assert.StartsWith("250", await client.SendCommandAsync("MAIL FROM:<>"));
        Assert.StartsWith("250", await client.SendCommandAsync($"RCPT TO:<{verpAddress}>"));
        Assert.StartsWith("354", await client.SendDataStartAsync());

        var response = await client.SendBodyAsync(BuildDsn(verpAddress));

        Assert.StartsWith("250", response);

        var state = await _reader.ReadStateAsync(_paths.GetStateFilePath(messageId));
        Assert.Equal(RecipientStatus.Bounced, state!.Recipients[0].Status);
        var evt = Assert.Single(_accounting.Events);
        Assert.Equal(AccountingEventType.RemoteBounce, evt.Type);
        Assert.Equal(BounceCategory.BadMailbox, evt.Category);

        await client.SendCommandAsync("QUIT");
    }

    [Fact]
    public async Task RcptTo_AddressNotMatchingAnyPattern_Returns550_ButSessionContinues()
    {
        await using var client = new SmtpTestClient();
        await client.ConnectAsync(_listener.Port);

        await client.SendCommandAsync("EHLO mx.example.com");
        await client.SendCommandAsync("MAIL FROM:<>");
        var rcptResponse = await client.SendCommandAsync("RCPT TO:<qualquer-coisa@bounce.strongmta.test>");

        Assert.StartsWith("550", rcptResponse);

        // sessão continua viva - outro RCPT válido na MESMA conexão ainda deve funcionar
        var (_, recipientId) = await SeedMessageAsync();
        var verpAddress = VerpToken.Format(recipientId, BounceDomain);
        var secondRcpt = await client.SendCommandAsync($"RCPT TO:<{verpAddress}>");
        Assert.StartsWith("250", secondRcpt);

        await client.SendCommandAsync("QUIT");
    }

    [Fact]
    public async Task Data_WithoutAnyValidRecipient_Returns554()
    {
        await using var client = new SmtpTestClient();
        await client.ConnectAsync(_listener.Port);

        await client.SendCommandAsync("EHLO mx.example.com");
        await client.SendCommandAsync("MAIL FROM:<>");
        await client.SendCommandAsync("RCPT TO:<invalido@bounce.strongmta.test>");

        var response = await client.SendCommandAsync("DATA");

        Assert.StartsWith("554", response);
    }

    [Fact]
    public async Task FullSession_MalformedBody_StillReturns250_AndDoesNotChangeState()
    {
        var (messageId, recipientId) = await SeedMessageAsync();
        var verpAddress = VerpToken.Format(recipientId, BounceDomain);

        await using var client = new SmtpTestClient();
        await client.ConnectAsync(_listener.Port);

        await client.SendCommandAsync("EHLO mx.example.com");
        await client.SendCommandAsync("MAIL FROM:<>");
        await client.SendCommandAsync($"RCPT TO:<{verpAddress}>");
        await client.SendDataStartAsync();

        var response = await client.SendBodyAsync("isto nao e um e-mail rfc822 valido nem de longe {{{");

        Assert.StartsWith("250", response);
        var state = await _reader.ReadStateAsync(_paths.GetStateFilePath(messageId));
        Assert.Equal(RecipientStatus.Transient, state!.Recipients[0].Status); // sem correlação possível, sem mudança de estado
    }

    [Fact]
    public async Task FullSession_ArfToFixedFblAddress_CorrelatesViaOriginalMailFromInsideBody()
    {
        var (messageId, recipientId) = await SeedMessageAsync();
        var verpAddress = VerpToken.Format(recipientId, BounceDomain);

        await using var client = new SmtpTestClient();
        await client.ConnectAsync(_listener.Port);

        await client.SendCommandAsync("EHLO mx.example.com");
        await client.SendCommandAsync("MAIL FROM:<>");
        Assert.StartsWith("250", await client.SendCommandAsync($"RCPT TO:<fbl@{BounceDomain}>"));
        await client.SendDataStartAsync();

        var response = await client.SendBodyAsync(BuildArf("abuse", verpAddress));

        Assert.StartsWith("250", response);
        var evt = Assert.Single(_accounting.Events);
        Assert.Equal(AccountingEventType.RemoteFeedback, evt.Type);
        Assert.Equal(messageId, evt.MessageId);

        // FBL não é falha de entrega: estado de entrega não deve ser alterado
        var state = await _reader.ReadStateAsync(_paths.GetStateFilePath(messageId));
        Assert.Equal(RecipientStatus.Transient, state!.Recipients[0].Status);
    }
}
