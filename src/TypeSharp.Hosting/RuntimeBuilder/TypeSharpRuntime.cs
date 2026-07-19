using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using TypeSharp.Hosting.HotReload;
using TypeSharp.Hosting.Compilation;
using TypeSharp.Interop.HostExports;
using TypeSharp.Runtime.Generations;
using TypeSharp.Runtime.Modules;
using TypeSharp.VM.Interpreter;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;

namespace TypeSharp.Hosting;

public sealed class TypeSharpRuntimeBuilder
{
    private readonly List<string> _sourceDirectories = new();
    private readonly List<string> _sourceFiles = new();
    private readonly VMRuntimeLimits _limits = new();
    private readonly HostRegistry _hostRegistry = new();
    private readonly HotReloadOptions _hotReloadOptions = new();
    private bool _hotReloadEnabled;
    private readonly Dictionary<string, object> _hostServices = new();

    public TypeSharpRuntimeBuilder AddSourceDirectory(string path)
    {
        _sourceDirectories.Add(path);
        return this;
    }

    public TypeSharpRuntimeBuilder AddSourceFile(string path)
    {
        if (TypeScriptCompilation.IsExecutableTypeScriptFile(path))
            _sourceFiles.Add(path);
        return this;
    }

    public TypeSharpRuntimeBuilder EnableHotReload()
    {
        _hotReloadEnabled = true;
        return this;
    }

    public TypeSharpRuntimeBuilder ConfigureHotReload(Action<HotReloadOptions> configure)
    {
        configure(_hotReloadOptions);
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
        var desc = new HostFunctionDescriptor(module, name, typeof(object), null, func);
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
        foreach (var group in _hostRegistry.Functions.Values.GroupBy(f => f.FunctionName, StringComparer.Ordinal))
        {
            if (group.Count() == 1)
            {
                var desc = group.Single();
                interpreter.RegisterHostFunction(desc.FunctionName, (name, args) => desc.Implementation(args));
            }
        }

        var filesToLoad = new List<string>();

        foreach (var dir in _sourceDirectories)
        {
            if (Directory.Exists(dir))
            {
                var tsFiles = Directory.GetFiles(dir, "*.ts", SearchOption.AllDirectories)
                    .Where(TypeScriptCompilation.IsExecutableTypeScriptFile);
                filesToLoad.AddRange(tsFiles);
            }
        }

        filesToLoad.AddRange(_sourceFiles.Where(TypeScriptCompilation.IsExecutableTypeScriptFile));

        if (filesToLoad.Count > 0)
        {
            var sourceRoot = DetermineSourceRoot(filesToLoad, _sourceDirectories);
            var compilation = new TypeScriptCompilation(sourceRoot, _hostRegistry.CreateGlobalSymbols());
            foreach (var file in filesToLoad.Distinct(StringComparer.OrdinalIgnoreCase))
                compilation.AddSourceFile(file);

            var compiledModules = compilation.Compile();
            if (compilation.Diagnostics.HasErrors)
            {
                var diagnostics = string.Join(Environment.NewLine, compilation.Diagnostics.GetErrors());
                throw new InvalidOperationException($"Compilation failed:{Environment.NewLine}{diagnostics}");
            }

            int generationId = moduleManager.Registry.NextGenerationId();
            foreach (var compiled in compiledModules.Values)
            {
                var module = moduleManager.RegisterCompiledModule(compiled.ModuleId, compiled.SourcePath, compiled.Bytecode, generationId);
                var legacyAlias = Path.ChangeExtension(Path.GetRelativePath(Directory.GetCurrentDirectory(), compiled.SourcePath), null)
                    .Replace('\\', '/').TrimStart('/');
                moduleManager.Registry.RegisterAlias(legacyAlias, module, compiled.SourcePath);
            }
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
            _limits,
            _hotReloadOptions);

        if (_hotReloadEnabled)
        {
            foreach (var directory in _sourceDirectories)
                runtime.WatchDirectory(directory);
            foreach (var file in _sourceFiles)
                runtime.WatchDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        }

        runtime.InitializeGeneration();

        return runtime;
    }

    internal static string DetermineSourceRoot(IReadOnlyList<string> files, IReadOnlyList<string> sourceDirectories)
    {
        if (sourceDirectories.Count == 1)
            return Path.GetFullPath(sourceDirectories[0]);

        var directories = files.Select(file => Path.GetDirectoryName(Path.GetFullPath(file))!).ToList();
        string root = directories.Aggregate(CommonDirectory);
        if (files.Count == 1 && Path.GetFileNameWithoutExtension(files[0]) == "main")
            root = Directory.GetParent(root)?.FullName ?? root;
        return root;
    }

