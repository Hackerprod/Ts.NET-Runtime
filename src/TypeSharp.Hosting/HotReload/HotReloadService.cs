namespace TypeSharp.Hosting.HotReload;

public sealed class HotReloadService
{
    private readonly TypeSharpRuntime _runtime;
    private readonly HashSet<string> _watchedPaths = new();
    private Timer? _pollTimer;

    public event EventHandler<ModuleReloadedEventArgs>? ModuleReloaded;
    public event EventHandler<ModuleReloadErrorEventArgs>? ReloadError;

    public HotReloadService(TypeSharpRuntime runtime)
    {
        _runtime = runtime;
    }

    public void StartWatching(string directory, TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);

        _runtime.WatchDirectory(directory);

        _pollTimer = new Timer(PollChanges, null, interval, interval);
    }

    public void StopWatching()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private void PollChanges(object? state)
    {
        // FileWatcher handles most cases, this is a fallback
    }

    public void Dispose()
    {
        StopWatching();
    }
}

public sealed class ModuleReloadedEventArgs : EventArgs
{
    public string ModuleName { get; }
    public int NewGenerationId { get; }

    public ModuleReloadedEventArgs(string moduleName, int newGenerationId)
    {
        ModuleName = moduleName;
        NewGenerationId = newGenerationId;
    }
}

public sealed class ModuleReloadErrorEventArgs : EventArgs
{
    public string ModuleName { get; }
    public string ErrorMessage { get; }

    public ModuleReloadErrorEventArgs(string moduleName, string errorMessage)
    {
        ModuleName = moduleName;
        ErrorMessage = errorMessage;
    }
}
