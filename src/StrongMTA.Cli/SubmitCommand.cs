using System.CommandLine;
using System.Net;
using StrongMTA.Accounting;
using StrongMTA.Core;
using StrongMTA.Dkim;
using StrongMTA.Engine;
using StrongMTA.Spool;

namespace StrongMTA.Cli;

/// <summary>Comando de submissão de teste: escreve uma mensagem no spool exatamente como o daemon faria, sem precisar de um processo rodando.</summary>
internal static class SubmitCommand
{
    public static Command Create()
    {
        var from = new Option<string>("--from") { Description = "Endereço de e-mail do remetente (From:).", Required = true };
        var to = new Option<string[]>("--to") { Description = "Endereço de e-mail do destinatário (repetível: --to a@x --to b@y).", Required = true };
        var subject = new Option<string>("--subject") { Description = "Assunto.", DefaultValueFactory = _ => "(sem assunto)" };
        var bodyFile = new Option<string?>("--body-file") { Description = "Caminho de um arquivo com o corpo da mensagem (texto puro). Se omitido, usa um corpo padrão de teste." };
        var jobId = new Option<string?>("--job-id") { Description = "Identificador de campanha/job, usado depois para pause/resume." };
        var vmtaName = new Option<string>("--vmta-name") { Description = "Nome do VirtualMta a usar.", DefaultValueFactory = _ => "vmta-01" };
        var sourceIp = new Option<string>("--source-ip") { Description = "IP de origem do VirtualMta.", DefaultValueFactory = _ => "0.0.0.0" };
        var helo = new Option<string>("--helo") { Description = "Hostname usado no HELO/EHLO.", DefaultValueFactory = _ => "localhost" };
        var bounceDomain = new Option<string>("--bounce-domain") { Description = "Domínio sob o qual os endereços VERP de bounce são gerados.", Required = true };
        var dkimDomain = new Option<string?>("--dkim-domain") { Description = "Domínio remetente a assinar com DKIM (opcional)." };
        var dkimSelector = new Option<string?>("--dkim-selector") { Description = "Selector DKIM (obrigatório se --dkim-domain for usado)." };
        var dkimKeyPath = new Option<string?>("--dkim-key-path") { Description = "Caminho da chave privada PEM (obrigatório se --dkim-domain for usado)." };

        var command = new Command("submit", "Submete uma mensagem de teste ao spool.")
        {
            SpoolOptions.Spool, from, to, subject, bodyFile, jobId, vmtaName, sourceIp, helo, bounceDomain, dkimDomain, dkimSelector, dkimKeyPath
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var paths = new SpoolPaths(parseResult.GetRequiredValue(SpoolOptions.Spool));
            var writer = new SpoolWriter(paths);

            // GlobalMaxConcurrency = 0: este comando só escreve no spool, exatamente como o
            // daemon faria, sem rodar nenhum worker neste processo — a entrega de fato é feita
            // por um daemon rodando separadamente. Um teto global zero garante que o item fica
            // só na lane (nunca tenta entregar dentro do próprio comando da CLI).
            var schedulerDomainConfigProvider = new StaticDomainConfigProvider(new DomainConfig
            {
                DomainName = "*",
                RetryIntervals = [TimeSpan.FromMinutes(30)],
                BounceAfter = TimeSpan.FromDays(2)
            });
            var scheduler = new FairShareDeliveryScheduler(
                schedulerDomainConfigProvider,
                new SchedulerOptions { GlobalMaxConcurrency = 0 },
                (_, _) => Task.CompletedTask);

            var vmta = new VirtualMta
            {
                Name = parseResult.GetRequiredValue(vmtaName),
                SourceIps = [IPAddress.Parse(parseResult.GetRequiredValue(sourceIp))],
                HostName = parseResult.GetRequiredValue(helo),
                DkimSelector = parseResult.GetValue(dkimSelector) ?? "default"
            };
            var vmtaProvider = new StaticVirtualMtaProvider(new Dictionary<string, VirtualMta> { [vmta.Name] = vmta });

            var dkimDomainValue = parseResult.GetValue(dkimDomain);
            IDkimSigningService dkimService = dkimDomainValue is null
                ? new DkimSigningService(new StaticDkimKeyProvider(new Dictionary<string, DkimSigningConfig>()))
                : new DkimSigningService(new StaticDkimKeyProvider(new Dictionary<string, DkimSigningConfig>
                {
                    [dkimDomainValue] = new DkimSigningConfig
                    {
                        Domain = dkimDomainValue,
                        Selector = parseResult.GetValue(dkimSelector) ?? throw new ArgumentException("--dkim-selector é obrigatório quando --dkim-domain é usado."),
                        PrivateKeyPath = parseResult.GetValue(dkimKeyPath) ?? throw new ArgumentException("--dkim-key-path é obrigatório quando --dkim-domain é usado.")
                    }
                }));

            var warmupRouter = new WarmupRouter(new WarmupCounterStore(paths));
            var bounceTokenStore = new BounceTokenStore(paths);
            using var accountingSink = new JsonlAccountingSink(paths.AccountingDirectory);

            var submission = new SubmissionService(paths, writer, scheduler, dkimService, vmtaProvider, warmupRouter, bounceTokenStore, accountingSink, parseResult.GetRequiredValue(bounceDomain));

            var bodyFileValue = parseResult.GetValue(bodyFile);
            var subjectValue = parseResult.GetRequiredValue(subject);
            var fromValue = parseResult.GetRequiredValue(from);
            var recipients = parseResult.GetRequiredValue(to)
                .Select(addr => new SubmissionRecipient(addr, vmta.Name))
                .ToList();

            Func<CancellationToken, Task<Stream>> openBody = async ct =>
            {
                string content;
                if (bodyFileValue is not null)
                    content = await File.ReadAllTextAsync(bodyFileValue, ct).ConfigureAwait(false);
                else
                    content = "Mensagem de teste enviada via StrongMTA CLI.\r\n";

                var rfc822 = $"From: {fromValue}\r\nSubject: {subjectValue}\r\nDate: {DateTime.UtcNow:R}\r\n\r\n{content}";
                return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rfc822));
            };

            var messageId = await submission.SubmitAsync(parseResult.GetValue(jobId), recipients, openBody, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Mensagem submetida: {messageId}");
            Console.WriteLine($"Destinatários: {recipients.Count}");
        });

        return command;
    }
}
