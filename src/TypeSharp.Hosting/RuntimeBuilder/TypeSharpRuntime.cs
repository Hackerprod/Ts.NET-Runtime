using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using TypeSharp.Hosting.HotReload;
using TypeSharp.Interop.HostExports;
using TypeSharp.Runtime.Generations;
using TypeSharp.Runtime.Modules;
using TypeSharp.VM.Interpreter;
using TypeSharp.VM.Memory;

namespace TypeSharp.Hosting;

public sealed class TypeSharpRuntimeBuilder
{
    private readonly List<string> _sourceDirectories = new();
    private readonly List<string> _sourceFiles = new();
    private readonly VMRuntimeLimits _limits = new();
    private readonly HostRegistry _hostRegistry = new();
    private bool _hotReloadEnabled;
    private readonly Dictionary<string, object> _hostServices = new();

    public TypeSharpRuntimeBuilder AddSourceDirectory(string path)
    {
        _sourceDirectories.Add(path);
        return this;
    }

    public TypeSharpRuntimeBuilder AddSourceFile(string path)
    {
        _sourceFiles.Add(path);
        return this;
    }

    public TypeSharpRuntimeBuilder EnableHotReload()
    {
        _hotReloadEnabled = true;
        return this;
    }

    public TypeSharpRuntimeBuilder ConfigureLimits(Action<VMRuntimeLimits> configure)
    {
        configure(_limits);
        return this;
    }

    public TypeSharpRuntimeBuilder AddHostService(string name, object service)
    {
        _hostServices[name] = service;
        _hostRegistry.RegisterObject(name, service);
        return this;
    }

    public TypeSharpRuntimeBuilder RegisterHostFunction(string module, string name, Func<TsValue[], TsValue?> func)
    {
        var desc = new HostFunctionDescriptor(module, name, typeof(void),
            Type.EmptyTypes, func);
        _hostRegistry.RegisterFunction(desc);
        return this;
    }

    public async Task<TypeSharpRuntime> BuildAsync()
    {
        var interpreter = new Interpreter(_limits);
        var moduleManager = new TsModuleManager(interpreter);

        foreach (var (key, desc) in _hostRegistry.Functions)
        {
            interpreter.RegisterHostFunction(key, (name, args) => desc.Implementation(args));
        }

        var filesToLoad = new List<string>();

        foreach (var dir in _sourceDirectories)
        {
            if (Directory.Exists(dir))
            {
                var tsFiles = Directory.GetFiles(dir, "*.ts", SearchOption.AllDirectories);
                filesToLoad.AddRange(tsFiles);
            }
        }

        filesToLoad.AddRange(_sourceFiles);

        foreach (var file in filesToLoad)
        {
            await moduleManager.LoadModuleAsync(file);
        }

        HotReloadManager? hotReloadManager = null;
        if (_hotReloadEnabled)
        {
            hotReloadManager = new HotReloadManager();
        }

        var runtime = new TypeSharpRuntime(
            interpreter,
            moduleManager,
            _hostRegistry,
            hotReloadManager,
            _limits);

        runtime.InitializeGeneration();

        return runtime;
    }
}

public sealed class TypeSharpRuntime : IAsyncDisposable
{
    private readonly Interpreter _interpreter;
    private readonly TsModuleManager _moduleManager;
    private readonly HostRegistry _hostRegistry;
    private readonly HotReloadManager? _hotReloadManager;
    private readonly VMRuntimeLimits _limits;
    private readonly FileSystemWatcher? _fileWatcher;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private RuntimeGeneration? _activeGeneration;
    private int _nextGenerationId;
    private bool _disposed;

    public ModuleRegistry Modules => _moduleManager.Registry;
    public HotReloadManager? HotReload => _hotReloadManager;
    public RuntimeGeneration? ActiveGeneration => _activeGeneration;
    public int NextGenerationId => Interlocked.Increment(ref _nextGenerationId);

    internal TypeSharpRuntime(
        Interpreter interpreter,
        TsModuleManager moduleManager,
        HostRegistry hostRegistry,
        HotReloadManager? hotReloadManager,
        VMRuntimeLimits limits)
    {
        _interpreter = interpreter;
        _moduleManager = moduleManager;
        _hostRegistry = hostRegistry;
        _hotReloadManager = hotReloadManager;
        _limits = limits;
        _nextGenerationId = 0;

        if (_hotReloadManager != null)
        {
            _fileWatcher = new FileSystemWatcher();
            _fileWatcher.Changed += OnFileChanged;
            _fileWatcher.EnableRaisingEvents = true;
        }
    }

    public void InitializeGeneration()
    {
        var modules = new Dictionary<string, TsModule>();
        foreach (var mod in _moduleManager.Registry.GetActiveModules())
        {
            modules[mod.Name] = mod;
        }

        int genId = NextGenerationId;
        var gen = new RuntimeGeneration(genId, modules)
        {
            IsCurrent = true,
            SwappedAt = DateTime.UtcNow
        };

        var previous = _activeGeneration;
        Interlocked.Exchange(ref _activeGeneration, gen);

        if (previous != null)
        {
            previous.IsCurrent = false;
        }
    }

