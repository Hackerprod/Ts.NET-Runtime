using System.Collections.Concurrent;
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

    public ExecutionContext(VMRuntimeLimits limits, TsHeap heap)
    {
        Limits = limits;
        Heap = heap;
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
            throw new InvalidOperationException("Memory limit exceeded");
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
        return new ExecutionContext(_limits, _heap);
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
            return ExecuteFrame(frame, module, ctx);
        }
        finally
        {
            if (ownsContext)
                ctx.Dispose();
        }
    }

    private TsValue? ExecuteFrame(CallFrame frame, BytecodeModule module, ExecutionContext ctx)
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

                case Opcodes.LoadConstU64: // LOAD_CONST_U64
                    frame.Push(TsValue.FromUInt64(ReadUInt64(bytecode, ref frame.InstructionPointer)));
                    break;

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
                    frame.Push(TsValue.FromUInt64(AsUInt64(left) + AsUInt64(right)));
                    break;
                }
                case Opcodes.SubU64: // SUB_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromUInt64(AsUInt64(left) - AsUInt64(right)));
                    break;
                }
                case Opcodes.MulU64: // MUL_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromUInt64(AsUInt64(left) * AsUInt64(right)));
                    break;
                }
                case Opcodes.DivU64: // DIV_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    ulong r = AsUInt64(right);
                    if (r == 0) throw new InvalidOperationException("Division by zero");
                    frame.Push(TsValue.FromUInt64(AsUInt64(left) / r));
                    break;
                }
                case Opcodes.ModU64: // MOD_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    ulong r = AsUInt64(right);
                    if (r == 0) throw new InvalidOperationException("Division by zero");
                    frame.Push(TsValue.FromUInt64(AsUInt64(left) % r));
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
                    frame.Push(new TsBoolValue(AsUInt64(left) == AsUInt64(right)));
                    break;
                }
                case Opcodes.CmpNeU64: // CMP_NE_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsUInt64(left) != AsUInt64(right)));
                    break;
                }
                case Opcodes.CmpLtU64: // CMP_LT_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsUInt64(left) < AsUInt64(right)));
                    break;
                }
                case Opcodes.CmpLeU64: // CMP_LE_U64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsUInt64(left) <= AsUInt64(right)));
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

                    if (TryGetHostFunction(calleeName, out var hostFunc))
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
                        var result = ExecuteFrame(newFrame, module, ctx);
                        ctx.CallStack.Pop();

                        frame.Push(result ?? TsValue.Null);
                    }
                    else
                    {
                        for (int i = 0; i < argCount; i++) frame.Pop();
                        frame.Push(TsValue.Null);
                    }
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

                    if (TryGetHostFunction(calleeName, out var hostFunc))
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
                        var result = ExecuteFrame(newFrame, module, ctx);
                        ctx.CallStack.Pop();
                        frame.Push(result ?? TsValue.Null);
                    }
                    else
                    {
                        for (int i = 0; i < argCount; i++) frame.Pop();
                        frame.Push(TsValue.Null);
                    }
                    break;
                }

                // ── Object ──

                case Opcodes.NewObject: // NEW_OBJECT
                {
                    int typeIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    int argCount = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var typeName = strings[typeIdx];

                    var obj = ctx.Heap.AllocateObject(typeName);

                    string ctorName = $"{typeName}::.ctor";
                    if (module.FunctionIndex.TryGetValue(ctorName, out int ctorIdx))
                    {
                        var ctorFunc = module.Functions[ctorIdx];
                        var ctorFrame = new CallFrame(ctorFunc, frame);

                        ctorFrame.Locals[0] = TsValue.FromObject(obj);
                        for (int i = 0; i < argCount; i++)
                            ctorFrame.Locals[i + 1] = frame.Pop();

                        ctx.CallStack.Push(ctorFrame);
                        ExecuteFrame(ctorFrame, module, ctx);
                        ctx.CallStack.Pop();
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
                    for (int i = 0; i < capacity; i++)
                        arr.Set(i, frame.Pop());
                    frame.Push(new TsArrayValue(arr));
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
                    frame.Push(TsValue.FromString($"{left}{right}"));
                    break;
                }

                // ── Async ──

                case Opcodes.Await: // AWAIT
                {
                    var val = frame.Pop();
                    frame.Push(val);
                    break;
                }

                // ── Exception ──

                case Opcodes.Throw: // THROW
                {
                    var val = frame.Pop();
                    var msg = val is TsStringValue sv ? sv.Value : val.ToString();
                    throw new InvalidOperationException(msg);
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
                        TsStringValue => typeName == "string",
                        TsInt32Value => typeName == "int32",
                        TsInt64Value => typeName == "int64",
                        TsUInt64Value => typeName == "uint64",
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
                    frame.Push(new TsBoolValue(val is TsNull));
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
        TsFloat32Value v => (decimal)v.Value,
        TsFloat64Value v => (decimal)v.Value,
        TsDecimalValue v => v.Value,
        _ => 0m
    };

    private static bool AsBool(TsValue value) => value switch
    {
        TsBoolValue v => v.Value,
        TsInt32Value v => v.Value != 0,
        TsNull => false,
        _ => true
    };

    // ────────────────────────────────────────────────────────
    //  Bytecode readers
    // ────────────────────────────────────────────────────────

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
