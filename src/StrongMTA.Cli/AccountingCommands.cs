using System.CommandLine;
using StrongMTA.Spool;

namespace StrongMTA.Cli;

internal static class AccountingCommands
{
    public static Command Create()
    {
        var accounting = new Command("accounting", "Consulta os eventos de accounting (JSONL) gravados pelo daemon.");
        accounting.Add(CreateTailCommand());
        return accounting;
    }

    private static Command CreateTailCommand()
    {
        var date = new Option<string?>("--date") { Description = "Data (UTC, yyyy-MM-dd) do arquivo a ler. Padrão: hoje." };
        var follow = new Option<bool>("--follow") { Description = "Continua acompanhando o arquivo (como tail -f) até Ctrl+C." };

        var command = new Command("tail", "Imprime (e opcionalmente acompanha) o arquivo de accounting de um dia.");
        command.Add(SpoolOptions.Spool);
        command.Add(date);
        command.Add(follow);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var paths = new SpoolPaths(parseResult.GetRequiredValue(SpoolOptions.Spool));
            var dateValue = parseResult.GetValue(date) is { } d ? DateOnly.Parse(d) : DateOnly.FromDateTime(DateTime.UtcNow);
            var filePath = Path.Combine(paths.AccountingDirectory, $"{dateValue:yyyy-MM-dd}.jsonl");

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"(nenhum evento ainda em {filePath})");
                if (!parseResult.GetValue(follow))
                    return;
            }

            var lastPosition = 0L;
            void PrintNewLines()
            {
                if (!File.Exists(filePath)) return;
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(lastPosition, SeekOrigin.Begin);
                using var streamReader = new StreamReader(stream);
                string? line;
                while ((line = streamReader.ReadLine()) is not null)
                    Console.WriteLine(line);
                lastPosition = stream.Position;
            }

            PrintNewLines();

            if (!parseResult.GetValue(follow))
                return;

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                PrintNewLines();
            }
        });

        return command;
    }
}
