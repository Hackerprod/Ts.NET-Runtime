using System.Collections.Concurrent;

namespace TypeSharp.VM.Interpreter;

public sealed class ExecutionProfile
{
    private readonly ConcurrentDictionary<string, long> _callCounts = new(StringComparer.Ordinal);

    public long RecordCall(string moduleName, string functionName) =>
        _callCounts.AddOrUpdate($"{moduleName}::{functionName}", 1, static (_, count) => count + 1);

    public long GetCallCount(string moduleName, string functionName) =>
        _callCounts.TryGetValue($"{moduleName}::{functionName}", out var count) ? count : 0;

    public IReadOnlyDictionary<string, long> Snapshot() =>
        new Dictionary<string, long>(_callCounts, StringComparer.Ordinal);
}