    internal static string CommonDirectory(string left, string right)
    {
        var leftParts = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
        var rightParts = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
        int count = 0;
        while (count < leftParts.Length && count < rightParts.Length &&
               string.Equals(leftParts[count], rightParts[count], StringComparison.OrdinalIgnoreCase))
            count++;
        return string.Join(Path.DirectorySeparatorChar, leftParts.Take(count));
    }
}

public sealed class TypeSharpRuntime : IAsyncDisposable
{
    private readonly Interpreter _interpreter;
    private readonly TsModuleManager _moduleManager;
    private readonly HostRegistry _hostRegistry;
    private readonly HotReloadManager? _hotReloadManager;
    private readonly VMRuntimeLimits _limits;
    private readonly HotReloadOptions _hotReloadOptions;
    private readonly List<FileSystemWatcher> _fileWatchers = new();
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly List<RuntimeGeneration> _retiredGenerations = new();
    private readonly object _generationLock = new();
    private readonly ConcurrentDictionary<string, byte> _pendingReloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer? _reloadDebounceTimer;
    private RuntimeGeneration? _activeGeneration;
    private int _nextGenerationId;
    private bool _disposed;

    public ModuleRegistry Modules => _moduleManager.Registry;
    public HotReloadManager? HotReload => _hotReloadManager;
    public RuntimeGeneration? ActiveGeneration => _activeGeneration;
    public IReadOnlyList<RuntimeGeneration> RetiredGenerations
    {
        get
        {
            lock (_generationLock)
                return _retiredGenerations.ToList();
        }
    }
    public int NextGenerationId => Interlocked.Increment(ref _nextGenerationId);

