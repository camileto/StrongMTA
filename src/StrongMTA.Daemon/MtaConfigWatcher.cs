using StrongMTA.Core;

namespace StrongMTA.Daemon;

/// <summary>
/// Observa o arquivo <c>mta-config.json</c> com <see cref="FileSystemWatcher"/> e recarrega
/// os providers ao detectar mudança. Um debounce de 500ms agrupa múltiplos eventos (editores
/// costumam escrever o arquivo em várias etapas). Em caso de erro de parse a configuração
/// atual é mantida e o erro é logado — o daemon não é derrubado.
/// </summary>
public sealed class MtaConfigWatcher(
    string configPath,
    LiveDomainConfigProvider domainConfigProvider,
    LiveVirtualMtaProvider vmtaProvider,
    ILogger<MtaConfigWatcher> logger) : IDisposable
{
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource _debounceCts = new();

    public void Start()
    {
        var fullPath = Path.GetFullPath(configPath);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Renamed += OnFileChanged;

        logger.LogInformation("Hot-reload ativo: monitorando {Path}.", fullPath);
    }

    private void OnFileChanged(object _, FileSystemEventArgs __)
    {
        var oldCts = Interlocked.Exchange(ref _debounceCts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();

        var ct = _debounceCts.Token;
        _ = Task.Delay(TimeSpan.FromMilliseconds(500), ct)
            .ContinueWith(_ => ReloadConfig(), CancellationToken.None,
                          TaskContinuationOptions.OnlyOnRanToCompletion,
                          TaskScheduler.Default);
    }

    private void ReloadConfig()
    {
        try
        {
            var (defaultConfig, overrides, vmtas) = MtaConfigLoader.ParseConfig(configPath);
            domainConfigProvider.Reload(defaultConfig, overrides);
            vmtaProvider.Reload(vmtas);
            logger.LogInformation("mta-config.json recarregado com sucesso.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao recarregar mta-config.json — configuração atual mantida.");
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceCts.Dispose();
    }
}
