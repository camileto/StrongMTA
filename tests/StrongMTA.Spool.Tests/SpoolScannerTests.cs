using System.Text;
using StrongMTA.Core;

namespace StrongMTA.Spool.Tests;

public class SpoolScannerTests : IDisposable
{
    private readonly SpoolTestFixture _fixture = new();
    private readonly SpoolScanner _scanner;

    public SpoolScannerTests()
    {
        _scanner = new SpoolScanner(_fixture.Paths, _fixture.Reader);
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<MessageEnvelopeData> WriteMessageAsync(MessageStateData? state = null)
    {
        var envelope = SpoolTestFixture.CreateEnvelope();
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("Subject: x\r\n\r\ncorpo\r\n"));
        await _fixture.Writer.WriteMessageAsync(envelope, body);

        if (state is not null)
            await _fixture.Writer.WriteStateAsync(state);

        return envelope;
    }

    [Fact]
    public async Task ScanAsync_EmptySpool_ReturnsNoRecords()
    {
        var records = new List<SpoolMessageRecord>();
        await foreach (var record in _scanner.ScanAsync())
            records.Add(record);

        Assert.Empty(records);
    }

    [Fact]
    public async Task ScanAsync_FindsWrittenMessage_AndMatchesState()
    {
        var envelope = SpoolTestFixture.CreateEnvelope();
        var state = SpoolTestFixture.CreateDefaultState(envelope);
        state.Recipients[0].Status = RecipientStatus.Transient;
        state.Recipients[0].AttemptCount = 1;
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("corpo"));
        await _fixture.Writer.WriteMessageAsync(envelope, body);
        await _fixture.Writer.WriteStateAsync(state);

        var records = new List<SpoolMessageRecord>();
        await foreach (var record in _scanner.ScanAsync())
            records.Add(record);

        var found = Assert.Single(records);
        Assert.Equal(envelope.MessageId, found.Envelope.MessageId);
        Assert.Equal(RecipientStatus.Transient, found.State.Recipients[0].Status);
        Assert.Equal(1, found.State.Recipients[0].AttemptCount);
    }

    [Fact]
    public async Task ScanAsync_MissingStateFile_ReconstructsDefaultPendingState()
    {
        var envelope = await WriteMessageAsync(state: null);

        var records = new List<SpoolMessageRecord>();
        await foreach (var record in _scanner.ScanAsync())
            records.Add(record);

        var found = Assert.Single(records);
        Assert.Equal(RecipientStatus.Pending, found.State.Recipients[0].Status);
        Assert.Equal(0, found.State.Recipients[0].AttemptCount);
    }

    [Fact]
    public async Task ScanAsync_OrphanTempFile_IsDeletedAndNotReturned()
    {
        await WriteMessageAsync();

        // simula crash no meio de uma escrita atômica: arquivo .tmp- nunca renomeado
        var orphanDir = _fixture.Paths.GetMessageDirectory(Guid.NewGuid(), createIfMissing: true);
        var orphanTemp = Path.Combine(orphanDir, ".tmp-" + Guid.NewGuid().ToString("N") + ".msg");
        await File.WriteAllTextAsync(orphanTemp, "lixo de escrita interrompida");

        var records = new List<SpoolMessageRecord>();
        await foreach (var record in _scanner.ScanAsync())
            records.Add(record);

        Assert.Single(records); // só a mensagem válida, o órfão não aparece
        Assert.False(File.Exists(orphanTemp), "arquivo .tmp- órfão deveria ter sido removido pelo scanner");
    }

    [Fact]
    public async Task ScanAsync_CorruptMsgFile_IsSkippedWithoutThrowing()
    {
        await WriteMessageAsync(); // uma mensagem válida

        var corruptDir = _fixture.Paths.GetMessageDirectory(Guid.NewGuid(), createIfMissing: true);
        var corruptMsg = Path.Combine(corruptDir, Guid.NewGuid().ToString("N") + ".msg");
        await File.WriteAllBytesAsync(corruptMsg, [1, 2, 3, 4]); // não é um .msg válido (magic header errado)

        var records = new List<SpoolMessageRecord>();
        await foreach (var record in _scanner.ScanAsync())
            records.Add(record);

        Assert.Single(records); // o corrompido foi ignorado, não derrubou o scan
    }
}
