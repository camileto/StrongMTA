using System.Net;
using StrongMTA.Core;

namespace StrongMTA.Core.Tests;

public class LiveConfigProviderTests
{
    [Fact]
    public void LiveDomainConfigProvider_ReturnsInitialConfig()
    {
        var defaultConfig = MakeConfig("*", TimeSpan.FromMinutes(10));
        var provider = new LiveDomainConfigProvider(defaultConfig);

        Assert.Equal(TimeSpan.FromMinutes(10), provider.GetConfig("gmail.com").RetryIntervals[0]);
    }

    [Fact]
    public void LiveDomainConfigProvider_ReturnsNewConfig_AfterReload()
    {
        var provider = new LiveDomainConfigProvider(MakeConfig("*", TimeSpan.FromMinutes(10)));

        provider.Reload(MakeConfig("*", TimeSpan.FromMinutes(30)));

        Assert.Equal(TimeSpan.FromMinutes(30), provider.GetConfig("gmail.com").RetryIntervals[0]);
    }

    [Fact]
    public void LiveVirtualMtaProvider_ReturnsInitialVmta()
    {
        var vmta = MakeVmta("vmta-01", IPAddress.Loopback);
        var provider = new LiveVirtualMtaProvider(new Dictionary<string, VirtualMta> { [vmta.Name] = vmta });

        Assert.Equal(IPAddress.Loopback, provider.GetVirtualMta("vmta-01").SourceIps[0]);
    }

    [Fact]
    public void LiveVirtualMtaProvider_ReturnsNewVmta_AfterReload()
    {
        var vmta1 = MakeVmta("vmta-01", IPAddress.Loopback);
        var provider = new LiveVirtualMtaProvider(new Dictionary<string, VirtualMta> { [vmta1.Name] = vmta1 });

        var vmta2 = MakeVmta("vmta-01", IPAddress.Parse("127.0.0.2"));
        provider.Reload(new Dictionary<string, VirtualMta> { [vmta2.Name] = vmta2 });

        Assert.Equal(IPAddress.Parse("127.0.0.2"), provider.GetVirtualMta("vmta-01").SourceIps[0]);
    }

    private static DomainConfig MakeConfig(string domain, TimeSpan retryInterval) => new()
    {
        DomainName = domain,
        RetryIntervals = [retryInterval],
        BounceAfter = TimeSpan.FromHours(24)
    };

    private static VirtualMta MakeVmta(string name, IPAddress ip) => new()
    {
        Name = name,
        SourceIps = [ip],
        HostName = "mta.test",
        DkimSelector = "default"
    };
}
