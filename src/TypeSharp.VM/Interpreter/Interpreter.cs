using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;

namespace TypeSharp.VM.Interpreter;

public sealed class VMRuntimeLimits
{
    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public long MaximumInstructions { get; set; } = 10_000_000;
    public long MaximumMemoryBytes { get; set; } = 64 * 1024 * 1024;
    public int MaximumRecursionDepth { get; set; } = 256;
    public int HostStackSafetyFrameLimit { get; set; } = 1024;
}

public sealed class ExecutionContext : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    public long InstructionCount { get; set; }
    public Stack<CallFrame> CallStack { get; } = new();
    public CancellationToken CancellationToken => _cts.Token;
    public TsHeap Heap { get; }
    public VMRuntimeLimits Limits { get; }
    public IDictionary<string, TsValue> Globals { get; }
    public HashSet<string> InitializedModules { get; } = new(StringComparer.Ordinal);

    public ExecutionContext(VMRuntimeLimits limits, TsHeap heap, IDictionary<string, TsValue>? globals = null)
    {
        Limits = limits;
        Heap = heap;
        Globals = globals ?? new Dictionary<string, TsValue>(StringComparer.Ordinal);
        _cts = new CancellationTokenSource(limits.ExecutionTimeout);
    }

    public void IncrementInstruction()
    {
        InstructionCount++;
        if (InstructionCount > Limits.MaximumInstructions)
            throw new InvalidOperationException("Execution limit exceeded");
    }

    public void CheckRecursionDepth()
    {
        if (CallStack.Count > EffectiveMaximumCallFrames())
            throw new InvalidOperationException("Maximum recursion depth exceeded");
    }

    public int EffectiveMaximumCallFrames() =>
        Math.Max(1, Math.Min(Limits.MaximumRecursionDepth, Limits.HostStackSafetyFrameLimit));

    public void CheckMemory()
    {
        if (Heap.IsOverLimit())
        {
            var frame = CallStack.Count > 0 ? CallStack.Peek() : null;
            var frameDetail = frame == null
                ? "frame=<none>"
                : $"frame={frame.Function.Name}, ip={frame.InstructionPointer}, codeBytes={frame.Function.Instructions.Length}, strings={frame.Function.StringConstants.Length}";
            throw new InvalidOperationException(
                $"Memory limit exceeded: logicalBytes={Heap.LogicalBytes}, maxBytes={Heap.MaxBytes}, " +
                $"objects={Heap.ObjectsCreated}, arrays={Heap.ArraysCreated}, maps={Heap.MapsCreated}, " +
                $"instructions={InstructionCount}, {frameDetail}");
        }
    }

    public void CheckCancellation()
    {
        CancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Dispose();
    }
}

public sealed class ExecutionLease : IDisposable
{
    private readonly ExecutionContext _context;
    private bool _disposed;

    public ExecutionContext Context => _context;

    public ExecutionLease(ExecutionContext context)
    {
        _context = context;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.Dispose();
    }
}

public delegate TsValue? HostFunctionDelegate(string name, TsValue[] args);

internal sealed class TsGeneratorYieldException : Exception
{
    public TsValue Value { get; }

    public TsGeneratorYieldException(TsValue value)
    {
        Value = value;
    }
}

public sealed class Interpreter
{
    private readonly TsHeap _heap;
    private readonly VMRuntimeLimits _limits;
    private readonly Dictionary<string, HostFunctionDelegate> _hostFunctions = new();
    private readonly ExecutionProfile _profile = new();
    private readonly ConditionalWeakTable<BytecodeFunction, ConcurrentDictionary<int, int>> _callInlineCaches = new();

    public TsHeap Heap => _heap;
    public ExecutionProfile Profile => _profile;

    public static IReadOnlySet<string> HigherOrderIntrinsicNames => HigherOrderIntrinsics;
    internal IReadOnlyCollection<string> HostFunctionNames => _hostFunctions.Keys;

    public Interpreter(VMRuntimeLimits? limits = null)
    {
        _limits = limits ?? new VMRuntimeLimits();
        _heap = new TsHeap(_limits.MaximumMemoryBytes);
    }

    public void RegisterHostFunction(string name, HostFunctionDelegate func)
    {
        _hostFunctions[name] = func;
    }

    public ExecutionContext CreateContext()
    {
        // The logical allocation budget is a per-execution limit, not a
        // lifetime counter — reset the ledger, keep the shared heap so host
        // functions allocating through Interpreter.Heap still count.
        _heap.Reset();
        return new ExecutionContext(_limits, _heap);
    }

    public ExecutionContext CreateContext(IDictionary<string, TsValue> globals)
    {
        _heap.Reset();
        return new ExecutionContext(_limits, _heap, globals);
    }

    public TsValue? Execute(BytecodeModule module, string entryPoint, TsValue[]? args = null, ExecutionContext? context = null)
    {
        BytecodeVerifier.Verify(module);
        bool ownsContext = context == null;
        var ctx = context ?? CreateContext();

        try
        {
            if (!module.FunctionIndex.TryGetValue(entryPoint, out int funcIdx))
                throw new InvalidOperationException($"Entry point '{entryPoint}' not found");

            if (entryPoint.StartsWith("$module_init_", StringComparison.Ordinal))
                ctx.InitializedModules.Add(entryPoint);
            else
                EnsureModuleInitializers(module, ctx);

            var func = module.Functions[funcIdx];
            _profile.RecordCall(module.Name, func.Name);
            var frame = CreateCallFrame(func, null, args ?? Array.Empty<TsValue>(), ctx);

            if (func.IsGenerator)
                return new TsGeneratorValue(module, frame);

            try
            {
                return ExecuteManagedFrame(func, frame, module, ctx);
            }
            catch (TsThrownException thrown)
            {
                // Uncaught script throw crossing into the host surfaces as a
                // plain InvalidOperationException with the thrown message.
                throw new InvalidOperationException(thrown.Message);
            }
        }
        finally
        {
            if (ownsContext)
                ctx.Dispose();
        }
    }

    private TsValue ExecuteManagedFrame(BytecodeFunction function, CallFrame frame, BytecodeModule module, ExecutionContext ctx)
    {
        PushManagedFrame(ctx, frame);
        try
        {
            return FinishFunctionResult(function, ExecuteFrame(frame, module, ctx));
        }
        finally
        {
            PopManagedFrame(ctx, frame);
        }
    }

    private TsValue? ExecuteManagedFrameRaw(CallFrame frame, BytecodeModule module, ExecutionContext ctx)
    {
        PushManagedFrame(ctx, frame);
        try
        {
            return ExecuteFrame(frame, module, ctx);
        }
        finally
        {
            PopManagedFrame(ctx, frame);
        }
    }

    private static void PushManagedFrame(ExecutionContext ctx, CallFrame frame)
    {
        if (ctx.CallStack.Count >= ctx.EffectiveMaximumCallFrames())
            throw new InvalidOperationException(
                $"Maximum recursion depth exceeded ({ctx.EffectiveMaximumCallFrames()} frame limit)");
        ctx.CallStack.Push(frame);
    }

    private static void PopManagedFrame(ExecutionContext ctx, CallFrame expectedFrame)
    {
        if (ctx.CallStack.Count == 0)
            return;

        var popped = ctx.CallStack.Pop();
        if (!ReferenceEquals(popped, expectedFrame))
            throw new InvalidOperationException("VM call stack corruption detected");
    }

    private TsValue? ExecuteFrame(CallFrame frame, BytecodeModule module, ExecutionContext ctx)
    {
        while (true)
        {
            try
            {
                return ExecuteFrameCore(frame, module, ctx);
            }
            catch (TsThrownException thrown)
            {
                // Dispatch to the innermost active handler of THIS frame;
                // otherwise let the exception unwind to the calling frame.
                if (frame.TryHandlers is not { Count: > 0 })
                    throw;

                var handler = frame.TryHandlers.Pop();
                frame.StackPointer = handler.StackDepth;
                frame.Push(thrown.Value);
                frame.InstructionPointer = handler.HandlerOffset;
            }
        }
    }

