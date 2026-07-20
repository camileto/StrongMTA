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
