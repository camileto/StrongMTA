using System.Text;
using MimeKit;
using StrongMTA.Accounting;
using StrongMTA.Core;
using StrongMTA.Spool;

namespace StrongMTA.Bounce.Tests;

public class BounceCorrelationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "strongmta-bounce-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SpoolPaths _paths;
    private readonly SpoolWriter _writer;
    private readonly SpoolReader _reader = new();
    private readonly SpoolStateUpdater _stateUpdater;
    private readonly BounceTokenStore _bounceTokenStore;
    private readonly InMemoryAccountingSink _accounting = new();
    private readonly BounceCorrelationService _service;

    public BounceCorrelationServiceTests()
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
        _service = new BounceCorrelationService(_bounceTokenStore, _stateUpdater, _accounting, new BounceCategoryClassifier(),
            _paths, _reader, domainConfigProvider, new ResponseRuleEngine());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }

    private async Task<(Guid MessageId, Guid RecipientId)> SeedMessageAsync(RecipientStatus initialStatus = RecipientStatus.Transient)
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
                EnvelopeFrom = VerpToken.Format(recipientId, "bounce.strongmta.test"),
                DestinationDomain = "example.com",
                VirtualMtaName = "vmta-01"
            }]
        };
        using var body = new MemoryStream("Subject: x\r\n\r\ncorpo\r\n"u8.ToArray());
        await _writer.WriteMessageAsync(envelope, body);

        await _writer.WriteStateAsync(new MessageStateData
        {
            MessageId = messageId,
            Recipients = [new RecipientStateData { RecipientId = recipientId, Status = initialStatus, NextAttemptAt = DateTimeOffset.UtcNow }]
        });

        await _bounceTokenStore.RegisterAsync(recipientId, messageId);

        return (messageId, recipientId);
    }

    private static MimeMessage LoadDsn(string action, string status, string diagnosticCode)
    {
        var raw =
            "From: Mail Delivery Subsystem <mailer-daemon@mailhost1.example.com>\r\n" +
            "To: <bounce-x@bouncedomain.test>\r\nSubject: DSN\r\n" +
            "Content-Type: multipart/report; report-type=delivery-status; boundary=\"B1\"\r\nMIME-Version: 1.0\r\n\r\n" +
            "--B1\r\nContent-Type: text/plain\r\n\r\nfalhou.\r\n\r\n" +
            "--B1\r\nContent-Type: message/delivery-status\r\n\r\nReporting-MTA: dns; mx.example.com\r\n\r\n" +
            $"Final-Recipient: rfc822; destino@example.com\r\nAction: {action}\r\nStatus: {status}\r\nDiagnostic-Code: {diagnosticCode}\r\n\r\n" +
            "--B1--\r\n";
        return MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw)));
    }

    private static MimeMessage LoadArf(string feedbackType, string originalMailFrom)
    {
        var raw =
            "From: <abuse@example.com>\r\nTo: <fbl@bouncedomain.test>\r\nSubject: complaint\r\n" +
            "Content-Type: multipart/report; report-type=feedback-report; boundary=\"B2\"\r\nMIME-Version: 1.0\r\n\r\n" +
            "--B2\r\nContent-Type: text/plain\r\n\r\nreport.\r\n\r\n" +
            "--B2\r\nContent-Type: message/feedback-report\r\n\r\n" +
            $"Feedback-Type: {feedbackType}\r\nOriginal-Mail-From: <{originalMailFrom}>\r\nOriginal-Rcpt-To: <destino@example.com>\r\n\r\n" +
            "--B2--\r\n";
        return MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(raw)));
    }

    [Fact]
    public async Task ProcessDsnAsync_FailedAction_MarksRecipientBounced_AndEmitsRemoteBounceWithCategory()
    {
        var (messageId, recipientId) = await SeedMessageAsync();

        var handled = await _service.ProcessDsnAsync(recipientId, LoadDsn("failed", "5.1.1", "smtp; 550 5.1.1 User unknown"));

        Assert.True(handled);
        var state = await _reader.ReadStateAsync(_paths.GetStateFilePath(messageId));
        Assert.Equal(RecipientStatus.Bounced, state!.Recipients[0].Status);

        var evt = Assert.Single(_accounting.Events);
        Assert.Equal(AccountingEventType.RemoteBounce, evt.Type);
        Assert.Equal(BounceCategory.BadMailbox, evt.Category);
        Assert.Equal(messageId, evt.MessageId);
        Assert.Equal(recipientId, evt.RecipientId);
    }

    [Fact]
    public async Task ProcessDsnAsync_FailedAction_StatusStartsWith4_MarksRecipientExpired_NotBounced()
    {
        var (messageId, recipientId) = await SeedMessageAsync();

        var handled = await _service.ProcessDsnAsync(recipientId, LoadDsn("failed", "4.4.7", "smtp; 451 4.4.7 delivery time expired"));

        Assert.True(handled);
        var state = await _reader.ReadStateAsync(_paths.GetStateFilePath(messageId));
        Assert.Equal(RecipientStatus.Expired, state!.Recipients[0].Status); // 4.x.x = remoto desistiu de tentar, não é um veredito permanente nosso
    }

    [Fact]
    public async Task ProcessDsnAsync_FailedAction_RuleForcesBounceEvenOnStatus4_MarksRecipientBounced()
    {
        var domainConfigWithRule = new StaticDomainConfigProvider(new DomainConfig
        {
            DomainName = "example.com",
            RetryIntervals = [TimeSpan.FromMinutes(1)],
            BounceAfter = TimeSpan.FromHours(1),
            ResponseRules = [new ResponseRule
            {
                Pattern = new System.Text.RegularExpressions.Regex("delivery time expired"),
                Actions = [ResponseRuleAction.ForceBounce]
            }]
        });
        var serviceWithRule = new BounceCorrelationService(_bounceTokenStore, _stateUpdater, _accounting, new BounceCategoryClassifier(),
            _paths, _reader, domainConfigWithRule, new ResponseRuleEngine());

        var (messageId, recipientId) = await SeedMessageAsync();

        await serviceWithRule.ProcessDsnAsync(recipientId, LoadDsn("failed", "4.4.7", "smtp; 451 4.4.7 delivery time expired"));

        var state = await _reader.ReadStateAsync(_paths.GetStateFilePath(messageId));
        Assert.Equal(RecipientStatus.Bounced, state!.Recipients[0].Status);
    }

    [Fact]
    public async Task ProcessDsnAsync_DelayedAction_DoesNotChangeStateOrEmitEvent()
    {
        var (messageId, recipientId) = await SeedMessageAsync(RecipientStatus.Transient);

        var handled = await _service.ProcessDsnAsync(recipientId, LoadDsn("delayed", "4.4.7", "smtp; 451 timeout"));

        Assert.True(handled); // tratado com sucesso, mas é só informativo
        var state = await _reader.ReadStateAsync(_paths.GetStateFilePath(messageId));
        Assert.Equal(RecipientStatus.Transient, state!.Recipients[0].Status);
        Assert.Empty(_accounting.Events);
    }

    [Fact]
    public async Task ProcessDsnAsync_UnknownToken_ReturnsFalse_WithoutThrowing()
    {
        var handled = await _service.ProcessDsnAsync(Guid.NewGuid(), LoadDsn("failed", "5.1.1", "smtp; 550 user unknown"));

        Assert.False(handled);
        Assert.Empty(_accounting.Events);
    }

    [Fact]
    public async Task ProcessArfAsync_KnownToken_EmitsRemoteFeedback_WithoutChangingDeliveryState()
    {
        var (messageId, recipientId) = await SeedMessageAsync(RecipientStatus.Delivered);
        var envelopeFrom = VerpToken.Format(recipientId, "bounce.strongmta.test");

        var handled = await _service.ProcessArfAsync(LoadArf("abuse", envelopeFrom));

        Assert.True(handled);
        var state = await _reader.ReadStateAsync(_paths.GetStateFilePath(messageId));
        Assert.Equal(RecipientStatus.Delivered, state!.Recipients[0].Status); // FBL não reverte a entrega já confirmada

        var evt = Assert.Single(_accounting.Events);
        Assert.Equal(AccountingEventType.RemoteFeedback, evt.Type);
        Assert.Equal(BounceCategory.SpamRelated, evt.Category);
        Assert.Equal(messageId, evt.MessageId);
        Assert.Equal(recipientId, evt.RecipientId);
    }

    [Fact]
    public async Task ProcessArfAsync_UnknownToken_ReturnsFalse()
    {
        var handled = await _service.ProcessArfAsync(LoadArf("abuse", $"bounce-{Guid.NewGuid():N}@bouncedomain.test"));

        Assert.False(handled);
        Assert.Empty(_accounting.Events);
    }

    [Fact]
    public async Task ProcessArfAsync_NonArfMessage_ReturnsFalse()
    {
        var plain = MimeMessage.Load(new MemoryStream("From: a@b.com\r\nTo: c@d.com\r\nSubject: x\r\n\r\ncorpo\r\n"u8.ToArray()));

        var handled = await _service.ProcessArfAsync(plain);

        Assert.False(handled);
    }
}