    internal TypeSharpRuntime(
        Interpreter interpreter,
        TsModuleManager moduleManager,
        HostRegistry hostRegistry,
        HotReloadManager? hotReloadManager,
        VMRuntimeLimits limits,
        HotReloadOptions? hotReloadOptions = null)
    {
        _interpreter = interpreter;
        _moduleManager = moduleManager;
        _hostRegistry = hostRegistry;
        _hotReloadManager = hotReloadManager;
        _limits = limits;
        _hotReloadOptions = hotReloadOptions ?? new HotReloadOptions();
        _nextGenerationId = 0;

        if (_hotReloadManager != null)
        {
            _reloadDebounceTimer = new Timer(_ => _ = ProcessPendingReloadsAsync(), null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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
        var gen = new RuntimeGeneration(genId, modules) { SwappedAt = DateTime.UtcNow };
        PublishGeneration(gen);

        if (_hotReloadManager != null)
        {
            foreach (var module in modules.Values)
            {
                if (File.Exists(module.FilePath))
                    _hotReloadManager.CommitSourceHash(module.FilePath, File.ReadAllText(module.FilePath));
            }
        }
    }

    public GenerationLease? AcquireGeneration()
    {
        var gen = Volatile.Read(ref _activeGeneration);
        if (gen == null)
            return null;

        return new GenerationLease(gen, PruneRetiredGenerationsAfterLease);
    }

    public TsValue? InvokeWithLease(string functionName, TsValue[]? args = null)
    {
        using var lease = AcquireGeneration();
        if (lease == null)
            throw new InvalidOperationException("No active generation");

        return ExecuteWithGeneration(lease.Generation, functionName, args);
    }

    public TypeSharpFunctionHandle CreateFunctionHandle(string functionName)
    {
        using var lease = AcquireGeneration()
            ?? throw new InvalidOperationException("No active generation");

        if (!lease.Generation.Modules.Values.Any(module => module.Bytecode.FunctionIndex.ContainsKey(functionName)))
            throw new InvalidOperationException($"Function '{functionName}' not found in generation {lease.Generation.Id}");

        return new TypeSharpFunctionHandle(this, lease.Generation, functionName);
    }

    internal TsValue? ExecuteWithGeneration(RuntimeGeneration generation, string functionName, TsValue[]? args)
    {
        foreach (var mod in generation.Modules.Values)
        {
            if (mod.Bytecode.FunctionIndex.ContainsKey(functionName))
            {
                var context = _interpreter.CreateContext();
                try
                {
                    return _interpreter.Execute(CreateLinkedModule(generation), functionName, args, context);
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
        var tsArgs = TypeSharp.Interop.Marshalling.Marshaller.FromManagedArray(args,
            args.Select(a => a?.GetType() ?? typeof(object)).ToArray());

        using var lease = AcquireGeneration()
            ?? throw new InvalidOperationException("No active generation");
        if (!lease.Generation.Modules.TryGetValue(moduleName, out var module))
        {
            module = await ImportAsync(moduleName);
        }

        var context = _interpreter.CreateContext();
        try
        {
            var result = _interpreter.Execute(CreateLinkedModule(lease.Generation), functionName, tsArgs, context);
            var managed = await TypeSharp.Interop.Marshalling.Marshaller
                .ToManagedAsync(result ?? TsValue.Null, typeof(T), context.CancellationToken)
                .ConfigureAwait(false);
            return (T)(managed ?? default(T)!);
        }
        finally
        {
            context.Dispose();
        }
    }

    public TsValue? Invoke(string functionName, TsValue[]? args = null)
    {
        return InvokeWithLease(functionName, args);
    }

    private static BytecodeModule CreateLinkedModule(RuntimeGeneration generation)
    {
        var functions = new List<BytecodeFunction>();
        foreach (var module in generation.Modules.Values)
        {
            var renameMap = BuildPrivateFunctionRenameMap(module);
            foreach (var function in module.Bytecode.Functions)
            {
                functions.Add(CloneLinkedFunction(function, renameMap));
            }
        }

        return new BytecodeModule($"generation-{generation.Id}", functions.ToArray());
    }

    private static Dictionary<string, string> BuildPrivateFunctionRenameMap(TsModule module)
    {
        var prefix = ToLinkSafeName(module.Name);
        return module.Bytecode.Functions
            .Where(function => function.Name.StartsWith("$lambda_", StringComparison.Ordinal))
            .ToDictionary(
                function => function.Name,
                function => $"$module_{prefix}_{function.Name}",
                StringComparer.Ordinal);
    }

    private static BytecodeFunction CloneLinkedFunction(
        BytecodeFunction function,
        IReadOnlyDictionary<string, string> renameMap)
    {
        var name = renameMap.TryGetValue(function.Name, out var renamed)
            ? renamed
            : function.Name;
        var stringConstants = function.StringConstants
            .Select(value => renameMap.TryGetValue(value, out var replacement) ? replacement : value)
            .ToArray();

        return new BytecodeFunction(
            name,
            function.Instructions,
            function.ParameterCount,
            function.LocalCount,
            function.IsAsync,
            stringConstants,
            function.IntegerConstants,
            function.DoubleConstants,
            function.DecimalConstants);
    }

    private static string ToLinkSafeName(string value)
    {
        var safe = new string(value.Select(ch =>
            char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "module" : safe;
    }

    public async Task<TsModule> ImportAsync(string moduleName)
    {
        var module = _moduleManager.Registry.GetModule(moduleName);
        if (module != null) return module;

        var files = _moduleManager.Registry.Modules.Values
            .Select(m => m.FilePath)
            .Concat(Directory.GetFiles(".", "*.ts", SearchOption.AllDirectories))
            .Where(TypeScriptCompilation.IsExecutableTypeScriptFile)
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
        if (_hotReloadManager == null)
            throw new InvalidOperationException("Hot reload not enabled");

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException(fullPath);
        if (_fileWatchers.Any(w => string.Equals(w.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
            return;

        var watcher = new FileSystemWatcher(fullPath, "*.ts")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };
        watcher.Changed += OnFileChanged;
        watcher.Created += OnFileChanged;
        watcher.Renamed += OnFileRenamed;
        _fileWatchers.Add(watcher);
    }

    public async Task<bool> ReloadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await _reloadLock.WaitAsync();
        try
        {
            if (_hotReloadManager == null || !TypeScriptCompilation.IsExecutableTypeScriptFile(filePath))
                return false;

            string source = await ReadSourceSafelyAsync(filePath, cancellationToken);

            if (!_hotReloadManager.HasChanges(filePath, source))
                return false;

            var previous = Volatile.Read(ref _activeGeneration);
            int genId = NextGenerationId;

            try
            {
                // Candidate is never registered or visible until all gates pass.
                var compiledReplacement = CompileWithRuntimeContext(filePath, genId, previous);
                var existing = previous?.Modules.Values.FirstOrDefault(module =>
                    string.Equals(Path.GetFullPath(module.FilePath), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
                var replacement = existing == null
                    ? compiledReplacement
                    : new TsModule(existing.Name, filePath, compiledReplacement.Bytecode, genId);
                var newModules = previous?.Modules.ToDictionary(pair => pair.Key, pair => pair.Value)
                    ?? new Dictionary<string, TsModule>();
                newModules[replacement.Name] = replacement;
                var candidate = new RuntimeGeneration(genId, newModules);

                if (!candidate.Validate())
                    throw new InvalidOperationException("Candidate generation validation failed");
                if (_hotReloadOptions.RunStartupCanary && !candidate.RunStartupTests(_interpreter))
                    throw new InvalidOperationException("Candidate startup canary failed");
                if (_hotReloadOptions.Canary != null && !_hotReloadOptions.Canary(candidate))
                    throw new InvalidOperationException("Candidate canary was rejected");

                if (previous != null)
                {
                    foreach (var migrator in _hotReloadOptions.Migrators)
                    {
                        if (!migrator.CanMigrate(previous, candidate))
                            throw new InvalidOperationException($"Migration rejected by {migrator.GetType().Name}");
                        migrator.Migrate(previous, candidate);
                    }
                }

                PublishGeneration(candidate);
                _hotReloadManager.CommitSourceHash(filePath, source);

                ModuleReloaded?.Invoke(this, new ModuleReloadedEventArgs(
                    replacement.Name, genId));

                Debug.WriteLine($"[HotReload] Reloaded {Path.GetFileName(filePath)} (gen {genId})");
                return true;
            }
            catch (Exception ex)
            {
                ReloadError?.Invoke(this, new ModuleReloadErrorEventArgs(
                    Path.GetFileNameWithoutExtension(filePath), ex.Message));

                Debug.WriteLine($"[HotReload] Failed to reload {Path.GetFileName(filePath)}: {ex.Message}");
                return false;
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    // Reload compiles must see the same world the original build saw: host
    // global symbols plus every sibling module, so imports and host calls
    // keep binding after a file changes.
    private TsModule CompileWithRuntimeContext(string filePath, int generationId, RuntimeGeneration? previous)
    {
        var fullPath = Path.GetFullPath(filePath);
        var files = new List<string> { fullPath };
        if (previous != null)
        {
            foreach (var module in previous.Modules.Values)
            {
                var modulePath = Path.GetFullPath(module.FilePath);
                if (!files.Contains(modulePath, StringComparer.OrdinalIgnoreCase))
                    files.Add(modulePath);
            }
        }

        var sourceRoot = TypeSharpRuntimeBuilder.DetermineSourceRoot(files, Array.Empty<string>());
        var compilation = new TypeScriptCompilation(sourceRoot, _hostRegistry.CreateGlobalSymbols());
        foreach (var file in files)
            compilation.AddSourceFile(file);

        var compiled = compilation.Compile();
        if (compilation.Diagnostics.HasErrors)
        {
            var diagnostics = string.Join(Environment.NewLine, compilation.Diagnostics.GetErrors());
            throw new InvalidOperationException($"Compilation failed:{Environment.NewLine}{diagnostics}");
        }

        var target = compiled.Values.FirstOrDefault(module =>
            string.Equals(Path.GetFullPath(module.SourcePath), fullPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Reload did not produce a module for '{filePath}'");

        return new TsModule(target.ModuleId, fullPath, target.Bytecode, generationId);
    }

    public Task<bool> RollbackAsync()
    {
        RuntimeGeneration? rollback;
        lock (_generationLock)
        {
            rollback = _retiredGenerations.LastOrDefault();
            if (rollback != null)
                _retiredGenerations.RemoveAt(_retiredGenerations.Count - 1);
        }

        if (rollback == null)
            return Task.FromResult(false);

        PublishGeneration(rollback);
        return Task.FromResult(true);
    }

    private void PublishGeneration(RuntimeGeneration candidate)
    {
        candidate.MarkCurrent();
        candidate.SwappedAt = DateTime.UtcNow;
        var previous = Interlocked.Exchange(ref _activeGeneration, candidate);
        if (previous == null || ReferenceEquals(previous, candidate))
            return;

        previous.MarkRetired();
        lock (_generationLock)
        {
            _retiredGenerations.Add(previous);
            PruneRetiredGenerations();
        }
    }

    private void PruneRetiredGenerations()
    {
        while (_retiredGenerations.Count > _hotReloadOptions.RetainedGenerations)
        {
            var oldest = _retiredGenerations[0];
            if (oldest.ActiveExecutions != 0)
                break;
            _retiredGenerations.RemoveAt(0);
        }
    }

    private void PruneRetiredGenerationsAfterLease()
    {
        lock (_generationLock)
            PruneRetiredGenerations();
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!TypeScriptCompilation.IsExecutableTypeScriptFile(e.FullPath))
            return;
        _pendingReloads.TryAdd(e.FullPath, 0);
        _reloadDebounceTimer?.Change(_hotReloadOptions.DebounceInterval, Timeout.InfiniteTimeSpan);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e) => OnFileChanged(sender, e);

    private async Task ProcessPendingReloadsAsync()
    {
        foreach (var filePath in _pendingReloads.Keys)
        {
            if (_pendingReloads.TryRemove(filePath, out _))
                await ReloadAsync(filePath);
        }
    }

    private static async Task<string> ReadSourceSafelyAsync(string filePath, CancellationToken cancellationToken)
    {
        const int attempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync(cancellationToken);
            }
            catch (IOException) when (attempt < attempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), cancellationToken);
            }
        }
    }

    public event EventHandler<ModuleReloadedEventArgs>? ModuleReloaded;
    public event EventHandler<ModuleReloadErrorEventArgs>? ReloadError;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var watcher in _fileWatchers)
            watcher.Dispose();
        _reloadDebounceTimer?.Dispose();
        _reloadLock.Dispose();

        await ValueTask.CompletedTask;
    }
}
