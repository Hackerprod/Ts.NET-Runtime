using System.Collections.Concurrent;
using System.Collections.Immutable;
using TypeSharp.Runtime.Modules;
using TypeSharp.VM.Interpreter;
using TypeSharp.VM.Memory;

namespace TypeSharp.Runtime.Generations;

public sealed class RuntimeGeneration
{
    public int Id { get; }
    public IReadOnlyDictionary<string, TsModule> Modules { get; }
    public int ActiveExecutions { get; private set; }
    public bool IsCurrent { get; set; }
    public DateTime CreatedAt { get; }
    public DateTime? SwappedAt { get; set; }
    private readonly object _lock = new();

    public RuntimeGeneration(int id, IReadOnlyDictionary<string, TsModule> modules)
    {
        Id = id;
        Modules = modules;
        CreatedAt = DateTime.UtcNow;
        IsCurrent = false;
    }

    public void IncrementExecutions()
    {
        lock (_lock)
        {
            ActiveExecutions++;
        }
    }

    public void DecrementExecutions()
    {
        lock (_lock)
        {
            if (ActiveExecutions > 0)
                ActiveExecutions--;
        }
    }

    public bool Validate()
    {
        foreach (var module in Modules.Values)
        {
            if (module.Bytecode == null)
                return false;
            if (!module.Bytecode.FunctionIndex.ContainsKey("main") &&
                module.Name == "main")
                return false;
        }
        return true;
    }

    public bool RunStartupTests(Interpreter interpreter)
    {
        foreach (var module in Modules.Values)
        {
            if (module.Bytecode.FunctionIndex.TryGetValue("test", out int _))
            {
                try
                {
                    var result = interpreter.Execute(module.Bytecode, "test");
                    if (result is TsBoolValue boolVal && !boolVal.Value)
                        return false;
                }
                catch
                {
                    return false;
                }
            }
        }
        return true;
    }
}

public sealed class GenerationLease : IDisposable
{
    private readonly RuntimeGeneration _generation;
    private bool _disposed;

    public RuntimeGeneration Generation => _generation;

    public GenerationLease(RuntimeGeneration generation)
    {
        _generation = generation;
        _generation.IncrementExecutions();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation.DecrementExecutions();
    }
}
