namespace StrongMTA.Engine.Tests;

public class RetrySchedulerTests
{
    private static RecipientWorkItem CreateItem(DateTimeOffset nextAttemptAt) => new()
    {
        MessageId = Guid.NewGuid(),
        RecipientId = Guid.NewGuid(),
        MsgFilePath = "x.msg",
        StateFilePath = "x.state",
        EnvelopeFrom = "bounce@strongmta.test",
        RecipientAddress = "a@example.com",
        DestinationDomain = "example.com",
        VirtualMtaName = "vmta-01",
        SubmittedAt = DateTimeOffset.UtcNow,
        NextAttemptAt = nextAttemptAt
    };

    [Fact]
    public async Task RunOnceAsync_PromotesOnlyDueItems_ToTheQueue()
    {
        var index = new PendingRetryIndex();
        var due = CreateItem(DateTimeOffset.UtcNow.AddSeconds(-1));
        var notDue = CreateItem(DateTimeOffset.UtcNow.AddHours(1));
        index.Add(due);
        index.Add(notDue);

        var scheduler = new RecordingScheduler();
        var retryScheduler = new RetryScheduler(index, scheduler);

        var promoted = await retryScheduler.RunOnceAsync();

        Assert.Equal(1, promoted);
        Assert.Equal(1, index.Count); // o "notDue" continua pendente
        var item = Assert.Single(scheduler.Items);
        Assert.Equal(due.RecipientId, item.RecipientId);
    }

    [Fact]
    public async Task RunOnceAsync_NoItemsDue_PromotesNothing()
    {
        var index = new PendingRetryIndex();
        index.Add(CreateItem(DateTimeOffset.UtcNow.AddHours(1)));

        var scheduler = new RecordingScheduler();
        var retryScheduler = new RetryScheduler(index, scheduler);

        var promoted = await retryScheduler.RunOnceAsync();

        Assert.Equal(0, promoted);
        Assert.Equal(1, index.Count);
    }
}
