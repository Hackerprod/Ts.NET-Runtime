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
        if (CallStack.Count > Limits.MaximumRecursionDepth)
            throw new InvalidOperationException("Maximum recursion depth exceeded");
    }

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

public sealed class Interpreter
{
    private readonly TsHeap _heap;
    private readonly VMRuntimeLimits _limits;
    private readonly Dictionary<string, HostFunctionDelegate> _hostFunctions = new();
    private readonly ExecutionProfile _profile = new();
    private readonly ConditionalWeakTable<BytecodeFunction, ConcurrentDictionary<int, int>> _callInlineCaches = new();

    public TsHeap Heap => _heap;
    public ExecutionProfile Profile => _profile;

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

            var func = module.Functions[funcIdx];
            _profile.RecordCall(module.Name, func.Name);
            var frame = new CallFrame(func);

            if (args != null)
            {
                for (int i = 0; i < Math.Min(args.Length, func.ParameterCount); i++)
                    frame.Locals[i] = args[i];
            }

            ctx.CallStack.Push(frame);
            try
            {
                return FinishFunctionResult(func, ExecuteFrame(frame, module, ctx));
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

                case Opcodes.LoadField: // LOAD_FIELD
                {
                    int fieldIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var fieldName = strings[fieldIdx];
                    var obj = frame.Pop();
                    if (obj is TsObjectValue objVal)
                        frame.Push(objVal.Value.GetField(fieldName));
                    else if (obj is TsArrayValue arrVal && fieldName == "length")
                        frame.Push(TsValue.FromInt32(arrVal.Value.Count));
                    else if (obj is TsMapValue mapVal && fieldName == "size")
                        frame.Push(TsValue.FromFloat64(mapVal.Value.Count));
                    else if (obj is TsUint8ArrayValue bytesVal && fieldName == "length")
                        frame.Push(TsValue.FromFloat64(bytesVal.Length));
                    else if (obj is TsStringValue strVal && fieldName == "length")
                        frame.Push(TsValue.FromInt32(strVal.Value.Length));
                    else
                        frame.Push(TsValue.Null);
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
                        TsNull or TsVoid => "undefined",
                        _ => "object"
                    }));
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

                    if (HigherOrderIntrinsics.Contains(calleeName))
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
                        var newFrame = new CallFrame(calleeFunc, frame);

                        for (int i = argCount - 1; i >= 0; i--)
                            newFrame.Locals[i] = frame.Pop();

                        ctx.CallStack.Push(newFrame);
                        TsValue? result;
                        try
                        {
                            result = ExecuteFrame(newFrame, module, ctx);
                        }
                        finally
                        {
                            ctx.CallStack.Pop();
                        }

                        frame.Push(FinishFunctionResult(calleeFunc, result));
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
                    frame.Push(new TsFunctionValue(strings[nameIdx]));
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

                case Opcodes.CallVirt: // CALL_VIRT
                {
                    int funcIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    int argCount = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var calleeName = strings[funcIdx];
                    var callArgs = new TsValue[argCount];
                    for (int i = argCount - 1; i >= 0; i--)
                        callArgs[i] = frame.Pop();
                    var resolvedName = ResolveVirtualCalleeName(calleeName, callArgs);

                    if (TryGetVirtualFieldCallable(calleeName, callArgs, out var fieldCallable, out var fieldArgs))
                    {
                        frame.Push(InvokeCallable(fieldCallable, fieldArgs, module, ctx, frame));
                    }
                    else if (TryResolveCachedCallee(frame.Function, instructionOffset, resolvedName, module, out int calleeIdx)
                        || (resolvedName == calleeName && TryResolveCachedCallee(frame.Function, instructionOffset, calleeName, module, out calleeIdx)))
                    {
                        var calleeFunc = module.Functions[calleeIdx];
                        _profile.RecordCall(module.Name, calleeFunc.Name);
                        var newFrame = new CallFrame(calleeFunc, frame);
                        for (int i = 0; i < argCount; i++)
                            newFrame.Locals[i] = callArgs[i];
                        ctx.CallStack.Push(newFrame);
                        TsValue? result;
                        try
                        {
                            result = ExecuteFrame(newFrame, module, ctx);
                        }
                        finally
                        {
                            ctx.CallStack.Pop();
                        }
                        frame.Push(FinishFunctionResult(calleeFunc, result));
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
                        frame.Push(CreateUint8Array(args));
                        break;
                    }

                    if (typeName == "Map")
                    {
                        var args = new TsValue[argCount];
                        for (int i = argCount - 1; i >= 0; i--)
                            args[i] = frame.Pop();
                        var map = ctx.Heap.AllocateMap();
                        PopulateMap(map, args);
                        frame.Push(new TsMapValue(map));
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
                        var ctorFrame = new CallFrame(ctorFunc, frame);

                        ctorFrame.Locals[0] = TsValue.FromObject(obj);
                        for (int i = argCount; i >= 1; i--)
                            ctorFrame.Locals[i] = frame.Pop();

                        ctx.CallStack.Push(ctorFrame);
                        try
                        {
                            ExecuteFrame(ctorFrame, module, ctx);
                        }
                        finally
                        {
                            ctx.CallStack.Pop();
                        }
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

                case Opcodes.LoadElement: // LOAD_ELEMENT
                {
                    var index = frame.Pop();
                    var target = frame.Pop();
                    if (target is TsArrayValue arrVal)
                        frame.Push(arrVal.Value.Get(AsInt32(index)));
                    else if (target is TsUint8ArrayValue bytesVal)
                    {
                        int byteIdx = AsInt32(index);
                        frame.Push(byteIdx >= 0 && byteIdx < bytesVal.Length
                            ? TsValue.FromFloat64(bytesVal.Get(byteIdx))
                            : TsValue.Null);
                    }
                    else if (target is TsStringValue strTarget)
                    {
                        int chIdx = AsInt32(index);
                        frame.Push(chIdx >= 0 && chIdx < strTarget.Value.Length
                            ? TsValue.FromString(strTarget.Value[chIdx].ToString())
                            : TsValue.Null);
                    }
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
                        bytesVal.Set(AsInt32(index), value);
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
        "Array::some", "Array::every", "Array::find", "Array::findIndex",
        "Array::sort", "Array::flatMap", "Array::from"
    };

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
            var mapFn = args.Length > 1 ? args[1] : null;
            var result = new TsArray();
            void AddMapped(TsValue value, int index)
            {
                result.Add(mapFn != null ? Invoke(mapFn, value, TsValue.FromInt32(index)) : value);
            }
            switch (source)
            {
                case TsArrayValue av:
                    for (int i = 0; i < av.Value.Count; i++) AddMapped(av.Value.Get(i), i);
                    break;
                case TsUint8ArrayValue bv:
                    for (int i = 0; i < bv.Length; i++) AddMapped(TsValue.FromFloat64(bv.Get(i)), i);
                    break;
                case TsStringValue sv:
                    for (int i = 0; i < sv.Value.Length; i++) AddMapped(TsValue.FromString(sv.Value[i].ToString()), i);
                    break;
                case TsObjectValue ov when ov.Value.GetField("length") is not TsNull:
                    int length = AsInt32(ov.Value.GetField("length"));
                    for (int i = 0; i < length; i++) AddMapped(TsValue.Null, i);
                    break;
            }
            return new TsArrayValue(result);
        }

        if (args.Length == 0 || args[0] is not TsArrayValue receiver)
            throw new InvalidOperationException($"'{name}' requires an array receiver");
        var arr = receiver.Value;
        var callback = args.Length > 1 ? args[1] : null;

        switch (name)
        {
            case "Array::map":
            {
                var mapped = new TsArray(arr.Count);
                for (int i = 0; i < arr.Count; i++)
                    mapped.Add(Invoke(callback!, arr.Get(i), TsValue.FromInt32(i)));
                return new TsArrayValue(mapped);
            }
            case "Array::flatMap":
            {
                var flat = new TsArray(arr.Count);
                for (int i = 0; i < arr.Count; i++)
                {
                    var produced = Invoke(callback!, arr.Get(i), TsValue.FromInt32(i));
                    if (produced is TsArrayValue nested)
                        for (int j = 0; j < nested.Value.Count; j++) flat.Add(nested.Value.Get(j));
                    else
                        flat.Add(produced);
                }
                return new TsArrayValue(flat);
            }
            case "Array::filter":
            {
                var filtered = new TsArray();
                for (int i = 0; i < arr.Count; i++)
                {
                    var element = arr.Get(i);
                    if (AsBool(Invoke(callback!, element, TsValue.FromInt32(i))))
                        filtered.Add(element);
                }
                return new TsArrayValue(filtered);
            }
            case "Array::forEach":
            {
                for (int i = 0; i < arr.Count; i++)
                    Invoke(callback!, arr.Get(i), TsValue.FromInt32(i));
                return TsValue.Null;
            }
            case "Array::reduce":
            {
                int start = 0;
                TsValue accumulator;
                if (args.Length > 2)
                {
                    accumulator = args[2];
                }
                else
                {
                    if (arr.Count == 0)
                        throw new InvalidOperationException("Reduce of empty array with no initial value");
                    accumulator = arr.Get(0);
                    start = 1;
                }
                for (int i = start; i < arr.Count; i++)
                    accumulator = Invoke(callback!, accumulator, arr.Get(i), TsValue.FromInt32(i));
                return accumulator;
            }
            case "Array::some":
            {
                for (int i = 0; i < arr.Count; i++)
                    if (AsBool(Invoke(callback!, arr.Get(i), TsValue.FromInt32(i))))
                        return new TsBoolValue(true);
                return new TsBoolValue(false);
            }
            case "Array::every":
            {
                for (int i = 0; i < arr.Count; i++)
                    if (!AsBool(Invoke(callback!, arr.Get(i), TsValue.FromInt32(i))))
                        return new TsBoolValue(false);
                return new TsBoolValue(true);
            }
            case "Array::find":
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    var element = arr.Get(i);
                    if (AsBool(Invoke(callback!, element, TsValue.FromInt32(i))))
                        return element;
                }
                return TsValue.Null;
            }
            case "Array::findIndex":
            {
                for (int i = 0; i < arr.Count; i++)
                    if (AsBool(Invoke(callback!, arr.Get(i), TsValue.FromInt32(i))))
                        return TsValue.FromInt32(i);
                return TsValue.FromInt32(-1);
            }
            case "Array::sort":
            {
                var items = new TsValue[arr.Count];
                for (int i = 0; i < arr.Count; i++) items[i] = arr.Get(i);
                Comparison<TsValue> comparison = callback != null
                    ? (a, b) => Math.Sign(AsFloat64(Invoke(callback, a, b)))
                    : DefaultSortComparison;
                Array.Sort(items, comparison);
                for (int i = 0; i < items.Length; i++) arr.Set(i, items[i]);
                return receiver;
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
        if (left is TsUint8ArrayValue lbv && right is TsUint8ArrayValue rbv) return ReferenceEquals(lbv.Value, rbv.Value);
        if (IsBigIntValue(left) || IsBigIntValue(right)) return AsBigInteger(left) == AsBigInteger(right);
        if (IsNumericValue(left) && IsNumericValue(right)) return AsFloat64(left) == AsFloat64(right);
        return false;
    }

    private static bool StrictEquals(TsValue left, TsValue right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is TsNull && right is TsNull) return true;
        if (left is TsVoid && right is TsVoid) return true;
        if (left is TsNull or TsVoid || right is TsNull or TsVoid) return false;
        if (left is TsStringValue ls && right is TsStringValue rs) return ls.Value == rs.Value;
        if (left is TsBoolValue lb && right is TsBoolValue rb) return lb.Value == rb.Value;
        if (left is TsObjectValue lo && right is TsObjectValue ro) return ReferenceEquals(lo.Value, ro.Value);
        if (left is TsArrayValue la && right is TsArrayValue ra) return ReferenceEquals(la.Value, ra.Value);
        if (left is TsUint8ArrayValue lbv && right is TsUint8ArrayValue rbv) return ReferenceEquals(lbv.Value, rbv.Value);
        if (IsBigIntValue(left) || IsBigIntValue(right)) return IsBigIntValue(left) && IsBigIntValue(right) && AsBigInteger(left) == AsBigInteger(right);
        if (IsNumericValue(left) && IsNumericValue(right)) return AsFloat64(left) == AsFloat64(right);
        return false;
    }

    private static bool IsNumericValue(TsValue value) =>
        value is TsInt32Value or TsFloat32Value or TsFloat64Value or TsDecimalValue;

    private static bool IsBigIntValue(TsValue value) =>
        value is TsInt64Value or TsUInt64Value or TsBigIntValue;

    private static TsUint8ArrayValue CreateUint8Array(TsValue[] args)
    {
        if (args.Length == 0)
            return new TsUint8ArrayValue(Array.Empty<byte>());

        var source = args[0];
        switch (source)
        {
            case TsUint8ArrayValue bytes:
            {
                var copy = new byte[bytes.Length];
                Array.Copy(bytes.Value, copy, copy.Length);
                return new TsUint8ArrayValue(copy);
            }
            case TsArrayValue array:
            {
                var bytes = new byte[array.Value.Count];
                for (int i = 0; i < bytes.Length; i++)
                    bytes[i] = ToByte(array.Value.Get(i));
                return new TsUint8ArrayValue(bytes);
            }
            case TsInt32Value or TsInt64Value or TsUInt64Value or TsFloat32Value or TsFloat64Value or TsDecimalValue:
                return new TsUint8ArrayValue(new byte[Math.Max(AsInt32(source), 0)]);
            default:
                return new TsUint8ArrayValue(Array.Empty<byte>());
        }
    }

    private static void PopulateMap(TsMap map, TsValue[] args)
    {
        if (args.Length == 0 || args[0] is TsNull or TsVoid)
            return;

        if (args[0] is not TsArrayValue entries)
            throw new InvalidOperationException("Map constructor expects an array of [key, value] entries");

        for (int i = 0; i < entries.Value.Count; i++)
        {
            if (entries.Value.Get(i) is not TsArrayValue pair || pair.Value.Count < 2)
                throw new InvalidOperationException("Map constructor entry must be a [key, value] array");

            map.Set(pair.Value.Get(0), pair.Value.Get(1));
        }
    }

    internal static byte ToByte(TsValue value) => value switch
    {
        TsInt32Value v => unchecked((byte)v.Value),
        TsInt64Value v => unchecked((byte)v.Value),
        TsUInt64Value v => unchecked((byte)v.Value),
        TsBigIntValue v => unchecked((byte)v.Value),
        TsFloat32Value v => unchecked((byte)v.Value),
        TsFloat64Value v => unchecked((byte)v.Value),
        TsDecimalValue v => unchecked((byte)v.Value),
        TsBoolValue v => v.Value ? (byte)1 : (byte)0,
        _ => 0
    };

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

    // Invokes any callable value (module function, closure, builtin, host
    // function). Shared by CALL_DYNAMIC and the higher-order array intrinsics.
    internal TsValue InvokeCallable(TsValue calleeValue, TsValue[] args, BytecodeModule module,
        ExecutionContext ctx, CallFrame caller)
    {
        if (calleeValue is not TsFunctionValue fnValue)
            throw new InvalidOperationException($"Value '{calleeValue}' is not callable");

        if (Builtins.TryGet(fnValue.FunctionName, out var builtin))
            return builtin(args);

        if (module.FunctionIndex.TryGetValue(fnValue.FunctionName, out int fnIdx))
        {
            var calleeFunc = module.Functions[fnIdx];
            _profile.RecordCall(module.Name, calleeFunc.Name);
            var newFrame = new CallFrame(calleeFunc, caller);
            for (int i = 0; i < args.Length && i < newFrame.Locals.Length; i++)
                newFrame.Locals[i] = args[i];
            // Closure environment boxes install right after the parameters.
            for (int j = 0; j < fnValue.Captured.Length; j++)
            {
                int slot = calleeFunc.ParameterCount + j;
                if (slot < newFrame.Locals.Length)
                    newFrame.Locals[slot] = fnValue.Captured[j];
            }
            ctx.CallStack.Push(newFrame);
            try
            {
                return FinishFunctionResult(calleeFunc, ExecuteFrame(newFrame, module, ctx));
            }
            finally
            {
                ctx.CallStack.Pop();
            }
        }

        if (TryGetHostFunction(fnValue.FunctionName, out var host))
            return host(fnValue.FunctionName, args) ?? TsValue.Null;

        throw new InvalidOperationException($"Function '{fnValue.FunctionName}' not found");
    }

    private static string ResolveVirtualCalleeName(string calleeName, TsValue[] args)
    {
        if (calleeName.Contains("::", StringComparison.Ordinal) || args.Length == 0)
            return calleeName;

        return args[0] switch
        {
            TsObjectValue obj => $"{obj.Value.TypeName}::{calleeName}",
            TsMapValue => $"Map::{calleeName}",
            _ => calleeName
        };
    }

    private static string DescribeReceiver(TsValue[] args)
    {
        if (args.Length == 0)
            return "<none>";

        return args[0] switch
        {
            TsObjectValue obj => $"object:{obj.Value.TypeName}",
            TsArrayValue => "Array",
            TsMapValue => "Map",
            TsUint8ArrayValue => "Uint8Array",
            TsFunctionValue fn => $"function:{fn.FunctionName}",
            TsStringValue => "string",
            TsBoolValue => "boolean",
            TsInt32Value => "number:int32",
            TsInt64Value => "bigint:int64",
            TsUInt64Value => "bigint:uint64",
            TsBigIntValue => "bigint",
            TsNull => "null",
            TsVoid => "void",
            _ => args[0].GetType().Name
        };
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
