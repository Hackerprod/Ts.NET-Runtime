using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Interpreter;
using TypeSharp.VM.Memory;

namespace TypeSharp.Runtime.Modules;

public sealed class TsModule
{
    public string Name { get; }
    public string FilePath { get; }
    public BytecodeModule Bytecode { get; }
    public TsValue[] Exports { get; }
    public int GenerationId { get; }
    public DateTime LoadedAt { get; }
    public bool IsActive { get; set; }

    public TsModule(string name, string filePath, BytecodeModule bytecode, int generationId)
    {
        Name = name;
        FilePath = filePath;
        Bytecode = bytecode;
        Exports = Array.Empty<TsValue>();
        GenerationId = generationId;
        LoadedAt = DateTime.UtcNow;
        IsActive = true;
    }
}

public sealed class ModuleRegistry
{
    private readonly Dictionary<string, TsModule> _modules = new();
    private readonly Dictionary<string, string> _modulePaths = new();
    private int _nextGenerationId;

    public IReadOnlyDictionary<string, TsModule> Modules => _modules;

    public void Register(string path, TsModule module)
    {
        _modules[module.Name] = module;
        _modulePaths[module.Name] = path;
    }

    public TsModule? GetModule(string name)
    {
        return _modules.TryGetValue(name, out var module) && module.IsActive ? module : null;
    }

    public string? GetModulePath(string name) =>
        _modulePaths.TryGetValue(name, out var path) ? path : null;

    public int NextGenerationId() => Interlocked.Increment(ref _nextGenerationId);

    public IReadOnlyList<TsModule> GetActiveModules() =>
        _modules.Values.Where(m => m.IsActive).ToList();

    public TsModule? DeactivateModule(string name)
    {
        if (_modules.TryGetValue(name, out var module))
        {
            module.IsActive = false;
            return module;
        }
        return null;
    }

    public void RemoveModule(string name)
    {
        _modules.Remove(name);
        _modulePaths.Remove(name);
    }
}

public sealed class TsModuleManager
{
    private readonly ModuleRegistry _registry;
    private readonly Interpreter _interpreter;
    private readonly Dictionary<string, TsModule> _generationMap = new();

    public ModuleRegistry Registry => _registry;

    public TsModuleManager(Interpreter interpreter)
    {
        _interpreter = interpreter;
        _registry = new ModuleRegistry();
    }

    public async Task<TsModule> LoadModuleAsync(string filePath)
    {
        string source = await File.ReadAllTextAsync(filePath);
        string modulePath = Path.GetFullPath(filePath);
        string moduleName = Path.ChangeExtension(modulePath, null)
            .Replace(Directory.GetCurrentDirectory(), "")
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');

        var lexer = new TypeSharp.Syntax.Lexer(source, filePath);
        var tokens = lexer.Tokenize();

        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntaxTree = parser.Parse(filePath);

        if (parser.Diagnostics.Any(d =>
            d.Severity == TypeSharp.Syntax.Diagnostics.DiagnosticSeverity.Error))
        {
            var formatted = string.Join("\n", parser.Diagnostics.Select(d => d.ToString()));
            throw new InvalidOperationException(
                $"Parse errors in {filePath}:\n{formatted}");
        }

        var binder = new TypeSharp.Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);

        if (binder.Diagnostics.HasErrors)
        {
            var formatted = string.Join("\n", binder.Diagnostics.GetErrors().Select(d => d.ToString()));
            throw new InvalidOperationException(
                $"Type errors in {filePath}:\n{formatted}");
        }

        var irGen = new TypeSharp.IR.IRGenerator();
        var moduleIR = irGen.Generate(boundTree);

        var pipeline = new TypeSharp.IR.Optimizations.IRPipeline();
        foreach (var func in moduleIR.Functions)
        {
            pipeline.Optimize(func);
        }

        var bytecodeModule = TypeSharp.VM.Bytecode.BytecodeCompiler.Compile(moduleIR);

        int genId = _registry.NextGenerationId();
        var module = new TsModule(moduleName, filePath, bytecodeModule, genId);

        _registry.Register(filePath, module);

        return module;
    }

    public TsValue? ExecuteModule(TsModule module, string entryPoint, TsValue[]? args = null)
    {
        return _interpreter.Execute(module.Bytecode, entryPoint, args);
    }

    public void ActivateGeneration(int generationId)
    {
        foreach (var module in _registry.Modules.Values)
        {
            if (module.GenerationId == generationId)
                module.IsActive = true;
        }
    }

    public void DeactivateGeneration(int generationId)
    {
        foreach (var module in _registry.Modules.Values)
        {
            if (module.GenerationId == generationId)
                module.IsActive = false;
        }
    }
}
