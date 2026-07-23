using StrongMTA.Accounting;
using StrongMTA.Bounce;
using StrongMTA.Core;
using StrongMTA.Daemon;
using StrongMTA.Dkim;
using StrongMTA.Engine;
using StrongMTA.Smtp.Client;
using StrongMTA.Smtp.Server;
using StrongMTA.Spool;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(_ =>
    new SpoolPaths(builder.Configuration["Spool:RootDirectory"]
        ?? throw new InvalidOperationException("Spool:RootDirectory não configurado.")));
builder.Services.AddSingleton<SpoolWriter>();
builder.Services.AddSingleton<SpoolReader>();
builder.Services.AddSingleton<SpoolStateUpdater>();
builder.Services.AddSingleton<SpoolScanner>();
builder.Services.AddSingleton<BackoffStateStore>();
builder.Services.AddSingleton<DisabledSourceStore>();
builder.Services.AddSingleton<BounceTokenStore>();
builder.Services.AddSingleton<WarmupCounterStore>();
builder.Services.AddSingleton<WarmupRouter>();
builder.Services.AddSingleton<PendingRetryIndex>();
builder.Services.AddSingleton<ResponseRuleEngine>();
builder.Services.AddSingleton<BounceCategoryClassifier>();
builder.Services.AddSingleton<BounceQueueService>();
builder.Services.AddSingleton<SpoolBootRecovery>();

builder.Services.AddSingleton<IAccountingSink>(sp =>
    new JsonlAccountingSink(sp.GetRequiredService<SpoolPaths>().AccountingDirectory));
builder.Services.AddSingleton<IMxResolver, DnsClientMxResolver>();

// DKIM sem domínios configurados nesta milestone (carregamento de chave por arquivo fica pra depois) —
// toda mensagem passa sem assinatura, mesmo caminho de pass-through já exercitado nos testes.
builder.Services.AddSingleton<IDkimSigningService>(_ =>
    new DkimSigningService(new StaticDkimKeyProvider(new Dictionary<string, DkimSigningConfig>())));

var mtaConfigPath = builder.Configuration["MtaConfigPath"] ?? "mta-config.json";
var (domainConfigProvider, vmtaProvider) = MtaConfigLoader.Load(mtaConfigPath);
builder.Services.AddSingleton(domainConfigProvider);
builder.Services.AddSingleton(vmtaProvider);

builder.Services.AddSingleton(_ =>
{
    var configured = builder.Configuration.GetValue<int?>("Scheduler:GlobalMaxConcurrency");
    return configured is { } value ? new SchedulerOptions { GlobalMaxConcurrency = value } : SchedulerOptions.CreateDefault();
});

var smtpPort = builder.Configuration.GetValue("SmtpPort", 25);
builder.Services.AddSingleton(sp => new SpoolDeliveryWorker(
    sp.GetRequiredService<SpoolReader>(),
    sp.GetRequiredService<SpoolStateUpdater>(),
    sp.GetRequiredService<IMxResolver>(),
    sp.GetRequiredService<IAccountingSink>(),
    sp.GetRequiredService<IDomainConfigProvider>(),
    sp.GetRequiredService<IVirtualMtaProvider>(),
    sp.GetRequiredService<PendingRetryIndex>(),
    sp.GetRequiredService<ResponseRuleEngine>(),
    sp.GetRequiredService<BackoffStateStore>(),
    sp.GetRequiredService<DisabledSourceStore>(),
    sp.GetRequiredService<BounceQueueService>(),
    smtpPort));

builder.Services.AddSingleton(sp => new FairShareDeliveryScheduler(
    sp.GetRequiredService<IDomainConfigProvider>(),
    sp.GetRequiredService<SchedulerOptions>(),
    sp.GetRequiredService<SpoolDeliveryWorker>().DeliverOneAsync));
builder.Services.AddSingleton<IDeliveryScheduler>(sp => sp.GetRequiredService<FairShareDeliveryScheduler>());

builder.Services.AddSingleton(_ =>
{
    var intervalStr = builder.Configuration["Spool:PurgeInterval"];
    var retainStr = builder.Configuration["Spool:RetainAfterTerminal"];
    var defaults = SpoolPurgeOptions.CreateDefault();
    return new SpoolPurgeOptions
    {
        PollInterval = intervalStr is not null ? TimeSpan.Parse(intervalStr) : defaults.PollInterval,
        RetainAfterTerminal = retainStr is not null ? TimeSpan.Parse(retainStr) : defaults.RetainAfterTerminal
    };
});
builder.Services.AddSingleton<SpoolPurgeService>();

builder.Services.AddSingleton(sp => new RetryScheduler(
    sp.GetRequiredService<PendingRetryIndex>(),
    sp.GetRequiredService<IDeliveryScheduler>()));

var bounceDomain = builder.Configuration["BounceDomain"]
    ?? throw new InvalidOperationException("BounceDomain não configurado.");

builder.Services.AddSingleton(sp => new SubmissionService(
    sp.GetRequiredService<SpoolPaths>(),
    sp.GetRequiredService<SpoolWriter>(),
    sp.GetRequiredService<IDeliveryScheduler>(),
    sp.GetRequiredService<IDkimSigningService>(),
    sp.GetRequiredService<IVirtualMtaProvider>(),
    sp.GetRequiredService<WarmupRouter>(),
    sp.GetRequiredService<BounceTokenStore>(),
    sp.GetRequiredService<IAccountingSink>(),
    bounceDomain));

builder.Services.AddSingleton(sp => new BounceCorrelationService(
    sp.GetRequiredService<BounceTokenStore>(),
    sp.GetRequiredService<SpoolStateUpdater>(),
    sp.GetRequiredService<IAccountingSink>(),
    sp.GetRequiredService<BounceCategoryClassifier>(),
    sp.GetRequiredService<SpoolPaths>(),
    sp.GetRequiredService<SpoolReader>(),
    sp.GetRequiredService<IDomainConfigProvider>(),
    sp.GetRequiredService<ResponseRuleEngine>()));

var bounceListenPort = builder.Configuration.GetValue("BounceListenPort", 25);
builder.Services.AddSingleton(sp => new BounceListener(
    new BounceListenerOptions
    {
        BounceDomain = bounceDomain,
        HostName = Environment.MachineName,
        ListenPort = bounceListenPort
    },
    sp.GetRequiredService<BounceCorrelationService>()));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
