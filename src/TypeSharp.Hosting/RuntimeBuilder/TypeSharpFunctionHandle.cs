using TypeSharp.Runtime.Generations;
using TypeSharp.VM.Memory;

namespace TypeSharp.Hosting;

public sealed class TypeSharpFunctionHandle
{
    private readonly TypeSharpRuntime _runtime;
    private readonly RuntimeGeneration _generation;

    public string FunctionName { get; }
    public int GenerationId => _generation.Id;
    public bool IsCurrent => _generation.IsCurrent;

    internal TypeSharpFunctionHandle(TypeSharpRuntime runtime, RuntimeGeneration generation, string functionName)
    {
        _runtime = runtime;
        _generation = generation;
        FunctionName = functionName;
    }

    public TsValue? Invoke(params TsValue[] args)
    {
        using var lease = new GenerationLease(_generation);
        return _runtime.ExecuteWithGeneration(_generation, FunctionName, args);
    }
}
