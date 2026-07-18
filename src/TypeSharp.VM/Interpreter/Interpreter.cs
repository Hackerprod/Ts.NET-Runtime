using System.Collections.Concurrent;
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

    public TsHeap Heap => _heap;

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
        bool ownsContext = context == null;
        var ctx = context ?? CreateContext();

        try
        {
            if (!module.FunctionIndex.TryGetValue(entryPoint, out int funcIdx))
                throw new InvalidOperationException($"Entry point '{entryPoint}' not found");

            var func = module.Functions[funcIdx];
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

            byte op = bytecode[frame.InstructionPointer++];

            switch (op)
            {
                case 0x00: // NOP
                    break;

                case 0x01: // LOAD_CONST_I32
                    frame.Push(TsValue.FromInt32(ReadInt32(bytecode, ref frame.InstructionPointer)));
                    break;

                case 0x02: // LOAD_CONST_I64
                    frame.Push(TsValue.FromInt64(ReadInt64(bytecode, ref frame.InstructionPointer)));
                    break;

                case 0x03: // LOAD_CONST_F32
                    frame.Push(TsValue.FromFloat32(ReadFloat(bytecode, ref frame.InstructionPointer)));
                    break;

                case 0x04: // LOAD_CONST_F64
                    frame.Push(TsValue.FromFloat64(ReadDouble(bytecode, ref frame.InstructionPointer)));
                    break;

                case 0x05: // LOAD_CONST_STRING
                {
                    int idx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    frame.Push(TsValue.FromString(strings[idx]));
                    break;
                }

                case 0x06: // LOAD_CONST_BOOL
                    frame.Push(new TsBoolValue(bytecode[frame.InstructionPointer++] != 0));
                    break;

                case 0x07: // LOAD_CONST_NULL
                    frame.Push(TsValue.Null);
                    break;

                case 0x10: // LOAD_LOCAL
                {
                    int idx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    frame.Push(frame.Locals[idx]);
                    break;
                }

                case 0x11: // STORE_LOCAL
                {
                    int idx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    frame.Locals[idx] = frame.Pop();
                    break;
                }

                case 0x12: // LOAD_ARG
                {
                    int idx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    frame.Push(frame.Locals[idx]);
                    break;
                }

                case 0x13: // LOAD_THIS
                    frame.Push(frame.Locals[0]);
                    break;

                case 0x14: // LOAD_FIELD
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

                case 0x15: // STORE_FIELD
                {
                    int fieldIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var fieldName = strings[fieldIdx];
                    var value = frame.Pop();
                    var obj = frame.Pop();
                    if (obj is TsObjectValue objVal)
                        objVal.Value.SetField(fieldName, value);
                    break;
                }

                // Arithmetic - I32
                case 0x20: // ADD_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt32(AsInt32(left) + AsInt32(right)));
                    break;
                }
                case 0x21: // SUB_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt32(AsInt32(left) - AsInt32(right)));
                    break;
                }
                case 0x22: // MUL_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt32(AsInt32(left) * AsInt32(right)));
                    break;
                }
                case 0x23: // DIV_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    int r = AsInt32(right);
                    if (r == 0) throw new InvalidOperationException("Division by zero");
                    frame.Push(TsValue.FromInt32(AsInt32(left) / r));
                    break;
                }
                case 0x24: // MOD_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    int r = AsInt32(right);
                    if (r == 0) throw new InvalidOperationException("Division by zero");
                    frame.Push(TsValue.FromInt32(AsInt32(left) % r));
                    break;
                }
                case 0x25: // NEG_I32
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromInt32(-AsInt32(val)));
                    break;
                }

                // Arithmetic - I64
                case 0x26: // ADD_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt64(AsInt64(left) + AsInt64(right)));
                    break;
                }
                case 0x27: // SUB_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt64(AsInt64(left) - AsInt64(right)));
                    break;
                }
                case 0x28: // MUL_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromInt64(AsInt64(left) * AsInt64(right)));
                    break;
                }
                case 0x29: // DIV_I64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    long r = AsInt64(right);
                    if (r == 0) throw new InvalidOperationException("Division by zero");
                    frame.Push(TsValue.FromInt64(AsInt64(left) / r));
                    break;
                }

                // Arithmetic - F64
                case 0x2A: // ADD_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromFloat64(AsFloat64(left) + AsFloat64(right)));
                    break;
                }
                case 0x2B: // SUB_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromFloat64(AsFloat64(left) - AsFloat64(right)));
                    break;
                }
                case 0x2C: // MUL_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromFloat64(AsFloat64(left) * AsFloat64(right)));
                    break;
                }
                case 0x2D: // DIV_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromFloat64(AsFloat64(left) / AsFloat64(right)));
                    break;
                }
                case 0x32: // MOD_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromFloat64(AsFloat64(left) % AsFloat64(right)));
                    break;
                }
                case 0x33: // NEG_F64
                {
                    var val = frame.Pop();
                    frame.Push(TsValue.FromFloat64(-AsFloat64(val)));
                    break;
                }

                // Comparison - I32
                case 0x40: // CMP_EQ_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt32(left) == AsInt32(right)));
                    break;
                }
                case 0x41: // CMP_NE_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt32(left) != AsInt32(right)));
                    break;
                }
                case 0x42: // CMP_LT_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt32(left) < AsInt32(right)));
                    break;
                }
                case 0x43: // CMP_LE_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt32(left) <= AsInt32(right)));
                    break;
                }
                case 0x44: // CMP_GT_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt32(left) > AsInt32(right)));
                    break;
                }
                case 0x45: // CMP_GE_I32
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsInt32(left) >= AsInt32(right)));
                    break;
                }

                // Comparison - F64
                case 0x48: // CMP_EQ_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat64(left) == AsFloat64(right)));
                    break;
                }
                case 0x49: // CMP_NE_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat64(left) != AsFloat64(right)));
                    break;
                }
                case 0x4A: // CMP_LT_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat64(left) < AsFloat64(right)));
                    break;
                }
                case 0x4B: // CMP_LE_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat64(left) <= AsFloat64(right)));
                    break;
                }
                case 0x4C: // CMP_GT_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat64(left) > AsFloat64(right)));
                    break;
                }
                case 0x4D: // CMP_GE_F64
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsFloat64(left) >= AsFloat64(right)));
                    break;
                }

                // Logical
                case 0x50: // AND_BOOL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsBool(left) && AsBool(right)));
                    break;
                }
                case 0x51: // OR_BOOL
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(new TsBoolValue(AsBool(left) || AsBool(right)));
                    break;
                }
                case 0x52: // NOT_BOOL
                {
                    var val = frame.Pop();
                    frame.Push(new TsBoolValue(!AsBool(val)));
                    break;
                }

                // Control flow
                case 0x60: // BRANCH
                {
                    int target = ReadInt32(bytecode, ref frame.InstructionPointer);
                    frame.InstructionPointer = target;
                    break;
                }
                case 0x61: // BRANCH_TRUE
                {
                    int target = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var cond = frame.Pop();
                    if (AsBool(cond))
                        frame.InstructionPointer = target;
                    break;
                }
                case 0x62: // BRANCH_FALSE
                {
                    int target = ReadInt32(bytecode, ref frame.InstructionPointer);
                    var cond = frame.Pop();
                    if (!AsBool(cond))
                        frame.InstructionPointer = target;
                    break;
                }

                // Call
                case 0x70: // CALL
                {
                    int funcIdx = ReadInt32(bytecode, ref frame.InstructionPointer);
                    int argCount = ReadInt32(bytecode, ref frame.InstructionPointer);

                    var calleeName = strings[funcIdx];

                    if (_hostFunctions.TryGetValue(calleeName, out var hostFunc))
                    {
                        var hostArgs = new TsValue[argCount];
                        for (int i = argCount - 1; i >= 0; i--)
                            hostArgs[i] = frame.Pop();
                        var result = hostFunc(calleeName, hostArgs);
                        frame.Push(result ?? TsValue.Null);
                    }
                    else if (module.FunctionIndex.TryGetValue(calleeName, out int calleeIdx))
                    {
                        var calleeFunc = module.Functions[calleeIdx];
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

                // Return
                case 0x80: // RETURN
                {
                    return frame.Pop();
                }
                case 0x81: // RETURN_VOID
                {
                    return null;
                }

                // Object
                case 0x90: // NEW_OBJECT
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

                case 0x91: // DUP
                {
                    var val = frame.Peek();
                    frame.Push(val);
                    break;
                }

                // String
                case 0xA0: // CONCAT_STRING
                {
                    var right = frame.Pop();
                    var left = frame.Pop();
                    frame.Push(TsValue.FromString($"{left}{right}"));
                    break;
                }

                // Pop
                case 0xD0: // POP
                    frame.Pop();
                    break;

                default:
                    throw new InvalidOperationException($"Unknown opcode: 0x{op:X2}");
            }
        }

        return null;
    }

    private static int AsInt32(TsValue value) => value switch
    {
        TsInt32Value v => v.Value,
        TsInt64Value v => (int)v.Value,
        TsFloat64Value v => (int)v.Value,
        TsBoolValue v => v.Value ? 1 : 0,
        _ => 0
    };

    private static long AsInt64(TsValue value) => value switch
    {
        TsInt32Value v => v.Value,
        TsInt64Value v => v.Value,
        TsFloat64Value v => (long)v.Value,
        _ => 0
    };

    private static double AsFloat64(TsValue value) => value switch
    {
        TsInt32Value v => v.Value,
        TsInt64Value v => v.Value,
        TsFloat64Value v => v.Value,
        _ => 0.0
    };

    private static bool AsBool(TsValue value) => value switch
    {
        TsBoolValue v => v.Value,
        TsInt32Value v => v.Value != 0,
        TsNull => false,
        _ => true
    };

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
}
