using System.CommandLine;
using StrongMTA.Core;
using StrongMTA.Spool;

namespace StrongMTA.Cli;

internal static class QueueCommands
{
    public static Command Create()
    {
        var queue = new Command("queue", "Inspeciona e administra a fila no spool.");
        queue.Add(CreateListCommand());
        queue.Add(CreatePauseCommand());
        queue.Add(CreateResumeCommand());
        return queue;
    }

    private static (SpoolPaths Paths, SpoolReader Reader, SpoolScanner Scanner, SpoolStateUpdater StateUpdater) CreateSpoolAccessors(string root)
    {
        var paths = new SpoolPaths(root);
        var reader = new SpoolReader();
        var writer = new SpoolWriter(paths);
        var scanner = new SpoolScanner(paths, reader);
        var stateUpdater = new SpoolStateUpdater(paths, reader, writer);
        return (paths, reader, scanner, stateUpdater);
    }

    private static Command CreateListCommand()
    {
        var jobId = new Option<string?>("--job-id") { Description = "Filtra por JobId." };
        var status = new Option<RecipientStatus?>("--status") { Description = "Filtra por status." };
        var domain = new Option<string?>("--domain") { Description = "Filtra por domínio destino." };

        var command = new Command("list", "Lista destinatários no spool, um por linha.");
        command.Add(SpoolOptions.Spool);
        command.Add(jobId);
        command.Add(status);
        command.Add(domain);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var (_, _, scanner, _) = CreateSpoolAccessors(parseResult.GetRequiredValue(SpoolOptions.Spool));
            var jobIdFilter = parseResult.GetValue(jobId);
            var statusFilter = parseResult.GetValue(status);
            var domainFilter = parseResult.GetValue(domain);

            var printed = 0;
            await foreach (var record in scanner.ScanAsync(cancellationToken))
            {
                if (jobIdFilter is not null && record.Envelope.JobId != jobIdFilter)
                    continue;

                var recipientsByid = record.Envelope.Recipients.ToDictionary(r => r.RecipientId);
                foreach (var recipientState in record.State.Recipients)
                {
                    if (statusFilter is not null && recipientState.Status != statusFilter)
                        continue;
                    if (!recipientsByid.TryGetValue(recipientState.RecipientId, out var envelope))
                        continue;
                    if (domainFilter is not null && !string.Equals(envelope.DestinationDomain, domainFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Console.WriteLine(
                        $"{record.Envelope.MessageId:N} {recipientState.RecipientId:N} {envelope.Address,-30} " +
                        $"domain={envelope.DestinationDomain,-20} vmta={envelope.VirtualMtaName,-12} status={recipientState.Status,-10} " +
                        $"attempts={recipientState.AttemptCount} next={recipientState.NextAttemptAt:O} job={record.Envelope.JobId ?? "-"}");
                    printed++;
                }
            }

            Console.WriteLine($"--- {printed} destinatário(s) ---");
        });

        return command;
    }

    private static Command CreatePauseCommand()
    {
        var jobId = new Option<string>("--job-id") { Description = "JobId cujos destinatários pendentes serão pausados.", Required = true };
        var command = new Command("pause", "Pausa todos os destinatários não-terminais de um JobId — não serão tentados até um resume.");
        command.Add(SpoolOptions.Spool);
        command.Add(jobId);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var count = await ChangeStatusByJobIdAsync(
                parseResult.GetRequiredValue(SpoolOptions.Spool), parseResult.GetRequiredValue(jobId),
                eligible: s => s is RecipientStatus.Pending or RecipientStatus.Transient,
                mutate: recipient => recipient.Status = RecipientStatus.Paused,
                cancellationToken);

            Console.WriteLine($"{count} destinatário(s) pausado(s).");
        });

        return command;
    }

    private static Command CreateResumeCommand()
    {
        var jobId = new Option<string>("--job-id") { Description = "JobId cujos destinatários pausados serão retomados.", Required = true };
        var command = new Command("resume", "Retoma destinatários pausados de um JobId, elegíveis para entrega imediata.");
        command.Add(SpoolOptions.Spool);
        command.Add(jobId);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var count = await ChangeStatusByJobIdAsync(
                parseResult.GetRequiredValue(SpoolOptions.Spool), parseResult.GetRequiredValue(jobId),
                eligible: s => s == RecipientStatus.Paused,
                mutate: recipient =>
                {
                    recipient.Status = RecipientStatus.Pending;
                    recipient.NextAttemptAt = DateTimeOffset.UtcNow;
                },
                cancellationToken);

            Console.WriteLine($"{count} destinatário(s) retomado(s).");
            Console.WriteLine("Nota: se um daemon estiver rodando, ele só vai pegar esses destinatários no próximo boot/rescan — resume não acorda um processo já em execução.");
        });

        return command;
    }

    private static async Task<int> ChangeStatusByJobIdAsync(
        string spoolRoot, string jobId, Func<RecipientStatus, bool> eligible, Action<RecipientStateData> mutate, CancellationToken cancellationToken)
    {
        var (_, _, scanner, stateUpdater) = CreateSpoolAccessors(spoolRoot);
        var count = 0;

        await foreach (var record in scanner.ScanAsync(cancellationToken))
        {
            if (record.Envelope.JobId != jobId)
                continue;

            foreach (var recipientState in record.State.Recipients)
            {
                if (!eligible(recipientState.Status))
                    continue;

                await stateUpdater.UpdateRecipientAsync(record.Envelope.MessageId, recipientState.RecipientId, mutate, cancellationToken).ConfigureAwait(false);
                count++;
            }
        }

        return count;
    }
}
