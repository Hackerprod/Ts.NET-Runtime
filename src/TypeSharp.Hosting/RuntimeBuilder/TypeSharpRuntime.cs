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

        // Register host functions in interpreter
        foreach (var (key, desc) in _hostRegistry.Functions)
        {
            interpreter.RegisterHostFunction(desc.FunctionName, (name, args) => desc.Implementation(args));
        }

        // Load source files
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

        // Compile and register all modules
        foreach (var file in filesToLoad)
        {
            await moduleManager.LoadModuleAsync(file);
        }

        HotReloadManager? hotReloadManager = null;
        if (_hotReloadEnabled)
        {
            hotReloadManager = new HotReloadManager();
        }

        return new TypeSharpRuntime(
            interpreter,
            moduleManager,
            _hostRegistry,
            hotReloadManager,
            _limits);
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
    private bool _disposed;

    public ModuleRegistry Modules => _moduleManager.Registry;
    public HotReloadManager? HotReload => _hotReloadManager;

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

        if (_hotReloadManager != null)
        {
            _fileWatcher = new FileSystemWatcher();
            _fileWatcher.Changed += OnFileChanged;
            _fileWatcher.EnableRaisingEvents = true;
        }
    }

    public async Task<TsModule> ImportAsync(string moduleName)
    {
        var module = _moduleManager.Registry.GetModule(moduleName);
        if (module != null) return module;

        // Try to find and load the file
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

            var gen = _hotReloadManager.BeginNewGeneration();

            try
            {
                await _moduleManager.LoadModuleAsync(e.FullPath);
                _hotReloadManager.CommitSourceHash(e.FullPath, source);

                // Deactivate old modules with same names
                var newModule = _moduleManager.Registry.Modules.Values
                    .Where(m => m.GenerationId == gen.GenerationId)
                    .ToList();

                foreach (var mod in newModule)
                {
                    var oldModules = _moduleManager.Registry.Modules.Values
                        .Where(m => m.GenerationId < gen.GenerationId &&
                                    m.Name == mod.Name && m.IsActive)
                        .ToList();

                    foreach (var old in oldModules)
                    {
                        old.IsActive = false;
                    }
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[HotReload] Reloaded {Path.GetFileName(e.FullPath)} (gen {gen.GenerationId})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HotReload] Failed to reload {Path.GetFileName(e.FullPath)}: {ex.Message}");
                gen.IsActive = false;
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _fileWatcher?.Dispose();
        _reloadLock.Dispose();

        await ValueTask.CompletedTask;
    }
}
