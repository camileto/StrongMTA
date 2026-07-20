using StrongMTA.Core;

namespace StrongMTA.Core.Tests;

public class SchedulerOptionsTests
{
    [Fact]
    public void CreateDefault_MultipliesProcessorCountByDefaultMultiplier()
    {
        var options = SchedulerOptions.CreateDefault(processorCount: 8);

        Assert.Equal(800, options.GlobalMaxConcurrency);
    }

    [Fact]
    public void CreateDefault_WithoutProcessorCountOverride_UsesEnvironmentProcessorCount()
    {
        var options = SchedulerOptions.CreateDefault();

        Assert.Equal(Environment.ProcessorCount * SchedulerOptions.DefaultCoreMultiplier, options.GlobalMaxConcurrency);
    }
}
