using System.Text;
using StrongMTA.Core;

namespace StrongMTA.Spool.Tests;

public class SpoolWriterReaderTests : IDisposable
{
    private readonly SpoolTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task WriteMessageAsync_ThenReadEnvelope_RoundTrips()
    {
        var envelope = SpoolTestFixture.CreateEnvelope();
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("Subject: x\r\n\r\nCorpo.\r\n"));

        var msgPath = await _fixture.Writer.WriteMessageAsync(envelope, body);
        var readBack = await _fixture.Reader.ReadEnvelopeAsync(msgPath);

        Assert.Equal(envelope.MessageId, readBack.MessageId);
        Assert.Equal(envelope.JobId, readBack.JobId);
        Assert.Equal(envelope.Recipients.Single().Address, readBack.Recipients.Single().Address);
        Assert.Equal(envelope.Recipients.Single().EnvelopeFrom, readBack.Recipients.Single().EnvelopeFrom);
    }

    [Fact]
    public async Task WriteMessageAsync_ThenOpenBodyStream_ReturnsExactBodyBytes()
    {
        var envelope = SpoolTestFixture.CreateEnvelope();
        const string bodyText = "Subject: corpo exato\r\n\r\nConteudo de teste com varias linhas.\r\nSegunda linha.\r\n";
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(bodyText));

        var msgPath = await _fixture.Writer.WriteMessageAsync(envelope, body);

        await using var bodyStream = await _fixture.Reader.OpenBodyStreamAsync(msgPath);
        using var streamReader = new StreamReader(bodyStream, Encoding.UTF8);
        var readBody = await streamReader.ReadToEndAsync();

        Assert.Equal(bodyText, readBody);
    }

    [Fact]
    public async Task WriteStateAsync_ThenReadState_RoundTrips()
    {
        var envelope = SpoolTestFixture.CreateEnvelope();
        var state = SpoolTestFixture.CreateDefaultState(envelope);
        state.Recipients[0].Status = RecipientStatus.Transient;
        state.Recipients[0].AttemptCount = 2;
        state.Recipients[0].LastSmtpResponse = "451 temp failure";

        await _fixture.Writer.WriteStateAsync(state);
        var statePath = _fixture.Paths.GetStateFilePath(envelope.MessageId);
        var readBack = await _fixture.Reader.ReadStateAsync(statePath);

        Assert.NotNull(readBack);
        Assert.Equal(RecipientStatus.Transient, readBack!.Recipients[0].Status);
        Assert.Equal(2, readBack.Recipients[0].AttemptCount);
        Assert.Equal("451 temp failure", readBack.Recipients[0].LastSmtpResponse);
    }

    [Fact]
    public async Task ReadStateAsync_NonExistentFile_ReturnsNull()
    {
        var result = await _fixture.Reader.ReadStateAsync(Path.Combine(_fixture.RootDirectory, "queue", "00", "00", "naoexiste.state"));

        Assert.Null(result);
    }

    [Fact]
    public async Task WriteMessageAsync_NeverLeavesTempFileBehind_OnSuccess()
    {
        var envelope = SpoolTestFixture.CreateEnvelope();
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("corpo"));

        var msgPath = await _fixture.Writer.WriteMessageAsync(envelope, body);
        var dir = Path.GetDirectoryName(msgPath)!;

        Assert.DoesNotContain(Directory.EnumerateFiles(dir), f => Path.GetFileName(f).StartsWith(".tmp-"));
    }

    [Fact]
    public void GetMessageDirectory_ShardsByFirstFourHexCharsOfGuid()
    {
        var messageId = Guid.Parse("ab120000-0000-0000-0000-000000000000");

        var dir = _fixture.Paths.GetMessageDirectory(messageId);

        var expected = Path.Combine(_fixture.RootDirectory, "queue", "ab", "12");
        Assert.Equal(expected, dir);
    }

    [Fact]
    public async Task WriteMessageAsync_OverwritingSameMessageId_ReplacesContentAtomically()
    {
        var messageId = Guid.NewGuid();
        var envelope1 = SpoolTestFixture.CreateEnvelope(messageId);
        using var body1 = new MemoryStream(Encoding.UTF8.GetBytes("versao 1"));
        await _fixture.Writer.WriteMessageAsync(envelope1, body1);

        var envelope2 = SpoolTestFixture.CreateEnvelope(messageId);
        using var body2 = new MemoryStream(Encoding.UTF8.GetBytes("versao 2 mais longa"));
        var msgPath = await _fixture.Writer.WriteMessageAsync(envelope2, body2);

        await using var bodyStream = await _fixture.Reader.OpenBodyStreamAsync(msgPath);
        using var reader = new StreamReader(bodyStream);
        Assert.Equal("versao 2 mais longa", await reader.ReadToEndAsync());
    }
}
