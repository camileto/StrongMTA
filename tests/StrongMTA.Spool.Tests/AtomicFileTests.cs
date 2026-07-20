namespace StrongMTA.Spool.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "strongmta-atomicfile-tests-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task WriteAsync_Success_CreatesFinalFile_AndNoTempFileRemains()
    {
        var finalPath = Path.Combine(_dir, "msg.txt");

        await AtomicFile.WriteAsync(finalPath, async (stream, ct) =>
        {
            await stream.WriteAsync("conteudo"u8.ToArray(), ct);
        });

        Assert.True(File.Exists(finalPath));
        Assert.Equal("conteudo", await File.ReadAllTextAsync(finalPath));
        Assert.DoesNotContain(Directory.EnumerateFiles(_dir), f => Path.GetFileName(f).StartsWith(".tmp-"));
    }

    [Fact]
    public async Task WriteAsync_BodyThrows_FinalFileIsNeverCreated_AndTempFileIsCleanedUp()
    {
        var finalPath = Path.Combine(_dir, "msg.txt");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AtomicFile.WriteAsync(finalPath, (stream, ct) => throw new InvalidOperationException("falha simulada durante escrita")));

        Assert.False(File.Exists(finalPath));
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task WriteAsync_Twice_SecondWriteFullyReplacesFirst()
    {
        var finalPath = Path.Combine(_dir, "msg.txt");

        await AtomicFile.WriteAsync(finalPath, (s, ct) => s.WriteAsync("primeira versao bem mais longa"u8.ToArray(), ct).AsTask());
        await AtomicFile.WriteAsync(finalPath, (s, ct) => s.WriteAsync("v2"u8.ToArray(), ct).AsTask());

        Assert.Equal("v2", await File.ReadAllTextAsync(finalPath));
    }
}