    public GenerationLease? AcquireGeneration()
    {
        var gen = _activeGeneration;
        if (gen == null || !gen.IsCurrent)
            return null;

        return new GenerationLease(gen);
    }

    public TsValue? InvokeWithLease(string functionName, TsValue[]? args = null)
    {
        using var lease = AcquireGeneration();
        if (lease == null)
            throw new InvalidOperationException("No active generation");

        return ExecuteWithGeneration(lease.Generation, functionName, args);
    }

    private TsValue? ExecuteWithGeneration(RuntimeGeneration generation, string functionName, TsValue[]? args)
    {
        foreach (var mod in generation.Modules.Values)
        {
            if (mod.Bytecode.FunctionIndex.ContainsKey(functionName))
            {
                var context = _interpreter.CreateContext();
                try
                {
                    return _interpreter.Execute(mod.Bytecode, functionName, args, context);
                }
                finally
                {
                    context.Dispose();
                }
            }
        }

        throw new InvalidOperationException($"Function '{functionName}' not found in generation {generation.Id}");
    }

    public async Task<T> InvokeAsync<T>(string moduleName, string functionName, params object[] args)
    {
        var module = await ImportAsync(moduleName);
        var tsArgs = TypeSharp.Interop.Marshalling.Marshaller.FromManagedArray(args,
            args.Select(a => a?.GetType() ?? typeof(object)).ToArray());

        var result = _interpreter.Execute(module.Bytecode, functionName, tsArgs);

        return (T)(TypeSharp.Interop.Marshalling.Marshaller.ToManaged(result ?? TsValue.Null, typeof(T)) ?? default(T)!);
    }

    public TsValue? Invoke(string functionName, TsValue[]? args = null)
    {
        foreach (var module in _moduleManager.Registry.GetActiveModules())
        {
            if (module.Bytecode.FunctionIndex.ContainsKey(functionName))
            {
                return _interpreter.Execute(module.Bytecode, functionName, args);
            }
        }

        throw new InvalidOperationException($"Function '{functionName}' not found in any module");
    }

    public async Task<TsModule> ImportAsync(string moduleName)
    {
        var module = _moduleManager.Registry.GetModule(moduleName);
        if (module != null) return module;

        var files = _moduleManager.Registry.Modules.Values
            .Select(m => m.FilePath)
            .Concat(Directory.GetFiles(".", "*.ts", SearchOption.AllDirectories))
            .Distinct();

        foreach (var file in files)
        {
            if (Path.GetFileNameWithoutExtension(file) == moduleName ||
                file.Replace("\\", "/").EndsWith($"{moduleName}.ts"))
            {
                return await _moduleManager.LoadModuleAsync(file);
            }
        }

        throw new InvalidOperationException($"Module '{moduleName}' not found");
    }

    public void WatchDirectory(string path)
    {
        if (_fileWatcher == null)
            throw new InvalidOperationException("Hot reload not enabled");

        _fileWatcher.Path = Path.GetFullPath(path);
        _fileWatcher.Filter = "*.ts";
        _fileWatcher.EnableRaisingEvents = true;
    }

    private async void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_hotReloadManager == null) return;
        if (!e.FullPath.EndsWith(".ts")) return;

        await _reloadLock.WaitAsync();
        try
        {
            string source = await File.ReadAllTextAsync(e.FullPath);

            if (!_hotReloadManager.HasChanges(e.FullPath, source))
                return;

            var previous = _activeGeneration;
            int genId = NextGenerationId;

            try
            {
                await _moduleManager.LoadModuleAsync(e.FullPath);
                _hotReloadManager.CommitSourceHash(e.FullPath, source);

                var newModules = new Dictionary<string, TsModule>();
                foreach (var mod in _moduleManager.Registry.GetActiveModules())
                {
                    newModules[mod.Name] = mod;
                }

                var candidate = new RuntimeGeneration(genId, newModules);

                if (!candidate.Validate())
                    return;

                if (previous != null)
                    previous.IsCurrent = false;

                Interlocked.Exchange(ref _activeGeneration, candidate);
                candidate.IsCurrent = true;
                candidate.SwappedAt = DateTime.UtcNow;

                ModuleReloaded?.Invoke(this, new ModuleReloadedEventArgs(
                    Path.GetFileNameWithoutExtension(e.FullPath), genId));

                Debug.WriteLine($"[HotReload] Reloaded {Path.GetFileName(e.FullPath)} (gen {genId})");
            }
            catch (Exception ex)
            {
                if (previous != null)
                {
                    Interlocked.Exchange(ref _activeGeneration, previous);
                    previous.IsCurrent = true;
                }

                ReloadError?.Invoke(this, new ModuleReloadErrorEventArgs(
                    Path.GetFileNameWithoutExtension(e.FullPath), ex.Message));

                Debug.WriteLine($"[HotReload] Failed to reload {Path.GetFileName(e.FullPath)}: {ex.Message}");
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    public event EventHandler<ModuleReloadedEventArgs>? ModuleReloaded;
    public event EventHandler<ModuleReloadErrorEventArgs>? ReloadError;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _fileWatcher?.Dispose();
        _reloadLock.Dispose();

        await ValueTask.CompletedTask;
    }
}
