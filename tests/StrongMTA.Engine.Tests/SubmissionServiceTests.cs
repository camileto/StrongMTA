using System.Security.Cryptography;
using System.Text;
using MimeKit;
using StrongMTA.Core;
using StrongMTA.Dkim;

namespace StrongMTA.Engine.Tests;

public class SubmissionServiceTests : IDisposable
{
    private readonly EngineTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private static Func<CancellationToken, Task<Stream>> Body(string text) =>
        _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task SubmitAsync_WritesMsgAndStateToSpool_AndEnqueuesEachRecipient()
    {
        var scheduler = new RecordingScheduler();
        var service = _fixture.CreateSubmissionService(scheduler);

        var messageId = await service.SubmitAsync(
            jobId: "campanha-1",
            recipients: [new SubmissionRecipient("a@example.com", "vmta-01"), new SubmissionRecipient("b@example.com", "vmta-01")],
            openBodyStream: Body("Subject: teste\r\n\r\ncorpo\r\n"));

        var msgPath = _fixture.Paths.GetMsgFilePath(messageId);
        var statePath = _fixture.Paths.GetStateFilePath(messageId);
        Assert.True(File.Exists(msgPath));
        Assert.True(File.Exists(statePath));

        var envelope = await _fixture.Reader.ReadEnvelopeAsync(msgPath);
        Assert.Equal(2, envelope.Recipients.Count);
        Assert.Equal("campanha-1", envelope.JobId);

        var items = scheduler.Items;

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(messageId, i.MessageId));
        Assert.Equal(["a@example.com", "b@example.com"], items.Select(i => i.RecipientAddress).Order());
    }

    [Fact]
    public async Task SubmitAsync_NoRecipients_Throws()
    {
        var scheduler = new RecordingScheduler();
        var service = _fixture.CreateSubmissionService(scheduler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SubmitAsync(null, [], Body("corpo")));
    }

    [Fact]
    public async Task SubmitAsync_InvalidRecipientAddress_Throws()
    {
        var scheduler = new RecordingScheduler();
        var service = _fixture.CreateSubmissionService(scheduler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SubmitAsync(null, [new SubmissionRecipient("sem-arroba", "vmta-01")], Body("corpo")));
    }

    [Fact]
    public async Task SubmitAsync_SenderDomainConfiguredForDkim_PersistsSignedBodyInSpool()
    {
        using var rsa = RSA.Create(2048);
        var keyPath = Path.Combine(_fixture.RootDirectory, "dkim.pem");
        Directory.CreateDirectory(_fixture.RootDirectory);
        await File.WriteAllTextAsync(keyPath, rsa.ExportRSAPrivateKeyPem());

        var dkimConfig = new DkimSigningConfig { Domain = "example.com", Selector = "default", PrivateKeyPath = keyPath };
        var dkimService = new DkimSigningService(new StaticDkimKeyProvider(new Dictionary<string, DkimSigningConfig> { ["example.com"] = dkimConfig }));

        var scheduler = new RecordingScheduler();
        var vmtaProvider = EngineTestFixture.CreateVirtualMtaProvider(EngineTestFixture.CreateVirtualMta());
        var service = _fixture.CreateSubmissionService(scheduler, vmtaProvider, dkimService);

        var rawBody = "From: remetente@example.com\r\nTo: dest@other.com\r\nSubject: assinado\r\nDate: Thu, 25 Jun 2026 12:00:00 +0000\r\nMessage-Id: <x@example.com>\r\n\r\nCorpo.\r\n";
        var messageId = await service.SubmitAsync(null,
            [new SubmissionRecipient("dest@other.com", "vmta-01")], Body(rawBody));

        await using var bodyStream = await _fixture.Reader.OpenBodyStreamAsync(_fixture.Paths.GetMsgFilePath(messageId));
        var persistedMessage = await MimeMessage.LoadAsync(bodyStream);

        Assert.Contains(persistedMessage.Headers, h => h.Id == HeaderId.DkimSignature);
    }

    [Fact]
    public async Task SubmitAsync_WithWarmup_PersistsTheActuallyChosenVirtualMtaName_InTheEnvelope()
    {
        var hotVmta = EngineTestFixture.CreateVirtualMta(name: "vmta-quente", coldVmtaName: "vmta-fria", coldDailyLimit: 1);
        var coldVmta = EngineTestFixture.CreateVirtualMta(name: "vmta-fria");
        var vmtaProvider = EngineTestFixture.CreateVirtualMtaProvider(hotVmta, coldVmta);

        var scheduler = new RecordingScheduler();
        var service = _fixture.CreateSubmissionService(scheduler, vmtaProvider);

        const string minimalRfc822 = "From: a@example.com\r\nSubject: t\r\n\r\ncorpo\r\n";
        var firstId = await service.SubmitAsync(null,
            [new SubmissionRecipient("a@example.com", "vmta-quente")], Body(minimalRfc822));
        var secondId = await service.SubmitAsync(null,
            [new SubmissionRecipient("b@example.com", "vmta-quente")], Body(minimalRfc822));

        var firstEnvelope = await _fixture.Reader.ReadEnvelopeAsync(_fixture.Paths.GetMsgFilePath(firstId));
        var secondEnvelope = await _fixture.Reader.ReadEnvelopeAsync(_fixture.Paths.GetMsgFilePath(secondId));

        Assert.Equal("vmta-fria", firstEnvelope.Recipients[0].VirtualMtaName);
        Assert.Equal("vmta-quente", secondEnvelope.Recipients[0].VirtualMtaName);

        var items = scheduler.Items;
        Assert.Equal(["vmta-fria", "vmta-quente"], items.Select(i => i.VirtualMtaName));
    }
}
