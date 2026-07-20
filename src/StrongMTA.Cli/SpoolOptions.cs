using System.CommandLine;

namespace StrongMTA.Cli;

/// <summary>Opção compartilhada por todos os comandos: diretório raiz do spool sobre o qual operam.</summary>
internal static class SpoolOptions
{
    public static readonly Option<string> Spool = new("--spool")
    {
        Description = "Diretório raiz do spool (mesmo usado pelo daemon).",
        Required = true
    };
}
