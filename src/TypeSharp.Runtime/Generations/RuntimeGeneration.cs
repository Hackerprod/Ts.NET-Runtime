using System.Collections.Immutable;
using TypeSharp.Runtime.Modules;
using TypeSharp.VM.Interpreter;
using TypeSharp.VM.Memory;

namespace TypeSharp.Runtime.Generations;

public sealed class RuntimeGeneration
{
    public int Id { get; }
    public ImmutableDictionary<string, TsModule> Modules { get; }
    public int ActiveExecutions => Volatile.Read(ref _activeExecutions);
    public bool IsCurrent => Volatile.Read(ref _isCurrent) != 0;
    public DateTime CreatedAt { get; }
    public DateTime? SwappedAt { get; set; }
    private int _activeExecutions;
    private int _isCurrent;

    public RuntimeGeneration(int id, IReadOnlyDictionary<string, TsModule> modules)
    {
        Id = id;
        Modules = modules.ToImmutableDictionary(StringComparer.Ordinal);
        CreatedAt = DateTime.UtcNow;
    }

    public void IncrementExecutions()
    {
        Interlocked.Increment(ref _activeExecutions);
    }

    public void DecrementExecutions()
    {
        Interlocked.Decrement(ref _activeExecutions);
    }

    public void MarkCurrent() => Interlocked.Exchange(ref _isCurrent, 1);
    public void MarkRetired() => Interlocked.Exchange(ref _isCurrent, 0);

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
    private readonly Action? _onDisposed;
    private bool _disposed;

    public RuntimeGeneration Generation => _generation;

    public GenerationLease(RuntimeGeneration generation, Action? onDisposed = null)
    {
        _generation = generation;
        _onDisposed = onDisposed;
        _generation.IncrementExecutions();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation.DecrementExecutions();
        _onDisposed?.Invoke();
    }
}