    private void EnsureModuleInitializers(BytecodeModule module, ExecutionContext ctx)
    {
        foreach (var functionName in module.FunctionIndex.Keys
            .Where(name => name.StartsWith("$module_init_", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal))
        {
            if (!ctx.InitializedModules.Add(functionName))
                continue;
            Execute(module, functionName, null, ctx);
        }
    }

    private TsValue? ExecuteFrameCore(CallFrame frame, BytecodeModule module, ExecutionContext ctx)
    {
        var bytecode = frame.Function.Instructions;
        var strings = frame.Function.StringConstants;

        while (frame.InstructionPointer < bytecode.Length)
        {
            ctx.IncrementInstruction();
            ctx.CheckRecursionDepth();
            ctx.CheckMemory();
            ctx.CheckCancellation();

            int instructionOffset = frame.InstructionPointer;
            byte op = bytecode[frame.InstructionPointer++];

            switch (op)
            {
                // ── Load constants ──

                case Opcodes.Nop: // NOP
                    break;

                case Opcodes.LoadConstI32: // LOAD_CONST_I32
                    frame.Push(TsValue.FromInt32(ReadInt32(bytecode, ref frame.InstructionPointer)));
                    break;

                case Opcodes.LoadConstI64: // LOAD_CONST_I64
                    frame.Push(TsValue.FromInt64(ReadInt64(bytecode, ref frame.InstructionPointer)));
                    break;

                case Opcodes.LoadConstF32: // LOAD_CONST_F32
                    frame.Push(TsValue.FromFloat32(ReadFloat(bytecode, ref frame.InstructionPointer)));
                    break;

                case Opcodes.LoadConstF64: // LOAD_CONST_F64
                    frame.Push(TsValue.FromFloat64(ReadDouble(bytecode, ref frame.InstructionPointer)));
                    break;

                case Opcodes.LoadConstString: // LOAD_CONST_STRING
                {
                    int idx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    frame.Push(TsValue.FromString(strings[idx]));
                    break;
                }

                case Opcodes.LoadConstBool: // LOAD_CONST_BOOL
                    frame.Push(new TsBoolValue(bytecode[frame.InstructionPointer++] != 0));
                    break;

                case Opcodes.LoadConstNull: // LOAD_CONST_NULL
                    frame.Push(TsValue.Null);
                    break;

                case Opcodes.LoadConstVoid: // LOAD_CONST_VOID / undefined
                    frame.Push(TsValue.Void);
                    break;

                case Opcodes.LoadConstRegex: // LOAD_CONST_REGEX
                {
                    int idx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var encoded = strings[idx];
                    int separator = encoded.IndexOf('\0');
                    var pattern = separator >= 0 ? encoded[..separator] : encoded;
                    var flags = separator >= 0 ? encoded[(separator + 1)..] : "";
                    frame.Push(CreateRegex(pattern, flags));
                    break;
                }

                case Opcodes.LoadConstU64: // LOAD_CONST_U64
                    frame.Push(TsValue.FromUInt64(ReadUInt64(bytecode, ref frame.InstructionPointer)));
                    break;

                case Opcodes.LoadConstBigInt: // LOAD_CONST_BIGINT
                {
                    int idx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    frame.Push(TsValue.FromBigInt(BigInteger.Parse(strings[idx], CultureInfo.InvariantCulture)));
                    break;
                }

                case Opcodes.LoadConstDecimal: // LOAD_CONST_DECIMAL
                {
                    var decimals = frame.Function.DecimalConstants;
                    int idx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    frame.Push(TsValue.FromDecimal(decimals[idx]));
                    break;
                }

                // ── Variables ──

                case Opcodes.LoadLocal: // LOAD_LOCAL
                {
                    int idx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    frame.Push(frame.Locals[idx]);
                    break;
                }

                case Opcodes.StoreLocal: // STORE_LOCAL
                {
                    int idx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    frame.Locals[idx] = frame.Pop();
                    break;
                }

                case Opcodes.LoadArg: // LOAD_ARG
                {
                    int idx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    frame.Push(frame.Locals[idx]);
                    break;
                }

                case Opcodes.LoadThis: // LOAD_THIS
                    frame.Push(frame.Locals[0]);
                    break;

                case Opcodes.LoadInvocationReceiver:
                    frame.Push(frame.InvocationReceiver);
                    break;

                case Opcodes.LoadField: // LOAD_FIELD
                {
                    int fieldIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var fieldName = strings[fieldIdx];
                    frame.Push(ReadFieldValue(frame.Pop(), fieldName));
                    break;
                }

                case Opcodes.HasFieldLiteral:
                {
                    int fieldIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var fieldName = strings[fieldIdx];
                    var receiver = frame.Pop();
                    // Only TsObject gets own-key semantics; preserve the old read/compare
                    // result for other receivers.
                    bool hasField = receiver is TsObjectValue objVal
                        ? objVal.Value.Fields.ContainsKey(fieldName)
                        : receiver is TsSetValue
                            ? NativeSet.HasProperty(fieldName)
                            : receiver is TsRegexValue
                                ? NativeRegex.HasProperty(fieldName)
                            : !StrictEquals(ReadFieldValue(receiver, fieldName), TsValue.Void);
                    frame.Push(new TsBoolValue(hasField));
                    break;
                }

                case Opcodes.StoreField: // STORE_FIELD
                {
                    int fieldIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var fieldName = strings[fieldIdx];
                    var value = frame.Pop();
                    var obj = frame.Pop();
                    if (obj is TsObjectValue objVal)
                        objVal.Value.SetField(fieldName, value);
                    // AI: la escritura por punto sobre `null` lanza, como el
                    // camino de índice (`StoreElement`); los demás receptores
                    // no-objeto conservan el no-op silencioso de hoy.
                    else if (obj is TsMapValue && fieldName == "size")
                        throw NativeMap.TypeError(NativeMap.ReadonlySizeMessage);
                    else if (obj is TsSetValue && fieldName == "size")
                        throw NativeSet.TypeError(NativeSet.ReadonlySizeMessage);
                    else if (obj is TsRegexValue regex && fieldName == "lastIndex")
                        regex.ScriptLastIndex = value;
                    else if (obj is TsNull)
                        throw new InvalidOperationException("Cannot index a null value");
                    break;
                }

                case Opcodes.CopyObjectFields:
                {
                    var source = frame.Pop();
                    var target = frame.Pop();
                    if (target is TsObjectValue targetObject && source is TsObjectValue sourceObject)
                    {
                        foreach (var field in sourceObject.Value.Fields)
                            if (!IsPrivateRuntimeField(field.Key))
                                targetObject.Value.SetField(field.Key, field.Value);
                    }
                    else if (target is TsObjectValue arrayTarget && source is TsArrayValue sourceArray)
                    {
                        var array = sourceArray.Value;
                        for (int i = 0; i < array.Count; i++)
                            if (!array.IsHole(i))
                                arrayTarget.Value.SetField(i.ToString(CultureInfo.InvariantCulture), array.Get(i));
                    }
                    break;
                }

                case Opcodes.EnumerateKeys:
                {
                    var source = frame.Pop();
                    var keys = ctx.Heap.AllocateArray();
                    switch (source)
                    {
                        case TsObjectValue ordinaryObject when ordinaryObject.Value.TypeName == "Object":
                        {
                            var indexes = new List<(uint Index, string Key)>();
                            var names = new List<string>();
                            foreach (var key in ordinaryObject.Value.Fields.Keys)
                            {
                                if (IsPrivateRuntimeField(key))
                                    continue;
                                if (uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                                    && index != uint.MaxValue
                                    && string.Equals(key, index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                                    indexes.Add((index, key));
                                else
                                    names.Add(key);
                            }

                            indexes.Sort(static (left, right) => left.Index.CompareTo(right.Index));
                            foreach (var entry in indexes)
                                keys.Add(TsValue.FromString(entry.Key));
                            foreach (var name in names)
                                keys.Add(TsValue.FromString(name));
                            break;
                        }
                        case TsObjectValue objectValue:
                            foreach (var key in objectValue.Value.Fields.Keys)
                                if (!IsPrivateRuntimeField(key))
                                    keys.Add(TsValue.FromString(key));
                            break;
                        case TsArrayValue arrayValue:
                            for (int i = 0; i < arrayValue.Value.Count; i++)
                                if (!arrayValue.Value.IsHole(i))
                                    keys.Add(TsValue.FromString(i.ToString(CultureInfo.InvariantCulture)));
                            break;
                        case TsStringValue stringValue:
                            for (int i = 0; i < stringValue.Value.Length; i++)
                                keys.Add(TsValue.FromString(i.ToString(CultureInfo.InvariantCulture)));
                            break;
                        case TsNull or TsVoid:
                            throw new InvalidOperationException("Cannot enumerate keys of null or undefined");
                    }
                    frame.Push(new TsArrayValue(keys));
                    break;
                }

                case Opcodes.ArraySliceFrom:
                {
                    int start = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var source = frame.Pop();
                    if (source is not TsArrayValue arrayValue)
                        throw new InvalidOperationException("Array rest binding requires an array value");
                    // Allocate only the observable suffix; do not retain a backing
                    // array or expose host collections through the rest binding.
                    int first = Math.Clamp(start, 0, arrayValue.Value.Count);
                    var suffix = ctx.Heap.AllocateArray(arrayValue.Value.Count - first);
                    for (int i = first; i < arrayValue.Value.Count; i++)
                        suffix.Add(arrayValue.Value.Get(i));
                    frame.Push(new TsArrayValue(suffix));
                    break;
                }

                case Opcodes.ObjectRest:
                {
                    int excludedCount = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var excluded = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < excludedCount; i++)
                        excluded.Add(ToPropertyKey(frame.Pop()));
                    var source = frame.Pop();
                    if (source is not TsObjectValue objectValue)
                        throw new InvalidOperationException("Object rest binding requires an object value");
                    var copy = ctx.Heap.AllocateObject("Object");
                    foreach (var field in objectValue.Value.Fields)
                        if (!excluded.Contains(field.Key) && !IsPrivateRuntimeField(field.Key))
                            copy.SetField(field.Key, field.Value);
                    frame.Push(new TsObjectValue(copy));
                    break;
                }

                case Opcodes.LoadGlobal: // LOAD_GLOBAL
                {
                    int globalIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var globalName = strings[globalIdx];
                    frame.Push(ctx.Globals.TryGetValue(globalName, out var value) ? value : TsValue.Null);
                    break;
                }

                case Opcodes.StoreGlobal: // STORE_GLOBAL
                {
                    int globalIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var globalName = strings[globalIdx];
                    ctx.Globals[globalName] = frame.Pop();
                    break;
                }

                // ── I32 arithmetic ──

                case Opcodes.AddI32: // ADD_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt32(AsInt32(left) + AsInt32(right)));
                    break;
                }
                case Opcodes.SubI32: // SUB_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt32(AsInt32(left) - AsInt32(right)));
                    break;
                }
                case Opcodes.MulI32: // MUL_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt32(AsInt32(left) * AsInt32(right)));
                    break;
                }
                case Opcodes.DivI32: // DIV_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    int r = AsInt32(right);
                    if (r == 0) throw new InvalidOperationException("Division by zero");
                    frame.Push(TsValue.FromInt32(AsInt32(left) / r));
                    break;
                }
                case Opcodes.ModI32: // MOD_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    int r = AsInt32(right);
                    if (r == 0) throw new InvalidOperationException("Division by zero");
                    frame.Push(TsValue.FromInt32(AsInt32(left) % r));
                    break;
                }
                case Opcodes.NegI32: // NEG_I32
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromInt32(-AsInt32(val)));
                    break;
                }

                // ── I64 arithmetic ──

                case Opcodes.AddI64: // ADD_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt64(AsInt64(left) + AsInt64(right)));
                    break;
                }
                case Opcodes.SubI64: // SUB_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt64(AsInt64(left) - AsInt64(right)));
                    break;
                }
                case Opcodes.MulI64: // MUL_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt64(AsInt64(left) * AsInt64(right)));
                    break;
                }
                case Opcodes.DivI64: // DIV_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    long r = AsInt64(right);
                    if (r == 0) throw new InvalidOperationException("Division by zero");
                    frame.Push(TsValue.FromInt64(AsInt64(left) / r));
                    break;
                }
                case Opcodes.ModI64: // MOD_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    long r = AsInt64(right);
                    if (r == 0) throw new InvalidOperationException("Division by zero");
                    frame.Push(TsValue.FromInt64(AsInt64(left) % r));
                    break;
                }
                case Opcodes.NegI64: // NEG_I64
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromInt64(-AsInt64(val)));
                    break;
                }

                // ── U64 arithmetic ──

                case Opcodes.AddU64: // ADD_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    if (UsesBigIntSemantics(left, right))
                    {
                        frame.Push(TsValue.FromBigInt(AsBigInteger(left) + AsBigInteger(right)));
                        break;
                    }
                    frame.Push(TsValue.FromUInt64(AsUInt64(left) + AsUInt64(right)));
                    break;
                }
                case Opcodes.SubU64: // SUB_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    if (UsesBigIntSemantics(left, right))
                    {
                        frame.Push(TsValue.FromBigInt(AsBigInteger(left) - AsBigInteger(right)));
                        break;
                    }
                    frame.Push(TsValue.FromUInt64(AsUInt64(left) - AsUInt64(right)));
                    break;
                }
                case Opcodes.MulU64: // MUL_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    if (UsesBigIntSemantics(left, right))
                    {
                        frame.Push(TsValue.FromBigInt(AsBigInteger(left) * AsBigInteger(right)));
                        break;
                    }
                    frame.Push(TsValue.FromUInt64(AsUInt64(left) * AsUInt64(right)));
                    break;
                }
                case Opcodes.DivU64: // DIV_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    if (UsesBigIntSemantics(left, right))
                    {
                        var divisor = AsBigInteger(right);
                        if (divisor == BigInteger.Zero) throw new InvalidOperationException("Division by zero");
                        frame.Push(TsValue.FromBigInt(AsBigInteger(left) / divisor));
                        break;
                    }
                    ulong r = AsUInt64(right);
                    if (r == 0) throw new InvalidOperationException("Division by zero");
                    frame.Push(TsValue.FromUInt64(AsUInt64(left) / r));
                    break;
                }
                case Opcodes.ModU64: // MOD_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    if (UsesBigIntSemantics(left, right))
                    {
                        var divisor = AsBigInteger(right);
                        if (divisor == BigInteger.Zero) throw new InvalidOperationException("Division by zero");
                        frame.Push(TsValue.FromBigInt(AsBigInteger(left) % divisor));
                        break;
                    }
                    ulong r = AsUInt64(right);
                    if (r == 0) throw new InvalidOperationException("Division by zero");
                    frame.Push(TsValue.FromUInt64(AsUInt64(left) % r));
                    break;
                }
                case Opcodes.AndU64: // AND_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(UsesBigIntSemantics(left, right)
                        ? TsValue.FromBigInt(AsBigInteger(left) & AsBigInteger(right))
                        : TsValue.FromUInt64(AsUInt64(left) & AsUInt64(right)));
                    break;
                }
                case Opcodes.OrU64: // OR_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(UsesBigIntSemantics(left, right)
                        ? TsValue.FromBigInt(AsBigInteger(left) | AsBigInteger(right))
                        : TsValue.FromUInt64(AsUInt64(left) | AsUInt64(right)));
                    break;
                }
                case Opcodes.XorU64: // XOR_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(UsesBigIntSemantics(left, right)
                        ? TsValue.FromBigInt(AsBigInteger(left) ^ AsBigInteger(right))
                        : TsValue.FromUInt64(AsUInt64(left) ^ AsUInt64(right)));
                    break;
                }
                case Opcodes.NotU64: // NOT_U64
                {
                    var value = frame.Pop();
                    frame.Push(value is TsBigIntValue
                        ? TsValue.FromBigInt(~AsBigInteger(value))
                        : TsValue.FromUInt64(~AsUInt64(value)));
                    break;
                }
                case Opcodes.ShlU64: // SHL_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    var shift = AsInt32(right);
                    frame.Push(UsesBigIntSemantics(left, right)
                        ? TsValue.FromBigInt(AsBigInteger(left) << shift)
                        : TsValue.FromUInt64(AsUInt64(left) << shift));
                    break;
                }
                case Opcodes.ShrU64: // SHR_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    var shift = AsInt32(right);
                    frame.Push(UsesBigIntSemantics(left, right)
                        ? TsValue.FromBigInt(AsBigInteger(left) >> shift)
                        : TsValue.FromUInt64(AsUInt64(left) >> shift));
                    break;
                }

                // ── F64 arithmetic ──

                case Opcodes.AddF64: // ADD_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromFloat64(AsFloat64(left) + AsFloat64(right)));
                    break;
                }
                case Opcodes.SubF64: // SUB_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromFloat64(AsFloat64(left) - AsFloat64(right)));
                    break;
                }
                case Opcodes.MulF64: // MUL_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromFloat64(AsFloat64(left) * AsFloat64(right)));
                    break;
                }
                case Opcodes.DivF64: // DIV_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromFloat64(AsFloat64(left) / AsFloat64(right)));
                    break;
                }
                case Opcodes.ModF64: // MOD_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromFloat64(AsFloat64(left) % AsFloat64(right)));
                    break;
                }
                case Opcodes.NegF64: // NEG_F64
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromFloat64(-AsFloat64(val)));
                    break;
                }

                // ── F32 arithmetic ──

                case Opcodes.AddF32: // ADD_F32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromFloat32(AsFloat32(left) + AsFloat32(right)));
                    break;
                }
                case Opcodes.SubF32: // SUB_F32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromFloat32(AsFloat32(left) - AsFloat32(right)));
                    break;
                }
                case Opcodes.MulF32: // MUL_F32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromFloat32(AsFloat32(left) * AsFloat32(right)));
                    break;
                }
                case Opcodes.DivF32: // DIV_F32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    float r = AsFloat32(right);
                    if (r == 0f) throw new InvalidOperationException("Division by zero");
                    frame.Push(TsValue.FromFloat32(AsFloat32(left) / r));
                    break;
                }
                case Opcodes.NegF32: // NEG_F32
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromFloat32(-AsFloat32(val)));
                    break;
                }

                // ── I32 bitwise ──

                case Opcodes.AndI32: // AND_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt32(AsInt32(left) & AsInt32(right)));
                    break;
                }
                case Opcodes.OrI32: // OR_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt32(AsInt32(left) | AsInt32(right)));
                    break;
                }
                case Opcodes.XorI32: // XOR_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt32(AsInt32(left) ^ AsInt32(right)));
                    break;
                }
                case Opcodes.NotI32: // NOT_I32
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromInt32(~AsInt32(val)));
                    break;
                }

                // ── I32 comparison ──

                case Opcodes.CmpEqI32: // CMP_EQ_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt32(left) == AsInt32(right)));
                    break;
                }
                case Opcodes.CmpNeI32: // CMP_NE_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt32(left) != AsInt32(right)));
                    break;
                }
                case Opcodes.CmpLtI32: // CMP_LT_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt32(left) < AsInt32(right)));
                    break;
                }
                case Opcodes.CmpLeI32: // CMP_LE_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt32(left) <= AsInt32(right)));
                    break;
                }
                case Opcodes.CmpGtI32: // CMP_GT_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt32(left) > AsInt32(right)));
                    break;
                }
                case Opcodes.CmpGeI32: // CMP_GE_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt32(left) >= AsInt32(right)));
                    break;
                }

                // ── I64 comparison ──

                case Opcodes.CmpEqI64: // CMP_EQ_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt64(left) == AsInt64(right)));
                    break;
                }
                case Opcodes.CmpNeI64: // CMP_NE_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt64(left) != AsInt64(right)));
                    break;
                }
                case Opcodes.CmpLtI64: // CMP_LT_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt64(left) < AsInt64(right)));
                    break;
                }
                case Opcodes.CmpLeI64: // CMP_LE_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt64(left) <= AsInt64(right)));
                    break;
                }
                case Opcodes.CmpGtI64: // CMP_GT_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt64(left) > AsInt64(right)));
                    break;
                }
                case Opcodes.CmpGeI64: // CMP_GE_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt64(left) >= AsInt64(right)));
                    break;
                }

                // ── U64 comparison ──

                case Opcodes.CmpEqU64: // CMP_EQ_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    if (UsesBigIntSemantics(left, right))
                    {
                        frame.Push(new TsBoolValue(AsBigInteger(left) == AsBigInteger(right)));
                        break;
                    }
                    frame.Push(new TsBoolValue(AsUInt64(left) == AsUInt64(right)));
                    break;
                }
                case Opcodes.CmpNeU64: // CMP_NE_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    if (UsesBigIntSemantics(left, right))
                    {
                        frame.Push(new TsBoolValue(AsBigInteger(left) != AsBigInteger(right)));
                        break;
                    }
                    frame.Push(new TsBoolValue(AsUInt64(left) != AsUInt64(right)));
                    break;
                }
                case Opcodes.CmpLtU64: // CMP_LT_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    if (UsesBigIntSemantics(left, right))
                    {
                        frame.Push(new TsBoolValue(AsBigInteger(left) < AsBigInteger(right)));
                        break;
                    }
                    frame.Push(new TsBoolValue(AsUInt64(left) < AsUInt64(right)));
                    break;
                }
                case Opcodes.CmpLeU64: // CMP_LE_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    if (UsesBigIntSemantics(left, right))
                    {
                        frame.Push(new TsBoolValue(AsBigInteger(left) <= AsBigInteger(right)));
                        break;
                    }
                    frame.Push(new TsBoolValue(AsUInt64(left) <= AsUInt64(right)));
                    break;
                }
                case Opcodes.CmpGtU64: // CMP_GT_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    if (UsesBigIntSemantics(left, right))
                    {
                        frame.Push(new TsBoolValue(AsBigInteger(left) > AsBigInteger(right)));
                        break;
                    }
                    frame.Push(new TsBoolValue(AsUInt64(left) > AsUInt64(right)));
                    break;
                }
                case Opcodes.CmpGeU64: // CMP_GE_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    if (UsesBigIntSemantics(left, right))
                    {
                        frame.Push(new TsBoolValue(AsBigInteger(left) >= AsBigInteger(right)));
                        break;
                    }
                    frame.Push(new TsBoolValue(AsUInt64(left) >= AsUInt64(right)));
                    break;
                }

                // ── F64 comparison ──

                case Opcodes.CmpEqF64: // CMP_EQ_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat64(left) == AsFloat64(right)));
                    break;
                }
                case Opcodes.CmpNeF64: // CMP_NE_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat64(left) != AsFloat64(right)));
                    break;
                }
                case Opcodes.CmpLtF64: // CMP_LT_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat64(left) < AsFloat64(right)));
                    break;
                }
                case Opcodes.CmpLeF64: // CMP_LE_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat64(left) <= AsFloat64(right)));
                    break;
                }
                case Opcodes.CmpGtF64: // CMP_GT_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat64(left) > AsFloat64(right)));
                    break;
                }
                case Opcodes.CmpGeF64: // CMP_GE_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat64(left) >= AsFloat64(right)));
                    break;
                }

                // ── F32 comparison ──

                case Opcodes.CmpEqF32: // CMP_EQ_F32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat32(left) == AsFloat32(right)));
                    break;
                }
                case Opcodes.CmpNeF32: // CMP_NE_F32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat32(left) != AsFloat32(right)));
                    break;
                }
                case Opcodes.CmpLtF32: // CMP_LT_F32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat32(left) < AsFloat32(right)));
                    break;
                }
                case Opcodes.CmpLeF32: // CMP_LE_F32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat32(left) <= AsFloat32(right)));
                    break;
                }
                case Opcodes.CmpGtF32: // CMP_GT_F32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat32(left) > AsFloat32(right)));
                    break;
                }
                case Opcodes.CmpGeF32: // CMP_GE_F32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat32(left) >= AsFloat32(right)));
                    break;
                }

                // ── Logical ──

                case Opcodes.AndBool: // AND_BOOL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsBool(left) && AsBool(right)));
                    break;
                }
                case Opcodes.OrBool: // OR_BOOL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsBool(left) || AsBool(right)));
                    break;
                }
                case Opcodes.NotBool: // NOT_BOOL
                {
                    var val = frame.Pop();
                    frame.Push(new TsBoolValue(!AsBool(val)));
                    break;
                }

                case Opcodes.CmpEqAny: // CMP_EQ_ANY
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(GenericEquals(left, right)));
                    break;
                }
                case Opcodes.CmpNeAny: // CMP_NE_ANY
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(!GenericEquals(left, right)));
                    break;
                }
                case Opcodes.CmpStrictEqAny: // CMP_STRICT_EQ_ANY
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(StrictEquals(left, right)));
                    break;
                }
                case Opcodes.CmpStrictNeAny: // CMP_STRICT_NE_ANY
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(!StrictEquals(left, right)));
                    break;
                }
                case Opcodes.PowF64: // POW_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromFloat64(Math.Pow(AsFloat64(left), AsFloat64(right))));
                    break;
                }
                case Opcodes.TypeOf: // TYPEOF
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromString(val switch
                    {
                        TsStringValue => "string",
                        TsBoolValue => "boolean",
                        TsInt64Value or TsUInt64Value or TsBigIntValue => "bigint",
                        TsInt32Value or TsFloat32Value or
                        TsFloat64Value or TsDecimalValue => "number",
                        TsFunctionValue => "function",
                        TsNull => "object",
                        TsVoid => "undefined",
                        _ => "object"
                    }));
                    break;
                }
                case Opcodes.DeleteField: // DELETE_FIELD
                {
                    int fieldIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var fieldName = strings[fieldIdx];
                    var obj = frame.Pop();
                    if (obj is TsRegexValue && fieldName == "lastIndex")
                        throw NativeRegex.RejectCursorDeletion();
                    bool deleted = (obj is TsMapValue or TsSetValue) && fieldName == "size";
                    if (obj is TsObjectValue objVal)
                        deleted = objVal.Value.Fields.Remove(fieldName) || objVal.Value.NativeDateTimestamp.HasValue;
                    frame.Push(new TsBoolValue(deleted));
                    break;
                }
                case Opcodes.DeleteIndex: // DELETE_INDEX
                {
                    var indexVal = frame.Pop();
                    var obj = frame.Pop();
                    if (obj is TsRegexValue && NativeRegex.IsCursorKey(indexVal))
                        throw NativeRegex.RejectCursorDeletion();
                    bool deleted = false;
                    if (obj is TsArrayValue arrVal && indexVal is TsInt32Value idx)
                    {
                        int i = idx.Value;
                        if (i >= 0 && i < arrVal.Value.Count && !arrVal.Value.IsHole(i))
                        {
                            arrVal.Value.DeleteAt(i);
                            deleted = true;
                        }
                    }
                    else if (obj is TsObjectValue { Value.NativeDateTimestamp: not null } date)
                    {
                        date.Value.Fields.Remove(ToPropertyKey(indexVal));
                        deleted = true;
                    }
                    else if (obj is TsMapValue mapVal)
                    {
                        deleted = NativeMap.IsSizeKey(indexVal) || mapVal.Value.Remove(indexVal);
                    }
                    else if (obj is TsSetValue)
                    {
                        deleted = NativeSet.IsSizeKey(indexVal);
                    }
                    frame.Push(new TsBoolValue(deleted));
                    break;
                }

                // ── I64 bitwise ──

                case Opcodes.AndI64: // AND_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt64(AsInt64(left) & AsInt64(right)));
                    break;
                }
                case Opcodes.OrI64: // OR_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt64(AsInt64(left) | AsInt64(right)));
                    break;
                }
                case Opcodes.XorI64: // XOR_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt64(AsInt64(left) ^ AsInt64(right)));
                    break;
                }
                case Opcodes.NotI64: // NOT_I64
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromInt64(~AsInt64(val)));
                    break;
                }
                case Opcodes.ShlI32: // SHL_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt32(AsInt32(left) << AsInt32(right)));
                    break;
                }
                case Opcodes.ShrI32: // SHR_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt32(AsInt32(left) >> AsInt32(right)));
                    break;
                }
                case Opcodes.ShrU32: // SHR_U32 / JavaScript >>>
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    var shift = AsInt32(right) & 31;
                    frame.Push(TsValue.FromFloat64((uint)AsInt32(left) >> shift));
                    break;
                }
                case Opcodes.ShlI64: // SHL_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt64(AsInt64(left) << AsInt32(right)));
                    break;
                }
                case Opcodes.ShrI64: // SHR_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt64(AsInt64(left) >> AsInt32(right)));
                    break;
                }

                // ── Control flow ──

                case Opcodes.Branch: // BRANCH
                {
                    int target = ReadInt32(bytecode, ref frame.InstructionPointer);
                    frame.InstructionPointer = target;
                    break;
                }
                case Opcodes.BranchTrue: // BRANCH_TRUE
                {
                    int target = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var cond = frame.Pop();
                    if (AsBool(cond))
                        frame.InstructionPointer = target;
                    break;
                }
                case Opcodes.BranchFalse: // BRANCH_FALSE
                {
                    int target = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var cond = frame.Pop();
                    if (!AsBool(cond))
                        frame.InstructionPointer = target;
                    break;
                }

                // ── Functions ──

                case Opcodes.Call: // CALL
                {
                    int funcIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    int argCount = ReadInt32(bytecode, ref frame.InstructionPointer);

                    var calleeName = strings[funcIdx];

                    if (calleeName == NativeRegex.ReadTypedTestReference)
                    {
                        if (argCount != 1)
                            throw new InvalidOperationException("RegExp typed reference requires one operand");
                        frame.Push(NativeRegex.ReadTypedReference(frame.Pop()));
                    }
                    else if (calleeName is NativeSet.InvokeMemberReference or NativeRegex.InvokeMemberReference)
                    {
                        if (argCount != 3)
                            throw new InvalidOperationException(calleeName == NativeSet.InvokeMemberReference
                                ? "Set member-reference call requires three operands"
                                : "RegExp member-reference call requires three operands");
                        var positional = ToArgumentArray(frame.Pop());
                        var callable = frame.Pop();
                        var receiver = frame.Pop();
                        var invocationReceiver = (calleeName == NativeSet.InvokeMemberReference &&
                            receiver is TsSetValue && NativeSet.IsIntrinsicReference(callable))
                            || (receiver is TsRegexValue && NativeRegex.IsIntrinsicReference(callable))
                            ? receiver : null;
                        frame.Push(InvokeCallable(callable, positional, module, ctx, frame, invocationReceiver));
                    }
                    else if (HigherOrderIntrinsics.Contains(calleeName))
                    {
                        var intrinsicArgs = new TsValue[argCount];
                        for (int i = argCount - 1; i >= 0; i--)
                            intrinsicArgs[i] = frame.Pop();
                        frame.Push(ExecuteHigherOrderIntrinsic(calleeName, intrinsicArgs, module, ctx, frame));
                    }
                    else if (Builtins.TryGet(calleeName, out var builtin))
                    {
                        var builtinArgs = new TsValue[argCount];
                        for (int i = argCount - 1; i >= 0; i--)
                            builtinArgs[i] = frame.Pop();
                        frame.Push(builtin(builtinArgs));
                    }
                    else if (TryGetHostFunction(calleeName, out var hostFunc))
                    {
                        var hostArgs = new TsValue[argCount];
                        for (int i = argCount - 1; i >= 0; i--)
                            hostArgs[i] = frame.Pop();
                        var result = hostFunc(calleeName, hostArgs);
                        frame.Push(result ?? TsValue.Null);
                    }
                    else if (TryResolveCachedCallee(frame.Function, instructionOffset, calleeName, module, out int calleeIdx))
                    {
                        var calleeFunc = module.Functions[calleeIdx];
                        _profile.RecordCall(module.Name, calleeFunc.Name);
                        var directArgs = new TsValue[argCount];
                        for (int i = argCount - 1; i >= 0; i--)
                            directArgs[i] = frame.Pop();
                        var newFrame = CreateCallFrame(calleeFunc, frame, directArgs, ctx);

                        if (calleeFunc.IsGenerator)
                        {
                            frame.Push(new TsGeneratorValue(module, newFrame));
                            break;
                        }

                        frame.Push(ExecuteManagedFrame(calleeFunc, newFrame, module, ctx));
                    }
                    else
                    {
                        throw new InvalidOperationException($"Function '{calleeName}' not found");
                    }
                    break;
                }

                case Opcodes.LoadFunc: // LOAD_FUNC
                {
                    int nameIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var name = strings[nameIdx];
                    frame.Push(NativeSet.GetReference(name) ?? new TsFunctionValue(name));
                    break;
                }

                case Opcodes.MakeClosure: // MAKE_CLOSURE
                {
                    int nameIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    int captureCount = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var captured = new TsValue[captureCount];
                    for (int i = captureCount - 1; i >= 0; i--)
                        captured[i] = frame.Pop();
                    frame.Push(new TsFunctionValue(strings[nameIdx], captured));
                    break;
                }

                case Opcodes.CallDynamic: // CALL_DYNAMIC
                {
                    int argCount = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var dynArgs = new TsValue[argCount];
                    for (int i = argCount - 1; i >= 0; i--)
                        dynArgs[i] = frame.Pop();
                    var calleeValue = frame.Pop();

                    frame.Push(InvokeCallable(calleeValue, dynArgs, module, ctx, frame));
                    break;
                }

                case Opcodes.CallDynamicArray:
                {
                    var argumentArray = frame.Pop();
                    var calleeValue = frame.Pop();
                    frame.Push(InvokeCallable(calleeValue, ToArgumentArray(argumentArray), module, ctx, frame));
                    break;
                }

                case Opcodes.CallHost: // CALL_HOST
                {
                    int funcIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    int argCount = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var hostName = strings[funcIdx];
                    var hostArgs = new TsValue[argCount];
                    for (int i = argCount - 1; i >= 0; i--)
                        hostArgs[i] = frame.Pop();
                    if (TryGetHostFunction(hostName, out var hostFunc))
                    {
                        var result = hostFunc(hostName, hostArgs);
                        frame.Push(result ?? TsValue.Null);
                    }
                    else
                    {
                        frame.Push(TsValue.Null);
                    }
                    break;
                }

                case Opcodes.Return: // RETURN
                {
                    return frame.Pop();
                }
                case Opcodes.ReturnVoid: // RETURN_VOID
                {
                    return null;
                }

                case Opcodes.Yield:
                {
                    throw new TsGeneratorYieldException(frame.Pop());
                }

                case Opcodes.CallVirt: // CALL_VIRT
                {
                    int funcIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    int argCount = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var calleeName = strings[funcIdx];
                    var callArgs = new TsValue[argCount];
                    for (int i = argCount - 1; i >= 0; i--)
                        callArgs[i] = frame.Pop();
                    var resolvedName = ResolveVirtualCalleeName(calleeName, callArgs);

                    // Do not let a previously cached managed method at this bare
                    // selector consume an actual native RegExp receiver.
                    if (calleeName == "test" && callArgs.Length > 0 && callArgs[0] is TsRegexValue regex)
                    {
                        frame.Push(NativeRegex.Test(regex, callArgs.Length > 1 ? callArgs[1] : TsValue.Void));
                        break;
                    }

                    // A qualified Set operand selects the native method regardless of
                    // receiver; a bare selector does so only for an actual native Set.
                    if (NativeSet.IsMethod(calleeName) ||
                        (callArgs.Length > 0 && callArgs[0] is TsSetValue && NativeSet.IsMethod(resolvedName)))
                    {
                        frame.Push(InvokeNativeSet(resolvedName, callArgs, module, ctx, frame));
                    }
                    else if (TryGetVirtualFieldCallable(calleeName, callArgs, out var fieldCallable, out var fieldArgs))
                    {
                        frame.Push(InvokeCallable(fieldCallable, fieldArgs, module, ctx, frame));
                    }
                    else if (TryInvokeGeneratorMethod(resolvedName, callArgs, module, ctx, frame, out var generatorResult)
                        || (resolvedName == calleeName && TryInvokeGeneratorMethod(calleeName, callArgs, module, ctx, frame, out generatorResult)))
                    {
                        frame.Push(generatorResult);
                    }
                    // A polymorphic site's cached managed method must not steal a genuine Map call.
                    else if (callArgs.Length > 0 && callArgs[0] is TsMapValue && NativeMap.IsMethod(resolvedName))
                    {
                        frame.Push(InvokeNativeMap(resolvedName, callArgs, module, ctx, frame));
                    }
                    else if (TryResolveCachedCallee(frame.Function, instructionOffset, resolvedName, module, out int calleeIdx)
                        || (resolvedName == calleeName && TryResolveCachedCallee(frame.Function, instructionOffset, calleeName, module, out calleeIdx)))
                    {
                        var calleeFunc = module.Functions[calleeIdx];
                        _profile.RecordCall(module.Name, calleeFunc.Name);
                        var newFrame = CreateCallFrame(calleeFunc, frame, callArgs, ctx);
                        if (calleeFunc.IsGenerator)
                        {
                            frame.Push(new TsGeneratorValue(module, newFrame));
                            break;
                        }
                        frame.Push(ExecuteManagedFrame(calleeFunc, newFrame, module, ctx));
                    }
                    else if (!NativeSet.IsMethod(resolvedName) && (HigherOrderIntrinsics.Contains(resolvedName)
                        || (resolvedName == calleeName && HigherOrderIntrinsics.Contains(calleeName))))
                    {
                        frame.Push(ExecuteHigherOrderIntrinsic(resolvedName, callArgs, module, ctx, frame));
                    }
                    else if (!NativeSet.IsMethod(resolvedName) && (Builtins.TryGet(resolvedName, out var builtin)
                        || (resolvedName == calleeName && Builtins.TryGet(calleeName, out builtin))))
                    {
                        frame.Push(builtin(callArgs));
                    }
                    else if (TryGetHostFunction(resolvedName, out var hostFunc)
                        || (resolvedName == calleeName && TryGetHostFunction(calleeName, out hostFunc)))
                    {
                        var result = hostFunc(resolvedName, callArgs);
                        frame.Push(result ?? TsValue.Null);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Function '{calleeName}' not found for receiver {DescribeReceiver(callArgs)} argCount={callArgs.Length}");
                    }
                    break;
                }

                // ── Object ──

                case Opcodes.NewObject: // NEW_OBJECT
                {
                    int typeIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    int argCount = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var typeName = strings[typeIdx];

                    if (typeName == "Uint8Array")
                    {
                        var args = new TsValue[argCount];
                        for (int i = argCount - 1; i >= 0; i--)
                            args[i] = frame.Pop();
                        frame.Push(Uint8ArrayOperations.Construct(args));
                        break;
                    }

                    if (typeName == "Date")
                    {
                        // Argument expressions have already run. Date consumes
                        // at most seven inputs; discard the rest from the stack.
                        int used = Math.Min(argCount, 7);
                        for (int i = argCount; i > used; --i)
                            frame.Pop();
                        var args = new TsValue[used];
                        for (int i = used - 1; i >= 0; --i)
                            args[i] = frame.Pop();
                        frame.Push(NativeDate.Construct(ctx, args));
                        break;
                    }

                    if (typeName == "Map")
                    {
                        var args = new TsValue[argCount];
                        for (int i = argCount - 1; i >= 0; i--)
                            args[i] = frame.Pop();
                        var map = ctx.Heap.AllocateMap();
                        PopulateMap(map, args, module, ctx, frame);
                        frame.Push(new TsMapValue(map));
                        break;
                    }

                    if (typeName == "Set")
                    {
                        var args = new TsValue[argCount];
                        for (int i = argCount - 1; i >= 0; i--)
                            args[i] = frame.Pop();
                        var set = ctx.Heap.AllocateInterpretedSet();
                        PopulateSet(set, args, module, ctx, frame);
                        frame.Push(new TsSetValue(set));
                        break;
                    }

                    var obj = ctx.Heap.AllocateObject(typeName);

                    // JS-style error objects: constructor argument becomes the
                    // message field so thrown errors surface readable text.
                    if (typeName is "Error" or "RangeError" or "TypeError" &&
                        !module.FunctionIndex.ContainsKey($"{typeName}::.ctor"))
                    {
                        if (argCount > 0)
                        {
                            var message = frame.Pop();
                            for (int i = 1; i < argCount; i++) frame.Pop();
                            obj.SetField("message", message);
                        }
                        obj.SetField("name", TsValue.FromString(typeName));
                        frame.Push(TsValue.FromObject(obj));
                        break;
                    }

                    string ctorName = $"{typeName}::.ctor";
                    if (module.FunctionIndex.TryGetValue(ctorName, out int ctorIdx))
                    {
                        var ctorFunc = module.Functions[ctorIdx];
                        var ctorArgs = new TsValue[argCount + 1];
                        ctorArgs[0] = TsValue.FromObject(obj);
                        for (int i = argCount; i >= 1; i--)
                            ctorArgs[i] = frame.Pop();
                        var ctorFrame = CreateCallFrame(ctorFunc, frame, ctorArgs, ctx);

                        ExecuteManagedFrameRaw(ctorFrame, module, ctx);
                    }
                    else
                    {
                        for (int i = 0; i < argCount; i++) frame.Pop();
                    }

                    frame.Push(TsValue.FromObject(obj));
                    break;
                }

                case Opcodes.NewArray: // NEW_ARRAY
                {
                    int capacity = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var arr = ctx.Heap.AllocateArray(capacity);
                    for (int i = capacity - 1; i >= 0; i--)
                        arr.Set(i, frame.Pop());
                    frame.Push(new TsArrayValue(arr));
                    break;
                }

                case Opcodes.ArrayAppend:
                {
                    var value = frame.Pop();
                    var target = frame.Pop();
                    if (target is not TsArrayValue arrayValue)
                        throw new InvalidOperationException("Array append requires an array target");
                    arrayValue.Value.Add(value);
                    ctx.CheckMemory();
                    break;
                }

                case Opcodes.ArrayAppendSpread:
                {
                    var source = frame.Pop();
                    var target = frame.Pop();
                    if (target is not TsArrayValue arrayValue)
                        throw new InvalidOperationException("Array spread requires an array target");
                    AppendSpread(arrayValue.Value, source);
                    ctx.CheckMemory();
                    break;
                }

                case Opcodes.IterableValues:
                {
                    var source = frame.Pop();
                    frame.Push(new TsArrayValue(MaterializeIterableValues(source)));
                    break;
                }

                case Opcodes.NewGenerator:
                {
                    var source = frame.Pop();
                    if (source is not TsArrayValue arrayValue)
                        throw new InvalidOperationException("Generator creation requires an array of yielded values");
                    frame.Push(new TsGeneratorValue(arrayValue.Value));
                    break;
                }

                case Opcodes.LoadElement: // LOAD_ELEMENT
                {
                    var index = frame.Pop();
                    var target = frame.Pop();
                    if (target is TsArrayValue arrVal)
                        frame.Push(arrVal.Value.Get(AsInt32(index)));
                    else if (target is TsUint8ArrayValue bytesVal)
                        frame.Push(Uint8ArrayOperations.ReadElement(bytesVal, index));
                    else if (target is TsStringValue strTarget)
                    {
                        int chIdx = AsInt32(index);
                        frame.Push(chIdx >= 0 && chIdx < strTarget.Value.Length
                            ? TsValue.FromString(strTarget.Value[chIdx].ToString())
                            : TsValue.Null);
                    }
                    else if (target is TsMapValue map)
                        frame.Push(NativeMap.IsSizeKey(index) ? ReadFieldValue(map, "size") : map.Value.Get(index));
                    else if (target is TsSetValue set && index is TsStringValue name)
                        frame.Push(NativeSet.ReadProperty(set, name.Value));
                    else if (target is TsRegexValue regex && index is TsStringValue regexKey)
                        frame.Push(NativeRegex.ReadProperty(regex, regexKey.Value));
                    else if (target is TsObjectValue objectTarget)
                        frame.Push(ReadFieldValue(objectTarget, ToPropertyKey(index)));
                    else if (target is TsNull)
                        throw new InvalidOperationException("Cannot index a null value");
                    else
                        frame.Push(TsValue.Null);
                    break;
                }

                case Opcodes.StoreElement: // STORE_ELEMENT
                {
                    var value = frame.Pop();
                    var index = frame.Pop();
                    var target = frame.Pop();
                    if (target is TsArrayValue arrVal)
                        arrVal.Value.Set(AsInt32(index), value);
                    else if (target is TsUint8ArrayValue bytesVal)
                        Uint8ArrayOperations.WriteElement(bytesVal, index, value);
                    else if (target is TsMapValue map)
                    {
                        if (NativeMap.IsSizeKey(index))
                            throw NativeMap.TypeError(NativeMap.ReadonlySizeMessage);
                        map.Value.Set(index, value);
                    }
                    else if (target is TsSetValue && NativeSet.IsSizeKey(index))
                        throw NativeSet.TypeError(NativeSet.ReadonlySizeMessage);
                    else if (target is TsRegexValue regex && NativeRegex.IsCursorKey(index))
                        regex.ScriptLastIndex = value;
                    else if (target is TsObjectValue objectTarget)
                        objectTarget.Value.SetField(ToPropertyKey(index), value);
                    else if (target is TsNull)
                        throw new InvalidOperationException("Cannot index a null value");
                    break;
                }

                case Opcodes.EnterTry: // ENTER_TRY
                {
                    int handlerOffset = ReadInt32(bytecode, ref frame.InstructionPointer);
                    frame.TryHandlers ??= new Stack<TryHandler>();
                    frame.TryHandlers.Push(new TryHandler(handlerOffset, frame.StackPointer));
                    break;
                }

                case Opcodes.LeaveTry: // LEAVE_TRY
                {
                    if (frame.TryHandlers is { Count: > 0 })
                        frame.TryHandlers.Pop();
                    break;
                }

                case Opcodes.NewMap: // NEW_MAP
                {
                    var map = ctx.Heap.AllocateMap();
                    frame.Push(new TsMapValue(map));
                    break;
                }

                case Opcodes.Dup: // DUP
                {
                    var val = frame.Peek();
                    frame.Push(val);
                    break;
                }

                case Opcodes.Pop: // POP
                    frame.Pop();
                    break;

                // ── String ──

                case Opcodes.ConcatString: // CONCAT_STRING
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromString(ToJsString(left) + ToJsString(right)));
                    break;
                }

                // ── Async ──

                case Opcodes.Await: // AWAIT
                {
                    var val = frame.Pop();
                    frame.Push(AwaitValue(val, ctx));
                    break;
                }

                // ── Exception ──

                case Opcodes.Throw: // THROW
                {
                    var val = frame.Pop();
                    throw new TsThrownException(val);
                }

                // ── Type/Null checks ──

                case Opcodes.TypeCheck: // TYPE_CHECK
                {
                    int typeIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var typeName = strings[typeIdx];
                    var val = frame.Peek();
                    bool match = val switch
                    {
                        TsObjectValue obj => obj.Value.TypeName == typeName,
                        TsUint8ArrayValue => typeName == "Uint8Array" || typeName == "Uint8ClampedArray",
                        TsStringValue => typeName == "string",
                        TsInt32Value => typeName == "int32",
                        TsInt64Value => typeName == "int64" || typeName == "bigint",
                        TsUInt64Value => typeName == "uint64" || typeName == "bigint",
                        TsBigIntValue => typeName == "bigint",
                        TsFloat32Value => typeName == "float32",
                        TsFloat64Value => typeName == "float64",
                        TsDecimalValue => typeName == "decimal",
                        TsBoolValue => typeName == "bool",
                        TsNull => false,
                        _ => false
                    };
                    frame.Push(new TsBoolValue(match));
                    break;
                }
                case Opcodes.NullCheck: // NULL_CHECK
                {
                    var val = frame.Peek();
                    frame.Push(new TsBoolValue(val is TsNull or TsVoid));
                    break;
                }

                // ── Decimal arithmetic ──

                case Opcodes.AddDecimal: // ADD_DECIMAL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromDecimal(AsDecimal(left) + AsDecimal(right)));
                    break;
                }
                case Opcodes.SubDecimal: // SUB_DECIMAL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromDecimal(AsDecimal(left) - AsDecimal(right)));
                    break;
                }
                case Opcodes.MulDecimal: // MUL_DECIMAL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromDecimal(AsDecimal(left) * AsDecimal(right)));
                    break;
                }
                case Opcodes.DivDecimal: // DIV_DECIMAL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    decimal r = AsDecimal(right);
                    if (r == 0m) throw new InvalidOperationException("Division by zero");
                    frame.Push(TsValue.FromDecimal(AsDecimal(left) / r));
                    break;
                }
                case Opcodes.ModDecimal: // MOD_DECIMAL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    decimal r = AsDecimal(right);
                    if (r == 0m) throw new InvalidOperationException("Division by zero");
                    frame.Push(TsValue.FromDecimal(AsDecimal(left) % r));
                    break;
                }
                case Opcodes.NegDecimal: // NEG_DECIMAL
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromDecimal(-AsDecimal(val)));
                    break;
                }

                // ── Decimal comparison ──

                case Opcodes.CmpEqDecimal: // CMP_EQ_DECIMAL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsDecimal(left) == AsDecimal(right)));
                    break;
                }
                case Opcodes.CmpNeDecimal: // CMP_NE_DECIMAL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsDecimal(left) != AsDecimal(right)));
                    break;
                }
                case Opcodes.CmpLtDecimal: // CMP_LT_DECIMAL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsDecimal(left) < AsDecimal(right)));
                    break;
                }
                case Opcodes.CmpLeDecimal: // CMP_LE_DECIMAL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsDecimal(left) <= AsDecimal(right)));
                    break;
                }
                case Opcodes.CmpGtDecimal: // CMP_GT_DECIMAL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsDecimal(left) > AsDecimal(right)));
                    break;
                }
                case Opcodes.CmpGeDecimal: // CMP_GE_DECIMAL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsDecimal(left) >= AsDecimal(right)));
                    break;
                }

                // ── Convert ──

                case Opcodes.ConvI32I64: // CONV_I32_I64
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromInt64(AsInt32(val)));
                    break;
                }
                case Opcodes.ConvI64I32: // CONV_I64_I32
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromInt32((int)AsInt64(val)));
                    break;
                }
                case Opcodes.ConvI32F64: // CONV_I32_F64
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromFloat64(AsInt32(val)));
                    break;
                }
                case Opcodes.ConvF64I32: // CONV_F64_I32
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromInt32((int)AsFloat64(val)));
                    break;
                }
                case Opcodes.ConvI32F32: // CONV_I32_F32
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromFloat32(AsInt32(val)));
                    break;
                }
                case Opcodes.ConvF32I32: // CONV_F32_I32
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromInt32((int)AsFloat32(val)));
                    break;
                }
                case Opcodes.ConvU64I64: // CONV_U64_I64
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromInt64(unchecked((long)AsUInt64(val))));
                    break;
                }
                case Opcodes.ConvI64U64: // CONV_I64_U64
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromUInt64(unchecked((ulong)AsInt64(val))));
                    break;
                }
                case Opcodes.ConvU64F64: // CONV_U64_F64
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromFloat64(AsUInt64(val)));
                    break;
                }
                case Opcodes.ConvF64U64: // CONV_F64_U64
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromUInt64(Convert.ToUInt64(AsFloat64(val))));
                    break;
                }
                case Opcodes.ConvU64I32: // CONV_U64_I32
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromInt32(unchecked((int)AsUInt64(val))));
                    break;
                }
                case Opcodes.ConvI32U64: // CONV_I32_U64
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromUInt64(unchecked((ulong)AsInt32(val))));
                    break;
                }

                case Opcodes.ConvF32F64: // CONV_F32_F64
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromFloat64(AsFloat32(val)));
                    break;
                }
                case Opcodes.ConvF64F32: // CONV_F64_F32
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromFloat32((float)AsFloat64(val)));
                    break;
                }

                default:
                    throw new InvalidOperationException($"Unknown opcode: 0x{op:X2}");
            }
        }

        return null;
    }

    // ────────────────────────────────────────────────────────
    //  Value coercion helpers
    // ────────────────────────────────────────────────────────

    private static int AsInt32(TsValue value) => value switch
    {
        TsInt32Value v => v.Value,
        TsInt64Value v => (int)v.Value,
        TsUInt64Value v => unchecked((int)v.Value),
        TsBigIntValue v => (int)v.Value,
        TsFloat32Value v => (int)v.Value,
        TsFloat64Value v => (int)v.Value,
        TsDecimalValue v => (int)v.Value,
        TsBoolValue v => v.Value ? 1 : 0,
        _ => 0
    };

    private static long AsInt64(TsValue value) => value switch
    {
        TsInt32Value v => v.Value,
        TsInt64Value v => v.Value,
        TsUInt64Value v => unchecked((long)v.Value),
        TsBigIntValue v => (long)v.Value,
        TsFloat32Value v => (long)v.Value,
        TsFloat64Value v => (long)v.Value,
        TsDecimalValue v => (long)v.Value,
        _ => 0
    };

    private static ulong AsUInt64(TsValue value) => value switch
    {
        TsInt32Value v => unchecked((ulong)v.Value),
        TsInt64Value v => unchecked((ulong)v.Value),
        TsUInt64Value v => v.Value,
        TsBigIntValue v => (ulong)v.Value,
        TsFloat32Value v => Convert.ToUInt64(v.Value),
        TsFloat64Value v => Convert.ToUInt64(v.Value),
        TsDecimalValue v => Convert.ToUInt64(v.Value),
        _ => 0
    };

    private static float AsFloat32(TsValue value) => value switch
    {
        TsInt32Value v => v.Value,
        TsInt64Value v => v.Value,
        TsUInt64Value v => v.Value,
        TsBigIntValue v => (float)v.Value,
        TsFloat32Value v => v.Value,
        TsFloat64Value v => (float)v.Value,
        TsDecimalValue v => (float)v.Value,
        _ => 0f
    };

    private static double AsFloat64(TsValue value) => value switch
    {
        TsInt32Value v => v.Value,
        TsInt64Value v => v.Value,
        TsUInt64Value v => v.Value,
        TsBigIntValue v => (double)v.Value,
        TsFloat32Value v => v.Value,
        TsFloat64Value v => v.Value,
        TsDecimalValue v => (double)v.Value,
        _ => 0.0
    };

    private static decimal AsDecimal(TsValue value) => value switch
    {
        TsInt32Value v => v.Value,
        TsInt64Value v => v.Value,
        TsUInt64Value v => v.Value,
        TsBigIntValue v => (decimal)v.Value,
        TsFloat32Value v => (decimal)v.Value,
        TsFloat64Value v => (decimal)v.Value,
        TsDecimalValue v => v.Value,
        _ => 0m
    };

    // JS truthiness: 0, NaN, "" and null/undefined are falsy.
    private static bool AsBool(TsValue value) => value switch
    {
        TsBoolValue v => v.Value,
        TsInt32Value v => v.Value != 0,
        TsInt64Value v => v.Value != 0,
        TsUInt64Value v => v.Value != 0,
        TsBigIntValue v => v.Value != BigInteger.Zero,
        TsFloat32Value v => v.Value != 0f && !float.IsNaN(v.Value),
        TsFloat64Value v => v.Value != 0.0 && !double.IsNaN(v.Value),
        TsDecimalValue v => v.Value != 0m,
        TsStringValue v => v.Value.Length > 0,
        TsNull or TsVoid => false,
        _ => true
    };

    // ────────────────────────────────────────────────────────
    //  Bytecode readers
    // ────────────────────────────────────────────────────────

    private static readonly HashSet<string> HigherOrderIntrinsics = new(StringComparer.Ordinal)
    {
        "Array::map", "Array::filter", "Array::forEach", "Array::reduce",
        "Array::some", "Array::every", "Array::find", "Array::findIndex", "Array::findLast", "Array::findLastIndex",
        "Array::sort", "Array::flatMap", "Array::from", "Map::forEach", "Set::forEach"
    };

    private TsValue InvokeNativeMap(string name, TsValue[] args, BytecodeModule module,
        ExecutionContext ctx, CallFrame frame)
    {
        if (name == "Map::forEach")
            return ExecuteHigherOrderIntrinsic(name, args, module, ctx, frame);
        if (Builtins.TryGet(name, out var builtin))
            return builtin(args);
        throw new InvalidOperationException($"Unregistered native Map method '{name}'");
    }

    private TsValue InvokeNativeSet(string name, TsValue[] args, BytecodeModule module,
        ExecutionContext ctx, CallFrame frame)
    {
        if (name == NativeSet.ForEach)
            return ExecuteHigherOrderIntrinsic(name, args, module, ctx, frame);
        if (Builtins.TryGet(name, out var builtin))
            return builtin(args);
        throw new InvalidOperationException($"Unregistered native Set method '{name}'");
    }

    private enum SetCallbackKind { Invalid, ClassReference, Callable }

    // Classification follows InvokeCallable without running a callback or
    // accepting an unrelated higher-order selector as a function value.
    private SetCallbackKind ClassifySetCallback(TsFunctionValue callback, BytecodeModule module)
    {
        var name = callback.FunctionName;
        if (name.EndsWith("::.ctor", StringComparison.Ordinal))
            return SetCallbackKind.ClassReference;
        return NativeSet.IsMethod(name) || NativeMap.IsMethod(name) || Builtins.TryGet(name, out _)
            || module.FunctionIndex.ContainsKey(name) || TryGetHostFunction(name, out _)
            ? SetCallbackKind.Callable : SetCallbackKind.Invalid;
    }

    // Array members that take callbacks execute here, where the interpreter
    // can re-enter script functions for each element.
    private TsValue ExecuteHigherOrderIntrinsic(string name, TsValue[] args, BytecodeModule module,
        ExecutionContext ctx, CallFrame frame)
    {
        TsValue Invoke(TsValue callee, params TsValue[] callArgs) =>
            InvokeCallable(callee, callArgs, module, ctx, frame);

        if (name == "Array::from")
        {
            // Array.from(arrayLike [, mapFn]) — supports arrays, strings and
            // `{ length: n }` shapes.
            var source = args.Length > 0 ? args[0] : TsValue.Null;
            var mapFn = args.Length > 1 && args[1] is not TsVoid ? args[1] : null;
            var result = new TsArray();
            void AddMapped(TsValue value, int index)
            {
                result.Add(mapFn != null ? Invoke(mapFn, value, TsValue.FromInt32(index)) : value);
            }

            // The array-like length is fixed, but the mapper can change later fields.
            if (mapFn is TsFunctionValue function &&
                !function.FunctionName.EndsWith("::.ctor", StringComparison.Ordinal) &&
                source is TsObjectValue objectValue &&
                objectValue.Value.GetField("length") is var lengthValue and not TsNull and not TsVoid)
            {
                int length = AsInt32(lengthValue);
                for (int i = 0; i < length; i++)
                    AddMapped(objectValue.Value.GetField(i.ToString(CultureInfo.InvariantCulture)), i);
                return new TsArrayValue(result);
            }

            var values = MaterializeIterableValues(source);
            for (int i = 0; i < values.Count; i++)
                AddMapped(values.Get(i), i);
            return new TsArrayValue(result);
        }

        if (name == "Map::forEach")
        {
            var mapReceiver = NativeMap.RequireReceiver(args);
            if (NativeMap.ArgOrVoid(args, 1) is not TsFunctionValue mapCallback)
                throw NativeMap.TypeError("Map.forEach callback is not callable");

            var thisArg = NativeMap.ArgOrVoid(args, 2);
            var map = mapReceiver.Value;
            var cursor = map.BeginForEach();
            try
            {
                while (map.TryReadForEachEntry(cursor, out var entry))
                {
                    if (mapCallback.FunctionName.EndsWith("::.ctor", StringComparison.Ordinal))
                        throw NativeMap.TypeError("Class constructor cannot be invoked without 'new'");
                    InvokeCallable(mapCallback, [entry.Value, entry.Key, mapReceiver], module, ctx, frame, thisArg);
                }
            }
            finally
            {
                map.EndForEach(cursor);
            }
            return TsValue.Void;
        }

        if (name == NativeSet.ForEach)
        {
            var setReceiver = NativeSet.RequireReceiver(args);
            if (NativeSet.ArgOrVoid(args, 1) is not TsFunctionValue setCallback)
                throw NativeSet.TypeError("Set.forEach callback is not callable");
            var callbackKind = ClassifySetCallback(setCallback, module);
            if (callbackKind == SetCallbackKind.Invalid)
                throw NativeSet.TypeError("Set.forEach callback is not callable");

            var thisArg = NativeSet.ArgOrVoid(args, 2);
            var set = setReceiver.Value;
            var cursor = set.BeginForEach();
            try
            {
                while (set.TryReadForEachValue(cursor, out var value))
                {
                    if (callbackKind == SetCallbackKind.ClassReference)
                        throw NativeSet.TypeError("Class constructor cannot be invoked without 'new'");
                    InvokeCallable(setCallback, [value, value, setReceiver], module, ctx, frame, thisArg);
                }
            }
            finally
            {
                set.EndForEach(cursor);
            }
            return TsValue.Void;
        }

        if (args.Length == 0 || args[0] is not TsArrayValue receiver)
        {
            var actual = args.Length == 0 ? "<missing>" : DescribeValue(args[0]);
            throw new InvalidOperationException($"'{name}' requires an array receiver, got {actual}");
        }
        var arr = receiver.Value;
        var callback = args.Length > 1 ? args[1] : null;

        switch (name)
        {
            case "Array::map":
            {
                var initialLength = arr.Count;
                var mapped = new TsArray(initialLength);
                for (int i = 0; i < initialLength; i++)
                {
                    if (arr.IsHole(i))
                    {
                        mapped.SetHole(i);
                    }
                    else
                    {
                        mapped.Set(i, Invoke(callback!, arr.Get(i), TsValue.FromInt32(i), args[0]));
                    }
                }
                return new TsArrayValue(mapped);
            }
            case "Array::flatMap":
            {
                var initialLength = arr.Count;
                var flat = new TsArray(initialLength);
                for (int i = 0; i < initialLength; i++)
                {
                    if (arr.IsHole(i)) continue;
                    var produced = Invoke(callback!, arr.Get(i), TsValue.FromInt32(i), args[0]);
                    if (produced is TsArrayValue nested)
                    {
                        for (int j = 0; j < nested.Value.Count; j++)
                        {
                            if (nested.Value.IsHole(j)) continue;
                            flat.Add(nested.Value.Get(j));
                        }
                    }
                    else
                        flat.Add(produced);
                }
                return new TsArrayValue(flat);
            }
            case "Array::filter":
            {
                var initialLength = arr.Count;
                var filtered = new TsArray();
                for (int i = 0; i < initialLength; i++)
                {
                    if (arr.IsHole(i)) continue;
                    var element = arr.Get(i);
                    if (AsBool(Invoke(callback!, element, TsValue.FromInt32(i), args[0])))
                        filtered.Add(element);
                }
                return new TsArrayValue(filtered);
            }
            case "Array::forEach":
            {
                var initialLength = arr.Count;
                for (int i = 0; i < initialLength; i++)
                {
                    if (arr.IsHole(i)) continue;
                    Invoke(callback!, arr.Get(i), TsValue.FromInt32(i), args[0]);
                }
                return TsValue.Void;
            }
            case "Array::reduce":
            {
                int initialCount = arr.Count;
                if (callback is not TsFunctionValue reducer)
                {
                    var error = new TsObject("TypeError");
                    error.SetField("name", TsValue.FromString("TypeError"));
                    error.SetField("message", TsValue.FromString("Array.reduce callback is not callable"));
                    throw new TsThrownException(TsValue.FromObject(error));
                }
                int start = 0;
                TsValue accumulator;
                if (args.Length > 2)
                {
                    accumulator = args[2];
                }
                else
                {
                    while (start < initialCount && arr.IsHole(start)) start++;
                    if (start >= initialCount)
                    {
                        var error = new TsObject("TypeError");
                        error.SetField("name", TsValue.FromString("TypeError"));
                        error.SetField("message", TsValue.FromString("Reduce of empty array with no initial value"));
                        throw new TsThrownException(TsValue.FromObject(error));
                    }
                    accumulator = arr.Get(start);
                    start++;
                }
                for (int i = start; i < initialCount; i++)
                {
                    if (arr.IsHole(i)) continue;
                    accumulator = Invoke(reducer, accumulator, arr.Get(i), TsValue.FromInt32(i), args[0]);
                }
                return accumulator;
            }
            case "Array::some":
            {
                int initialCount = arr.Count;
                for (int i = 0; i < initialCount; i++)
                {
                    if (arr.IsHole(i)) continue;
                    if (AsBool(Invoke(callback!, arr.Get(i), TsValue.FromInt32(i), args[0])))
                        return new TsBoolValue(true);
                }
                return new TsBoolValue(false);
            }
            case "Array::every":
            {
                int initialCount = arr.Count;
                for (int i = 0; i < initialCount; i++)
                {
                    if (arr.IsHole(i)) continue;
                    if (!AsBool(Invoke(callback!, arr.Get(i), TsValue.FromInt32(i), args[0])))
                        return new TsBoolValue(false);
                }
                return new TsBoolValue(true);
            }
            case "Array::find":
            {
                var initialLength = arr.Count;
                for (int i = 0; i < initialLength; i++)
                {
                    var element = arr.Get(i);
                    if (AsBool(Invoke(callback!, element, TsValue.FromInt32(i), args[0])))
                        return element;
                }
                return TsValue.Void;
            }
            case "Array::findLast":
            case "Array::findLastIndex":
            {
                var returnIndex = name == "Array::findLastIndex";
                var initialLength = arr.Count;
                for (int i = initialLength - 1; i >= 0; i--)
                {
                    var element = arr.Get(i);
                    if (AsBool(Invoke(callback!, element, TsValue.FromInt32(i), args[0])))
                        return returnIndex ? TsValue.FromInt32(i) : element;
                }
                return returnIndex ? TsValue.FromInt32(-1) : TsValue.Void;
            }
            case "Array::findIndex":
            {
                var initialLength = arr.Count;
                for (int i = 0; i < initialLength; i++)
                    if (AsBool(Invoke(callback!, arr.Get(i), TsValue.FromInt32(i), args[0])))
                        return TsValue.FromInt32(i);
                return TsValue.FromInt32(-1);
            }
            case "Array::sort":
            {
                if (callback is null or TsVoid)
                {
                    var hasHole = false;
                    var eligible = true;
                    for (int i = 0; i < arr.Count; i++)
                    {
                        if (arr.IsHole(i))
                            hasHole = true;
                        else if (arr.Get(i) is not (TsStringValue or TsVoid))
                        {
                            eligible = false;
                            break;
                        }
                    }

                    if (hasHole && eligible)
                    {
                        // Sort only present entries; explicit undefined sorts after strings, not as a hole.
                        var present = new List<TsValue>();
                        for (int i = 0; i < arr.Count; i++)
                            if (!arr.IsHole(i))
                                present.Add(arr.Get(i));

                        var ordered = present
                            .OrderBy(static value => value is TsVoid)
                            .ThenBy(static value => (value as TsStringValue)?.Value, StringComparer.Ordinal)
                            .ToArray();
                        for (int i = 0; i < ordered.Length; i++) arr.Set(i, ordered[i]);
                        for (int i = ordered.Length; i < arr.Count; i++) arr.SetHole(i);
                        return receiver;
                    }
                }

                var items = new TsValue[arr.Count];
                var skipUndefinedComparison = callback is TsFunctionValue;
                for (int i = 0; i < arr.Count; i++)
                {
                    items[i] = arr.Get(i);
                    // Holes read as void too; preserve the existing sparse comparator path.
                    if (skipUndefinedComparison && arr.IsHole(i))
                        skipUndefinedComparison = false;
                }
                if (callback != null && callback is not TsVoid)
                {
                    Array.Sort(items, (a, b) =>
                    {
                        if (skipUndefinedComparison)
                        {
                            if (a is TsVoid) return b is TsVoid ? 0 : 1;
                            if (b is TsVoid) return -1;
                        }
                        var result = AsFloat64(Invoke(callback, a, b));
                        return double.IsNaN(result) ? 0 : Math.Sign(result);
                    });
                }
                else if (Array.TrueForAll(items, IsDefaultIntegerSortKey))
                    Array.Sort(items, static (a, b) =>
                        string.CompareOrdinal(DefaultIntegerSortKey(a), DefaultIntegerSortKey(b)));
                else if (BuildDefaultFractionalSortKeys(arr, items) is { } keys)
                    Array.Sort(keys, items, StringComparer.Ordinal);
                else
                    Array.Sort(items, DefaultSortComparison);
                for (int i = 0; i < items.Length; i++) arr.Set(i, items[i]);
                return receiver;

                static bool IsDefaultIntegerSortKey(TsValue item) =>
                    item is TsInt32Value ||
                    (item is TsFloat64Value { Value: var value } &&
                     double.IsFinite(value) && value >= int.MinValue && value <= int.MaxValue &&
                     value == Math.Truncate(value));

                // Called only after all items pass the guard. Format keys without replacing the elements.
                static string DefaultIntegerSortKey(TsValue item) =>
                    (item is TsInt32Value integer ? integer.Value : checked((int)((TsFloat64Value)item).Value))
                    .ToString(CultureInfo.InvariantCulture);

                // Only use ordinal keys when the entire dense numeric array has fixed-decimal
                // spellings in this domain. Keep the original wrappers and the legacy fallback.
                static string[]? BuildDefaultFractionalSortKeys(TsArray array, TsValue[] items)
                {
                    var keys = new string[items.Length];
                    var hasFraction = false;
                    for (int i = 0; i < items.Length; i++)
                    {
                        if (array.IsHole(i))
                            return null;
                        var item = items[i];
                        if (IsDefaultIntegerSortKey(item))
                        {
                            keys[i] = DefaultIntegerSortKey(item);
                            continue;
                        }

                        if (item is not TsFloat64Value { Value: var value } ||
                            !double.IsFinite(value) || value < int.MinValue || value > int.MaxValue ||
                            value == Math.Truncate(value) || Math.Abs(value) < 1e-4)
                            return null;

                        var key = value.ToString("R", CultureInfo.InvariantCulture);
                        if (!key.Contains('.') || key.Contains('e') || key.Contains('E'))
                            return null;
                        keys[i] = key;
                        hasFraction = true;
                    }
                    return hasFraction ? keys : null;
                }
            }
            default:
                throw new InvalidOperationException($"Unknown intrinsic '{name}'");
        }
    }

    private static int DefaultSortComparison(TsValue left, TsValue right)
    {
        if (left is TsStringValue ls && right is TsStringValue rs)
            return string.CompareOrdinal(ls.Value, rs.Value);
        return AsFloat64(left).CompareTo(AsFloat64(right));
    }

    private static bool GenericEquals(TsValue left, TsValue right)
    {
        if (left is TsNull or TsVoid && right is TsNull or TsVoid) return true;
        if (left is TsNull or TsVoid || right is TsNull or TsVoid) return false;
        if (left is TsStringValue ls && right is TsStringValue rs) return ls.Value == rs.Value;
        if (left is TsBoolValue lb && right is TsBoolValue rb) return lb.Value == rb.Value;
        if (left is TsObjectValue lo && right is TsObjectValue ro) return ReferenceEquals(lo.Value, ro.Value);
        if (left is TsArrayValue la && right is TsArrayValue ra) return ReferenceEquals(la.Value, ra.Value);
        if (left is TsUint8ArrayValue && right is TsUint8ArrayValue) return ReferenceEquals(left, right);
        if (IsBigIntValue(left) || IsBigIntValue(right)) return AsBigInteger(left) == AsBigInteger(right);
        if (IsNumericValue(left) && IsNumericValue(right)) return AsFloat64(left) == AsFloat64(right);
        // Ciclo 10 (W, W-11): el `==` no estricto entre una función y otra
        // cosa es `false` vía StrictEquals.
        if (left is TsFunctionValue || right is TsFunctionValue)
            return StrictEquals(left, right);
        return false;
    }

    private static TsValue ReadFieldValue(TsValue receiver, string fieldName) => receiver switch
    {
        TsObjectValue { Value.NativeDateTimestamp: not null } date =>
            date.Value.Fields.TryGetValue(fieldName, out var value) ? value : TsValue.Void,
        TsObjectValue objVal => objVal.Value.GetField(fieldName),
        TsArrayValue arrVal when fieldName == "length" => TsValue.FromInt32(arrVal.Value.Count),
        TsMapValue mapVal when fieldName == "size" => TsValue.FromFloat64(mapVal.Value.Count),
        TsSetValue setVal => NativeSet.ReadProperty(setVal, fieldName),
        TsRegexValue regex => NativeRegex.ReadProperty(regex, fieldName),
        TsUint8ArrayValue bytesVal when fieldName is "length" or "byteLength" => TsValue.FromFloat64(bytesVal.Length),
        TsStringValue strVal when fieldName == "length" => TsValue.FromInt32(strVal.Value.Length),
        _ => TsValue.Null,
    };

    internal static bool StrictEquals(TsValue left, TsValue right)
    {
        // Even the same wrapper must obey IEEE equality for NaN.
        if (ReferenceEquals(left, right))
            return left switch
            {
                TsFloat32Value f32 => !float.IsNaN(f32.Value),
                TsFloat64Value f64 => !double.IsNaN(f64.Value),
                _ => true
            };
        if (left is TsNull && right is TsNull) return true;
        if (left is TsVoid && right is TsVoid) return true;
        if (left is TsNull or TsVoid || right is TsNull or TsVoid) return false;
        if (left is TsStringValue ls && right is TsStringValue rs) return ls.Value == rs.Value;
        if (left is TsBoolValue lb && right is TsBoolValue rb) return lb.Value == rb.Value;
        if (left is TsObjectValue lo && right is TsObjectValue ro) return ReferenceEquals(lo.Value, ro.Value);
        if (left is TsArrayValue la && right is TsArrayValue ra) return ReferenceEquals(la.Value, ra.Value);
        if (left is TsUint8ArrayValue && right is TsUint8ArrayValue) return ReferenceEquals(left, right);
        if (IsBigIntValue(left) || IsBigIntValue(right)) return IsBigIntValue(left) && IsBigIntValue(right) && AsBigInteger(left) == AsBigInteger(right);
        if (IsNumericValue(left) && IsNumericValue(right)) return AsFloat64(left) == AsFloat64(right);
        // Ciclo 10 (W, W-11): dos valores-función son iguales si tienen el
        // mismo nombre y comparten la misma instancia de capturas. LoadFunc
        // pasa captured: null -> el singleton Array.Empty compartido, así
        // que dos cargas del mismo nombre son iguales; MakeClosure pasa un
        // array nuevo por evaluación, así que dos lambdas distintas no lo son.
        if (left is TsFunctionValue lf && right is TsFunctionValue rf)
            return lf.FunctionName == rf.FunctionName && ReferenceEquals(lf.Captured, rf.Captured);
        return false;
    }

    private static bool IsNumericValue(TsValue value) =>
        value is TsInt32Value or TsFloat32Value or TsFloat64Value or TsDecimalValue;

    private static bool IsBigIntValue(TsValue value) =>
        value is TsInt64Value or TsUInt64Value or TsBigIntValue;

    private void PopulateMap(TsMap map, TsValue[] args, BytecodeModule module, ExecutionContext ctx, CallFrame caller)
    {
        if (args.Length == 0 || args[0] is TsNull or TsVoid)
            return;

        void AddEntry(TsValue value, TsGeneratorValue? source = null)
        {
            switch (value)
            {
                case TsArrayValue pair:
                    map.Set(pair.Value.Get(0), pair.Value.Get(1));
                    return;
                case TsUint8ArrayValue bytes:
                    map.Set(Uint8ArrayOperations.ReadElement(bytes, TsValue.FromInt32(0)),
                        Uint8ArrayOperations.ReadElement(bytes, TsValue.FromInt32(1)));
                    return;
                case TsObjectValue obj:
                    var fields = obj.Value.Fields;
                    map.Set(fields.TryGetValue("0", out var key) ? key : TsValue.Void,
                        fields.TryGetValue("1", out var item) ? item : TsValue.Void);
                    return;
                case TsMapValue or TsSetValue or TsRegexValue or TsGeneratorValue or TsPromiseValue or TsFunctionValue:
                    // Native reference values have no indexed fields on this runtime.
                    map.Set(TsValue.Void, TsValue.Void);
                    return;
                default:
                    // Close only an invalid yielded entry, never mask a generator's
                    // own throw or resume it to fetch any later entries.
                    if (source != null)
                        CompleteGenerator(source, TsValue.Void);
                    throw NativeMap.TypeError("Map constructor entry must be an object");
            }
        }

        switch (args[0])
        {
            case TsArrayValue array:
                for (int i = 0; i < array.Value.Count; i++)
                    AddEntry(array.Value.Get(i));
                return;
            case TsMapValue other:
                foreach (var entry in other.Value.Entries)
                    map.Set(entry.Key, entry.Value);
                return;
            case TsSetValue set:
                foreach (var entry in set.Value.Entries)
                    AddEntry(entry);
                return;
            case TsStringValue text:
                if (text.Value.Length > 0)
                    AddEntry(TsValue.FromString(text.Value[..1]));
                return;
            case TsUint8ArrayValue bytes:
                if (bytes.Length > 0)
                    AddEntry(TsValue.FromInt32(bytes.Get(0)));
                return;
            case TsGeneratorValue generator:
                while (true)
                {
                    var yielded = (TsObjectValue)ResumeGenerator(generator, module, ctx, caller, null, isThrow: false);
                    if (yielded.Value.GetField("done") is TsBoolValue { Value: true })
                        return;
                    AddEntry(yielded.Value.GetField("value"), generator);
                }
            default:
                throw NativeMap.TypeError("Map constructor requires an iterable");
        }
    }

    private void PopulateSet(TsSet set, TsValue[] args, BytecodeModule module, ExecutionContext ctx, CallFrame caller)
    {
        if (args.Length == 0)
            return;

        switch (args[0])
        {
            case TsNull or TsVoid:
                return;
            case TsArrayValue array:
                for (int i = 0; i < array.Value.Count; i++)
                    set.Add(array.Value.Get(i));
                return;
            case TsStringValue text:
                var values = MaterializeIterableValues(text);
                for (int i = 0; i < values.Count; i++)
                    set.Add(values.Get(i));
                return;
            case TsSetValue source:
                foreach (var value in source.Value.Entries)
                    set.Add(value);
                return;
            case TsUint8ArrayValue bytes:
                for (int i = 0; i < bytes.Length; i++)
                    set.Add(TsValue.FromInt32(bytes.Get(i)));
                return;
            case TsMapValue map:
                foreach (var entry in map.Value.Entries)
                {
                    var pair = new TsArray(2);
                    pair.Add(entry.Key);
                    pair.Add(entry.Value);
                    set.Add(new TsArrayValue(pair));
                }
                return;
            case TsGeneratorValue generator:
                while (true)
                {
                    // Charge every attempt, including legacy resumes that bypass
                    // ExecuteFrameCore and the terminal done:true attempt.
                    ctx.IncrementInstruction();
                    ctx.CheckRecursionDepth();
                    ctx.CheckMemory();
                    ctx.CheckCancellation();
                    var step = (TsObjectValue)ResumeGenerator(generator, module, ctx, caller, null, isThrow: false);
                    if (step.Value.GetField("done") is TsBoolValue { Value: true })
                        return;
                    set.Add(step.Value.GetField("value"));
                }
            default:
                throw NativeSet.TypeError("Set constructor requires an iterable");
        }
    }

    private static bool UsesBigIntSemantics(TsValue left, TsValue right) =>
        left is TsBigIntValue || right is TsBigIntValue;

    private static BigInteger AsBigInteger(TsValue value) => value switch
    {
        TsInt32Value v => v.Value,
        TsInt64Value v => v.Value,
        TsUInt64Value v => v.Value,
        TsBigIntValue v => v.Value,
        TsFloat32Value v => new BigInteger(v.Value),
        TsFloat64Value v => new BigInteger(v.Value),
        TsDecimalValue v => new BigInteger(v.Value),
        TsBoolValue v => v.Value ? BigInteger.One : BigInteger.Zero,
        _ => BigInteger.Zero
    };

    private static string ToJsString(TsValue value) => value switch
    {
        TsVoid => "undefined",
        TsNull => "null",
        TsBoolValue v => v.Value ? "true" : "false",
        TsStringValue v => v.Value,
        TsBigIntValue v => v.Value.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static bool IsPrivateRuntimeField(string name) =>
        name.StartsWith("\uE000private:", StringComparison.Ordinal);

    private static CallFrame CreateCallFrame(BytecodeFunction function, CallFrame? caller,
        IReadOnlyList<TsValue> args, ExecutionContext ctx, IReadOnlyList<TsValue>? captured = null,
        TsValue? invocationReceiver = null)
    {
        // Existing qualified script methods receive the phantom arg 0. A
        // Map callback can override only the independent invocation receiver.
        var frame = new CallFrame(function, caller)
        {
            InvocationReceiver = invocationReceiver ??
                (args.Count > 0 && function.ParameterCount > 0 && function.Name.Contains("::", StringComparison.Ordinal)
                    ? args[0] : TsValue.Void),
        };
        int restIndex = function.RestParameterIndex;

        if (restIndex >= 0)
        {
            int fixedCount = Math.Min(restIndex, function.ParameterCount);
            for (int i = 0; i < fixedCount && i < args.Count && i < frame.Locals.Length; i++)
                frame.Locals[i] = args[i];

            if (restIndex < frame.Locals.Length)
            {
                int restCount = Math.Max(0, args.Count - restIndex);
                var rest = ctx.Heap.AllocateArray(restCount);
                for (int i = restIndex; i < args.Count; i++)
                    rest.Add(args[i]);
                frame.Locals[restIndex] = new TsArrayValue(rest);
            }
        }
        else
        {
            int count = Math.Min(args.Count, Math.Min(function.ParameterCount, frame.Locals.Length));
            for (int i = 0; i < count; i++)
                frame.Locals[i] = args[i];
        }

        if (captured != null)
        {
            // Closure environment boxes install right after the declared parameters.
            for (int j = 0; j < captured.Count; j++)
            {
                int slot = function.ParameterCount + j;
                if (slot < frame.Locals.Length)
                    frame.Locals[slot] = captured[j];
            }
        }

        return frame;
    }

    // Invokes any callable value (module function, closure, builtin, host
    // function). Shared by CALL_DYNAMIC and the higher-order array intrinsics.
    internal TsValue InvokeCallable(TsValue calleeValue, TsValue[] args, BytecodeModule module,
        ExecutionContext ctx, CallFrame caller, TsValue? invocationReceiver = null)
    {
        if (calleeValue is not TsFunctionValue fnValue)
            throw new InvalidOperationException($"Value '{calleeValue}' is not callable");

        if (NativeRegex.IsIntrinsicReference(fnValue))
            return NativeRegex.TestWithReceiver(invocationReceiver, args);

        // Ciclo 10 (W, W-10): una clase no se invoca sin `new`. `NewObject`
        // no pasa por aquí (construye el frame del ctor directamente), así
        // que ningún camino legítimo trae un "::.ctor" a esta función.
        if (fnValue.FunctionName.EndsWith("::.ctor", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Class constructor '{fnValue.FunctionName[..^7]}' cannot be invoked without 'new'");

        // Native Set/Map use a separate receiver channel. A positional value
        // may never stand in for a receiver omitted by a detached invocation.
        if (NativeSet.IsMethod(fnValue.FunctionName))
        {
            var nativeArgs = new TsValue[args.Length + 1];
            nativeArgs[0] = invocationReceiver ?? TsValue.Void;
            Array.Copy(args, 0, nativeArgs, 1, args.Length);
            return InvokeNativeSet(fnValue.FunctionName, nativeArgs, module, ctx, caller);
        }

        if (NativeMap.IsMethod(fnValue.FunctionName))
        {
            var nativeArgs = new TsValue[args.Length + 1];
            nativeArgs[0] = invocationReceiver ?? TsValue.Void;
            Array.Copy(args, 0, nativeArgs, 1, args.Length);
            return InvokeNativeMap(fnValue.FunctionName, nativeArgs, module, ctx, caller);
        }

        if (Builtins.TryGet(fnValue.FunctionName, out var builtin))
            return builtin(args);

        if (module.FunctionIndex.TryGetValue(fnValue.FunctionName, out int fnIdx))
        {
            var calleeFunc = module.Functions[fnIdx];
            _profile.RecordCall(module.Name, calleeFunc.Name);
            // Ciclo 10 (W, W-10): un método llamado por valor no trae
            // receptor; el parámetro fantasma `this` del slot 0 recibe
            // `Void` (decisión del ciclo 9: `this` en un estático es `Void`).
            // Solo los miembros de clase ("C::m") tienen ese parámetro: las
            // funciones top-level nunca contienen "::" y las lambdas se llaman
            // "$lambda_...". Una función top-level SÍ puede declarar un
            // parámetro llamado `this`, por eso la mitad "::" del
            // discriminador es imprescindible.
            // Desviación del plan (§2.5): el fragmento del plan leía
            // `calleeFunc.Parameters[0].Name == "this"`, pero BytecodeFunction
            // no tiene lista `Parameters` (solo `ParameterCount`): no
            // compila. La corrección mínima es `ParameterCount > 0`:
            // GenerateMethodFunction/GenerateAccessorFunction ponen el
            // fantasma `this` en el slot 0 de forma incondicional
            // (IRGenerator.cs:503, :613), así que toda función "::" tiene
            // `this` en el slot 0 y ParameterCount >= 1; los ctores ya
            // quedaron fuera por el guarda de arriba.
            var callArgs = calleeFunc.Name.Contains("::", StringComparison.Ordinal)
                && calleeFunc.ParameterCount > 0
                ? new[] { TsValue.Void }.Concat(args).ToArray()
                : args;
            var newFrame = CreateCallFrame(calleeFunc, caller, callArgs, ctx, fnValue.Captured, invocationReceiver);
            if (calleeFunc.IsGenerator)
                return new TsGeneratorValue(module, newFrame);

            return ExecuteManagedFrame(calleeFunc, newFrame, module, ctx);
        }

        if (TryGetHostFunction(fnValue.FunctionName, out var host))
            return host(fnValue.FunctionName, args) ?? TsValue.Null;

        throw new InvalidOperationException($"Function '{fnValue.FunctionName}' not found");
    }

    private bool TryInvokeGeneratorMethod(string resolvedName, TsValue[] args, BytecodeModule module,
        ExecutionContext ctx, CallFrame caller, out TsValue result)
    {
        result = TsValue.Null;
        if (!resolvedName.StartsWith("Generator::", StringComparison.Ordinal))
            return false;
        if (args.Length < 1 || args[0] is not TsGeneratorValue generator)
            throw new InvalidOperationException($"{resolvedName} requires a generator receiver");

        result = resolvedName switch
        {
            "Generator::next" => ResumeGenerator(generator, module, ctx, caller, null, isThrow: false),
            "Generator::return" => CompleteGenerator(generator, args.Length > 1 ? args[1] : TsValue.Void),
            "Generator::throw" => ResumeGenerator(generator, module, ctx, caller,
                args.Length > 1 ? args[1] : TsValue.Void, isThrow: true),
            _ => throw new InvalidOperationException($"Generator method '{resolvedName}' is not implemented")
        };
        return true;
    }

    private TsValue ResumeGenerator(TsGeneratorValue generator, BytecodeModule module, ExecutionContext ctx,
        CallFrame caller, TsValue? abruptValue, bool isThrow)
    {
        if (generator.Done)
            return isThrow
                ? throw new TsThrownException(abruptValue ?? TsValue.Void)
                : CreateIteratorResult(TsValue.Void, done: true);
        if (generator.TryNextLegacy(out var legacyValue, out var legacyDone))
            return CreateIteratorResult(legacyValue, legacyDone);
        if (generator.Running)
            throw new InvalidOperationException("Generator is already running");
        if (generator.Frame == null || generator.Module == null)
            throw new InvalidOperationException("Generator has no suspended frame");

        var resumeModule = generator.Module;
        var frame = generator.Frame;
        frame.Caller = caller;

        if (isThrow && !InjectGeneratorThrow(generator, abruptValue ?? TsValue.Void))
            throw new TsThrownException(abruptValue ?? TsValue.Void);

        generator.Running = true;
        PushManagedFrame(ctx, frame);
        try
        {
            var returned = ExecuteFrame(frame, resumeModule, ctx);
            generator.Done = true;
            return CreateIteratorResult(FinishFunctionResult(frame.Function, returned), done: true);
        }
        catch (TsGeneratorYieldException yielded)
        {
            return CreateIteratorResult(yielded.Value, done: false);
        }
        catch (TsThrownException)
        {
            generator.Done = true;
            throw;
        }
        finally
        {
            PopManagedFrame(ctx, frame);
            generator.Running = false;
        }
    }

    private static bool InjectGeneratorThrow(TsGeneratorValue generator, TsValue value)
    {
        var frame = generator.Frame;
        if (frame?.TryHandlers is not { Count: > 0 })
        {
            generator.Done = true;
            return false;
        }

        var handler = frame.TryHandlers.Pop();
        frame.StackPointer = handler.StackDepth;
        frame.Push(value);
        frame.InstructionPointer = handler.HandlerOffset;
        return true;
    }

    private static TsValue CompleteGenerator(TsGeneratorValue generator, TsValue value)
    {
        generator.Done = true;
        return CreateIteratorResult(value, done: true);
    }

    private static TsValue CreateIteratorResult(TsValue value, bool done)
    {
        var result = new TsObject("Object");
        result.SetField("value", value);
        result.SetField("done", new TsBoolValue(done));
        return new TsObjectValue(result);
    }

    private static string ResolveVirtualCalleeName(string calleeName, TsValue[] args)
    {
        if (calleeName.Contains("::", StringComparison.Ordinal) || args.Length == 0)
            return calleeName;

        return args[0] switch
        {
            TsObjectValue obj => $"{obj.Value.TypeName}::{calleeName}",
            TsArrayValue => $"Array::{calleeName}",
            TsMapValue => $"Map::{calleeName}",
            TsSetValue => $"Set::{calleeName}",
            TsUint8ArrayValue => $"Uint8Array::{calleeName}",
            TsRegexValue => $"RegExp::{calleeName}",
            TsGeneratorValue => $"Generator::{calleeName}",
            TsStringValue => $"String::{calleeName}",
            _ => calleeName
        };
    }

    private static string DescribeReceiver(TsValue[] args)
    {
        if (args.Length == 0)
            return "<none>";

        return DescribeValue(args[0]);
    }

    private static string ToPropertyKey(TsValue value) => value switch
    {
        TsStringValue text => text.Value,
        TsInt32Value number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsInt64Value number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsUInt64Value number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsFloat32Value number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsFloat64Value number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsBoolValue boolean => boolean.Value ? "true" : "false",
        TsNull => "null",
        TsVoid => "undefined",
        _ => value.ToString() ?? string.Empty
    };

    private static string DescribeValue(TsValue value)
    {
        return value switch
        {
            TsObjectValue obj => $"object:{obj.Value.TypeName}",
            TsArrayValue => "Array",
            TsMapValue => "Map",
            TsSetValue => "Set",
            TsUint8ArrayValue => "Uint8Array",
            TsFunctionValue fn => $"function:{fn.FunctionName}",
            TsRegexValue => "RegExp",
            TsGeneratorValue => "Generator",
            TsStringValue => "string",
            TsBoolValue => "boolean",
            TsInt32Value => "number:int32",
            TsInt64Value => "bigint:int64",
            TsUInt64Value => "bigint:uint64",
            TsBigIntValue => "bigint",
            TsNull => "null",
            TsVoid => "void",
            _ => value.GetType().Name
        };
    }

    private static TsRegexValue CreateRegex(string pattern, string flags)
    {
        const int maxPatternLength = 1024;
        const int maxFlagLength = 8;
        if (pattern.Length > maxPatternLength)
            throw new InvalidOperationException("Regular expression pattern exceeds the runtime limit");
        if (flags.Length > maxFlagLength)
            throw new InvalidOperationException("Regular expression flags exceed the runtime limit");

        var seen = new HashSet<char>();
        foreach (var flag in flags)
        {
            if (flag is not ('g' or 'i' or 'm' or 's' or 'u' or 'y'))
                throw new InvalidOperationException($"Unsupported regular expression flag '{flag}'");
            if (!seen.Add(flag))
                throw new InvalidOperationException($"Duplicate regular expression flag '{flag}'");
        }

        try
        {
            return new TsRegexValue(pattern, flags);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid regular expression literal: {ex.Message}", ex);
        }
    }

    private static void AppendSpread(TsArray target, TsValue source)
    {
        var values = MaterializeIterableValues(source);
        for (int i = 0; i < values.Count; i++)
            target.Add(values.Get(i));
    }

    private static TsArray MaterializeIterableValues(TsValue source)
    {
        var result = new TsArray();
        switch (source)
        {
            case TsArrayValue arrayValue:
                for (int i = 0; i < arrayValue.Value.Count; i++)
                    result.Add(arrayValue.Value.Get(i));
                return result;
            case TsStringValue stringValue:
                for (int i = 0; i < stringValue.Value.Length; i++)
                {
                    char unit = stringValue.Value[i];
                    if (char.IsHighSurrogate(unit) && i + 1 < stringValue.Value.Length &&
                        char.IsLowSurrogate(stringValue.Value[i + 1]))
                    {
                        result.Add(TsValue.FromString(stringValue.Value.Substring(i, 2)));
                        i++;
                    }
                    else
                    {
                        result.Add(TsValue.FromString(unit.ToString()));
                    }
                }
                return result;
            case TsUint8ArrayValue bytesValue:
                for (int i = 0; i < bytesValue.Length; i++)
                    result.Add(TsValue.FromInt32(bytesValue.Get(i)));
                return result;
            case TsSetValue setValue:
                foreach (var value in setValue.Value.Entries)
                    result.Add(value);
                return result;
            case TsMapValue mapValue:
                foreach (var entry in mapValue.Value.Entries)
                {
                    var pair = new TsArray(2);
                    pair.Add(entry.Key);
                    pair.Add(entry.Value);
                    result.Add(new TsArrayValue(pair));
                }
                return result;
            case TsObjectValue objectValue when objectValue.Value.GetField("length") is not TsNull and not TsVoid:
            {
                int length = AsInt32(objectValue.Value.GetField("length"));
                for (int i = 0; i < length; i++)
                    result.Add(objectValue.Value.GetField(i.ToString(CultureInfo.InvariantCulture)));
                return result;
            }
            case TsNull or TsVoid:
                throw new InvalidOperationException("Cannot spread null or undefined");
            default:
                throw new InvalidOperationException($"Value '{DescribeValue(source)}' is not iterable");
        }
    }

    private static TsValue[] ToArgumentArray(TsValue argumentArray)
    {
        if (argumentArray is not TsArrayValue arrayValue)
            throw new InvalidOperationException("Spread call requires an argument array");

        var args = new TsValue[arrayValue.Value.Count];
        for (int i = 0; i < args.Length; i++)
            args[i] = arrayValue.Value.Get(i);
        return args;
    }

    private static bool TryGetVirtualFieldCallable(
        string calleeName,
        TsValue[] args,
        out TsValue callable,
        out TsValue[] callArgs)
    {
        callable = TsValue.Null;
        callArgs = Array.Empty<TsValue>();

        if (calleeName.Contains("::", StringComparison.Ordinal) || args.Length == 0)
            return false;
        if (args[0] is not TsObjectValue obj)
            return false;

        var field = obj.Value.GetField(calleeName);
        if (field is not TsFunctionValue)
            return false;

        callable = field;
        callArgs = args.Skip(1).ToArray();
        return true;
    }

    private static TsValue FinishFunctionResult(BytecodeFunction function, TsValue? result)
    {
        var value = result ?? TsValue.Void;
        if (!function.IsAsync)
            return value;
        return value is TsPromiseValue promise
            ? promise
            : TsPromiseValue.Resolved(value);
    }

    private static TsValue AwaitValue(TsValue value, ExecutionContext ctx)
    {
        return value is TsPromiseValue promise
            ? promise.Await(ctx.CancellationToken)
            : value;
    }

    private bool TryResolveCachedCallee(BytecodeFunction caller, int instructionOffset, string calleeName,
        BytecodeModule module, out int calleeIndex)
    {
        var cache = _callInlineCaches.GetValue(caller, static _ => new ConcurrentDictionary<int, int>());
        if (cache.TryGetValue(instructionOffset, out calleeIndex))
            return true;
        if (!module.FunctionIndex.TryGetValue(calleeName, out calleeIndex))
            return false;
        cache.TryAdd(instructionOffset, calleeIndex);
        return true;
    }

    private bool TryGetHostFunction(string name, out HostFunctionDelegate function)
    {
        if (_hostFunctions.TryGetValue(name, out function!))
            return true;

        var matches = _hostFunctions
            .Where(pair => pair.Key.EndsWith($".{name}", StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .Take(2)
            .ToArray();
        if (matches.Length == 1)
        {
            function = matches[0];
            return true;
        }

        function = null!;
        return false;
    }

    private static int ReadInt32(byte[] bytecode, ref int ip)
    {
        int value = BitConverter.ToInt32(bytecode, ip);
        ip += 4;
        return value;
    }

    private static long ReadInt64(byte[] bytecode, ref int ip)
    {
        long value = BitConverter.ToInt64(bytecode, ip);
        ip += 8;
        return value;
    }

    private static ulong ReadUInt64(byte[] bytecode, ref int ip)
    {
        ulong value = BitConverter.ToUInt64(bytecode, ip);
        ip += 8;
        return value;
    }

    private static float ReadFloat(byte[] bytecode, ref int ip)
    {
        float value = BitConverter.ToSingle(bytecode, ip);
        ip += 4;
        return value;
    }

    private static double ReadDouble(byte[] bytecode, ref int ip)
    {
        double value = BitConverter.ToDouble(bytecode, ip);
        ip += 8;
        return value;
    }

    private static decimal ReadDecimal(byte[] bytecode, ref int ip)
    {
        int lo = BitConverter.ToInt32(bytecode, ip);
        int mid = BitConverter.ToInt32(bytecode, ip + 4);
        int hi = BitConverter.ToInt32(bytecode, ip + 8);
        int flags = BitConverter.ToInt32(bytecode, ip + 12);
        ip += 16;
        return new decimal(new[] { lo, mid, hi, flags });
    }
}
