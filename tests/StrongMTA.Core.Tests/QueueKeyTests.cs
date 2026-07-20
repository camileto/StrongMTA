using StrongMTA.Core;

namespace StrongMTA.Core.Tests;

public class QueueKeyTests
{
    [Fact]
    public void Equals_IsCaseInsensitiveForDomain_ButCaseSensitiveForVmtaName()
    {
        var a = new QueueKey { DestinationDomain = "Example.com", VirtualMtaName = "vmta-01" };
        var b = new QueueKey { DestinationDomain = "example.com", VirtualMtaName = "vmta-01" };
        var c = new QueueKey { DestinationDomain = "example.com", VirtualMtaName = "VMTA-01" };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void DistinctDomainsOrVmtas_ProduceDifferentKeys()
    {
        var a = new QueueKey { DestinationDomain = "example.com", VirtualMtaName = "vmta-01" };
        var b = new QueueKey { DestinationDomain = "other.com", VirtualMtaName = "vmta-01" };
        var c = new QueueKey { DestinationDomain = "example.com", VirtualMtaName = "vmta-02" };

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void CanBeUsedAsDictionaryKey()
    {
        var dict = new Dictionary<QueueKey, int>();
        var key1 = new QueueKey { DestinationDomain = "example.com", VirtualMtaName = "vmta-01" };
        var key2 = new QueueKey { DestinationDomain = "EXAMPLE.COM", VirtualMtaName = "vmta-01" };

        dict[key1] = 42;

        Assert.True(dict.ContainsKey(key2));
        Assert.Equal(42, dict[key2]);
    }
}
