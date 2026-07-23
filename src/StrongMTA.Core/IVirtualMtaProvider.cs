namespace StrongMTA.Core;

public interface IVirtualMtaProvider
{
    VirtualMta GetVirtualMta(string name);
}

/// <summary>Provider em memória com todos os VirtualMtas conhecidos, indexados por nome.</summary>
public sealed class StaticVirtualMtaProvider(IReadOnlyDictionary<string, VirtualMta> vmtas) : IVirtualMtaProvider
{
    public VirtualMta GetVirtualMta(string name) =>
        vmtas.TryGetValue(name, out var vmta)
            ? vmta
            : throw new KeyNotFoundException($"VirtualMta \"{name}\" não está configurado.");
}

/// <summary>Provider com swap atômico — permite hot-reload do mta-config.json sem reiniciar o daemon.</summary>
public sealed class LiveVirtualMtaProvider : IVirtualMtaProvider
{
    private volatile StaticVirtualMtaProvider _inner;

    public LiveVirtualMtaProvider(IReadOnlyDictionary<string, VirtualMta> vmtas)
        => _inner = new StaticVirtualMtaProvider(vmtas);

    public VirtualMta GetVirtualMta(string name) => _inner.GetVirtualMta(name);

    public void Reload(IReadOnlyDictionary<string, VirtualMta> vmtas)
        => _inner = new StaticVirtualMtaProvider(vmtas);
}
