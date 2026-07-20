using StrongMTA.Spool;

namespace StrongMTA.Engine.Tests;

public class WarmupRouterTests : IDisposable
{
    private readonly EngineTestFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ResolveVirtualMtaNameAsync_VmtaWithoutWarmup_AlwaysReturnsItself()
    {
        var vmta = EngineTestFixture.CreateVirtualMta(name: "vmta-01"); // sem ColdVmtaName/limite
        var router = new WarmupRouter(new WarmupCounterStore(_fixture.Paths));

        var resolved = await router.ResolveVirtualMtaNameAsync(vmta, "example.com");

        Assert.Equal("vmta-01", resolved);
    }

    [Fact]
    public async Task ResolveVirtualMtaNameAsync_FirstNMessages_DivertedToColdVmta_RestStayOnWarm()
    {
        var vmta = EngineTestFixture.CreateVirtualMta(name: "vmta-quente", coldVmtaName: "vmta-fria", coldDailyLimit: 2);
        var router = new WarmupRouter(new WarmupCounterStore(_fixture.Paths));

        var first = await router.ResolveVirtualMtaNameAsync(vmta, "example.com");
        var second = await router.ResolveVirtualMtaNameAsync(vmta, "example.com");
        var third = await router.ResolveVirtualMtaNameAsync(vmta, "example.com");
        var fourth = await router.ResolveVirtualMtaNameAsync(vmta, "example.com");

        Assert.Equal("vmta-fria", first);
        Assert.Equal("vmta-fria", second);
        Assert.Equal("vmta-quente", third);
        Assert.Equal("vmta-quente", fourth);
    }

    [Fact]
    public async Task ResolveVirtualMtaNameAsync_LimitIsPerDestinationDomain_Independently()
    {
        var vmta = EngineTestFixture.CreateVirtualMta(name: "vmta-quente", coldVmtaName: "vmta-fria", coldDailyLimit: 1);
        var router = new WarmupRouter(new WarmupCounterStore(_fixture.Paths));

        await router.ResolveVirtualMtaNameAsync(vmta, "a.com"); // consome a vaga só de a.com

        var forDomainB = await router.ResolveVirtualMtaNameAsync(vmta, "b.com");

        // o limite diário é por domínio destino, não global do VirtualMta
        Assert.Equal("vmta-fria", forDomainB);
    }
}
