namespace TypeSharp.Hosting.HotReload;

public sealed class HotReloadService : IDisposable
{
    private readonly TypeSharpRuntime _runtime;
    private readonly Timer _debounceTimer;
    private readonly HashSet<string> _pendingChanges = new();
    private readonly SemaphoreSlim _debounceLock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public event EventHandler<ModuleReloadedEventArgs>? ModuleReloaded;
    public event EventHandler<ModuleReloadErrorEventArgs>? ReloadError;

    public HotReloadService(TypeSharpRuntime runtime, TimeSpan? debounceInterval = null)
    {
        _runtime = runtime;
        var interval = debounceInterval ?? TimeSpan.FromMilliseconds(300);
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        _runtime.ModuleReloaded += OnModuleReloaded;
        _runtime.ReloadError += OnReloadError;
    }

    public void StartWatching(string directory, TimeSpan? debounceInterval = null)
    {
        if (_watcher != null)
            return;

        _watcher = new FileSystemWatcher(directory, "*.ts")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = true
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        _watcher.EnableRaisingEvents = true;
    }

    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!e.FullPath.EndsWith(".ts")) return;
        QueueChange(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (!e.FullPath.EndsWith(".ts")) return;
        QueueChange(e.FullPath);
    }

    private async void QueueChange(string filePath)
    {
        await _debounceLock.WaitAsync();
        try
        {
            _pendingChanges.Add(filePath);
        }
        finally
        {
            _debounceLock.Release();
        }

        _debounceTimer.Change(TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);
    }

    private async void OnDebounceElapsed(object? state)
    {
        List<string> changes;
        await _debounceLock.WaitAsync();
        try
        {
            changes = _pendingChanges.ToList();
            _pendingChanges.Clear();
        }
        finally
        {
            _debounceLock.Release();
        }

        foreach (var filePath in changes)
        {
            try
            {
                _runtime.WatchDirectory(Path.GetDirectoryName(filePath) ?? ".");
                var watcher = new FileSystemWatcher();
                await Task.Delay(10);
            }
            catch (Exception ex)
            {
                ReloadError?.Invoke(this, new ModuleReloadErrorEventArgs(
                    Path.GetFileNameWithoutExtension(filePath), ex.Message));
            }
        }
    }

    private void OnModuleReloaded(object? sender, ModuleReloadedEventArgs e)
    {
        ModuleReloaded?.Invoke(this, e);
    }

    private void OnReloadError(object? sender, ModuleReloadErrorEventArgs e)
    {
        ReloadError?.Invoke(this, e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _debounceTimer.Dispose();
        _debounceLock.Dispose();
        StopWatching();

        _runtime.ModuleReloaded -= OnModuleReloaded;
        _runtime.ReloadError -= OnReloadError;
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
