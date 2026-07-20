using StrongMTA.Core;

namespace StrongMTA.Core.Tests;

public class DomainConfigTests
{
    private static DomainConfig CreateConfig(params TimeSpan[] intervals) => new()
    {
        DomainName = "example.com",
        RetryIntervals = intervals,
        BounceAfter = TimeSpan.FromHours(48)
    };

    [Fact]
    public void GetRetryInterval_ReturnsIntervalForEachAttempt_InOrder()
    {
        var config = CreateConfig(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromMinutes(10), config.GetRetryInterval(1));
        Assert.Equal(TimeSpan.FromMinutes(30), config.GetRetryInterval(2));
        Assert.Equal(TimeSpan.FromHours(1), config.GetRetryInterval(3));
    }

    [Fact]
    public void GetRetryInterval_SaturatesOnLastInterval_WhenAttemptExceedsListLength()
    {
        var config = CreateConfig(TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromHours(1), config.GetRetryInterval(5));
        Assert.Equal(TimeSpan.FromHours(1), config.GetRetryInterval(100));
    }

    [Fact]
    public void GetRetryInterval_WithEmptyList_FallsBackToDefault()
    {
        var config = CreateConfig();

        Assert.Equal(TimeSpan.FromMinutes(30), config.GetRetryInterval(1));
    }

    [Fact]
    public void GetRetryInterval_HandlesAttemptNumberBelowOne_WithoutThrowing()
    {
        var config = CreateConfig(TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromMinutes(10), config.GetRetryInterval(0));
    }
}
