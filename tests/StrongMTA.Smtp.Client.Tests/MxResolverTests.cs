namespace StrongMTA.Smtp.Client.Tests;

public class MxResolverTests
{
    [Fact]
    public void SelectHosts_OrdersByPreference_LowestFirst()
    {
        var records = new[] { ("mx2.example.com", 20), ("mx1.example.com", 10), ("mx3.example.com", 30) };

        var hosts = DnsClientMxResolver.SelectHosts("example.com", records);

        Assert.Equal(["mx1.example.com", "mx2.example.com", "mx3.example.com"], hosts.Select(h => h.HostName));
    }

    [Fact]
    public void SelectHosts_NoMxRecords_FallsBackToDomainItself()
    {
        var hosts = DnsClientMxResolver.SelectHosts("example.com", []);

        Assert.Single(hosts);
        Assert.Equal("example.com", hosts[0].HostName);
        Assert.Equal(0, hosts[0].Preference);
    }
}
