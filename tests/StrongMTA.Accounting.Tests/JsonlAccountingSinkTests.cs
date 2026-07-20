using System.Text.Json;
using StrongMTA.Core;

namespace StrongMTA.Accounting.Tests;

public class JsonlAccountingSinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "strongmta-accounting-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
        }
    }

    private static AccountingEvent CreateEvent(DateTimeOffset timestamp, AccountingEventType type = AccountingEventType.Delivered) => new()
    {
        Timestamp = timestamp,
        Type = type,
        MessageId = Guid.NewGuid(),
        RecipientId = Guid.NewGuid(),
        DestinationDomain = "example.com",
        SmtpCode = 250,
        SmtpResponseText = "OK"
    };

    [Fact]
    public async Task RecordAsync_WritesOneJsonLinePerEvent_ToFileNamedAfterUtcDate()
    {
        using var sink = new JsonlAccountingSink(_dir);
        var day = new DateTimeOffset(2026, 6, 25, 10, 0, 0, TimeSpan.Zero);

        await sink.RecordAsync(CreateEvent(day));
        await sink.RecordAsync(CreateEvent(day.AddHours(1)));

        var path = Path.Combine(_dir, "2026-06-25.jsonl");
        Assert.True(File.Exists(path));

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);

        var parsed = JsonSerializer.Deserialize<AccountingEvent>(lines[0]);
        Assert.Equal(AccountingEventType.Delivered, parsed!.Type);
        Assert.Equal(250, parsed.SmtpCode);
    }

    [Fact]
    public async Task RecordAsync_NeverRewritesPreviousLines_AppendOnly()
    {
        using var sink = new JsonlAccountingSink(_dir);
        var day = new DateTimeOffset(2026, 6, 25, 10, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
            await sink.RecordAsync(CreateEvent(day.AddMinutes(i)));

        var path = Path.Combine(_dir, "2026-06-25.jsonl");
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(5, lines.Length);
        Assert.All(lines, line => Assert.True(JsonSerializer.Deserialize<AccountingEvent>(line) is not null));
    }

    [Fact]
    public async Task RecordAsync_EventsOnDifferentUtcDates_GoToDifferentFiles()
    {
        using var sink = new JsonlAccountingSink(_dir);
        var day1 = new DateTimeOffset(2026, 6, 25, 23, 59, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 6, 26, 0, 1, 0, TimeSpan.Zero);

        await sink.RecordAsync(CreateEvent(day1));
        await sink.RecordAsync(CreateEvent(day2));

        Assert.True(File.Exists(Path.Combine(_dir, "2026-06-25.jsonl")));
        Assert.True(File.Exists(Path.Combine(_dir, "2026-06-26.jsonl")));
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(_dir, "2026-06-25.jsonl")));
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(_dir, "2026-06-26.jsonl")));
    }

    [Fact]
    public async Task RecordAsync_ConcurrentWrites_AllLinesPersistedWithoutCorruption()
    {
        using var sink = new JsonlAccountingSink(_dir);
        var day = DateTimeOffset.UtcNow;

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => sink.RecordAsync(CreateEvent(day.AddSeconds(i)))));

        var path = Path.Combine(_dir, $"{DateOnly.FromDateTime(day.UtcDateTime):yyyy-MM-dd}.jsonl");
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(50, lines.Length);
        Assert.All(lines, line => Assert.True(JsonSerializer.Deserialize<AccountingEvent>(line) is not null));
    }

    [Fact]
    public async Task RecordAsync_PersistsAcrossNewSinkInstances_SimulatingRestart()
    {
        var day = new DateTimeOffset(2026, 6, 25, 10, 0, 0, TimeSpan.Zero);

        using (var sink1 = new JsonlAccountingSink(_dir))
            await sink1.RecordAsync(CreateEvent(day));

        using var sink2 = new JsonlAccountingSink(_dir);
        await sink2.RecordAsync(CreateEvent(day.AddMinutes(1)));

        var lines = await File.ReadAllLinesAsync(Path.Combine(_dir, "2026-06-25.jsonl"));
        Assert.Equal(2, lines.Length);
    }
}
