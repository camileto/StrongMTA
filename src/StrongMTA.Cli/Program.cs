using System.CommandLine;
using StrongMTA.Cli;

var root = new RootCommand("StrongMTA — administração de spool: submissão de teste, fila e accounting.");
root.Add(SubmitCommand.Create());
root.Add(QueueCommands.Create());
root.Add(AccountingCommands.Create());

var parseResult = root.Parse(args);
return await parseResult.InvokeAsync();
