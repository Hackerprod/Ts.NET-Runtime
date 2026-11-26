using System.Collections.Concurrent;

namespace TypeSharp.VM.Interpreter;

public sealed class ExecutionProfile
{
    private readonly ConcurrentDictionary<(string Module, string Function), long> _callCounts = new();

    public long RecordCall(string moduleName, string functionName) =>
        _callCounts.AddOrUpdate((moduleName ?? string.Empty, functionName ?? string.Empty), 1,
            static (_, count) => unchecked(count + 1));

    public long GetCallCount(string moduleName, string functionName) =>
        _callCounts.TryGetValue((moduleName ?? string.Empty, functionName ?? string.Empty), out var count) ? count : 0;

    public IReadOnlyDictionary<string, long> Snapshot()
    {
        // Preserve the original flattened display keys and their aggregate counts.
        var snapshot = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in _callCounts.ToArray())
        {
            var key = $"{entry.Key.Module}::{entry.Key.Function}";
            snapshot.TryGetValue(key, out var count);
            snapshot[key] = unchecked(count + entry.Value);
        }

        return snapshot;
    }
}
