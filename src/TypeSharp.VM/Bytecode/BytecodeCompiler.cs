using TypeSharp.IR;

namespace TypeSharp.VM.Bytecode;

public sealed class BytecodeFunction
{
    public string Name { get; }
    public byte[] Instructions { get; }
    public int ParameterCount { get; }
    public int LocalCount { get; }
    public bool IsAsync { get; }
    public string[] StringConstants { get; }
    public long[] IntegerConstants { get; }
    public double[] DoubleConstants { get; }
    public decimal[] DecimalConstants { get; }
    public int OperandStackCapacity { get; }

    public BytecodeFunction(string name, byte[] instructions, int parameterCount, int localCount,
        bool isAsync, string[] stringConstants, long[] integerConstants, double[] doubleConstants,
        decimal[]? decimalConstants = null, int operandStackCapacity = 0)
    {
        Name = name;
        Instructions = instructions;
        ParameterCount = parameterCount;
        LocalCount = localCount;
        IsAsync = isAsync;
        StringConstants = stringConstants;
        IntegerConstants = integerConstants;
        DoubleConstants = doubleConstants;
        DecimalConstants = decimalConstants ?? Array.Empty<decimal>();
        OperandStackCapacity = operandStackCapacity;
    }
}

public sealed class BytecodeModule
{
    public string Name { get; }
    public BytecodeFunction[] Functions { get; }
    public Dictionary<string, int> FunctionIndex { get; }

    public BytecodeModule(string name, BytecodeFunction[] functions)
    {
        Name = name;
        Functions = functions;
        FunctionIndex = new Dictionary<string, int>();

        for (int i = 0; i < functions.Length; i++)
            FunctionIndex[functions[i].Name] = i;
    }
}

public sealed class BytecodeCompilationException : Exception
{
    public BytecodeCompilationException(string message) : base(message) { }
    public BytecodeCompilationException(string message, Exception inner) : base(message, inner) { }
}

public static class BytecodeCompiler
{
    // ────────────────────────────────────────────────────────
    //  Compile entry point
    // ────────────────────────────────────────────────────────

    public static BytecodeModule Compile(ModuleIR module)
    {
        var functions = new BytecodeFunction[module.Functions.Count];

        for (int i = 0; i < module.Functions.Count; i++)
        {
            functions[i] = CompileFunction(module.Functions[i]);
        }

        var bytecode = new BytecodeModule(module.Name, functions);
        BytecodeVerifier.Verify(bytecode);
        return bytecode;
    }

    public static BytecodeFunction CompileFunction(FunctionIR function)
    {
        var writer = new BytecodeWriter();

        var stringConstants = CollectStringConstants(function);
        var intConstants = Array.Empty<long>();
        var doubleConstants = Array.Empty<double>();
        var decimalConstants = CollectDecimalConstants(function);

        var blockStarts = new Dictionary<int, int>();

        foreach (var block in function.Blocks)
        {
            blockStarts[block.Id] = writer.Position;
            foreach (var instr in block.Instructions)
            {
                EmitInstruction(writer, instr);
            }
        }

        var bytecode = writer.ToArray();
        PatchBranchTargets(bytecode, function.Blocks, blockStarts);
        var stackCapacity = ComputeMaxStackDepth(function);

        return new BytecodeFunction(
            function.Name,
            bytecode,
            function.Parameters.Count,
            function.LocalCount,
            function.IsAsync,
            stringConstants,
            intConstants,
            doubleConstants,
            decimalConstants,
            stackCapacity);
    }

    // ────────────────────────────────────────────────────────
    //  Stack depth analysis
    // ────────────────────────────────────────────────────────

    private static int ComputeMaxStackDepth(FunctionIR function)
    {
        // Conservative linear scan: walk all blocks tracking peak depth.
        // Each block is analyzed independently starting from depth 0
        // (safe upper bound since blocks may be reached from different depths).
        int maxDepth = 0;

        foreach (var block in function.Blocks)
        {
            int currentDepth = 0;
            foreach (var instr in block.Instructions)
            {
                int delta = StackDelta(instr);
                currentDepth += delta;
                if (currentDepth > maxDepth)
                    maxDepth = currentDepth;
            }
        }

        return Math.Max(maxDepth + 4, 256); // small safety margin
    }

    private static int StackDelta(Instruction instr) => instr.Opcode switch
    {
        // Pushes
        Opcode.LoadConst_I32 or Opcode.LoadConst_I64 or Opcode.LoadConst_U64 or
        Opcode.LoadConst_BigInt or Opcode.LoadConst_F32 or Opcode.LoadConst_F64 or
        Opcode.LoadConst_Decimal or Opcode.LoadConst_String or Opcode.LoadConst_Bool or
        Opcode.LoadConst_Null or Opcode.LoadConst_Void or
        Opcode.LoadLocal or Opcode.LoadArg or Opcode.LoadThis or
        Opcode.LoadField or Opcode.LoadGlobal or
        Opcode.LoadFunc => 1,

        Opcode.CopyObjectFields => -2,
        Opcode.LoadElement => -1, // pop obj + index, push value
        Opcode.NewMap => 1,

        // NewArray: pops N (operand0) elements, pushes 1 array
        Opcode.NewArray => 1 - instr.Operand0,
        // NewObject: pops args (operand1), pushes 1 result
        Opcode.NewObject => 1 - instr.Operand1,
        Opcode.Add_I32 or Opcode.Sub_I32 or Opcode.Mul_I32 or Opcode.Div_I32 or Opcode.Mod_I32 or
        Opcode.Add_I64 or Opcode.Sub_I64 or Opcode.Mul_I64 or Opcode.Div_I64 or Opcode.Mod_I64 or
        Opcode.Add_U64 or Opcode.Sub_U64 or Opcode.Mul_U64 or Opcode.Div_U64 or Opcode.Mod_U64 or
        Opcode.Add_F32 or Opcode.Sub_F32 or Opcode.Mul_F32 or Opcode.Div_F32 or
        Opcode.Add_F64 or Opcode.Sub_F64 or Opcode.Mul_F64 or Opcode.Div_F64 or Opcode.Pow_F64 or
        Opcode.Add_Decimal or Opcode.Sub_Decimal or Opcode.Mul_Decimal or Opcode.Div_Decimal or Opcode.Mod_Decimal or
        Opcode.And_I32 or Opcode.And_I64 or Opcode.And_U64 or
        Opcode.Or_I32 or Opcode.Or_I64 or Opcode.Or_U64 or
        Opcode.Xor_I32 or Opcode.Xor_I64 or Opcode.Xor_U64 or
        Opcode.Shl_I32 or Opcode.Shl_I64 or Opcode.Shl_U64 or
        Opcode.Shr_I32 or Opcode.Shr_I64 or Opcode.Shr_U64 or
        Opcode.CmpEq_I32 or Opcode.CmpNe_I32 or Opcode.CmpLt_I32 or Opcode.CmpLe_I32 or Opcode.CmpGt_I32 or Opcode.CmpGe_I32 or
        Opcode.CmpEq_I64 or Opcode.CmpNe_I64 or Opcode.CmpLt_I64 or Opcode.CmpLe_I64 or Opcode.CmpGt_I64 or Opcode.CmpGe_I64 or
        Opcode.CmpEq_U64 or Opcode.CmpNe_U64 or Opcode.CmpLt_U64 or Opcode.CmpLe_U64 or Opcode.CmpGt_U64 or Opcode.CmpGe_U64 or
        Opcode.CmpEq_F32 or Opcode.CmpNe_F32 or Opcode.CmpLt_F32 or Opcode.CmpLe_F32 or Opcode.CmpGt_F32 or Opcode.CmpGe_F32 or
        Opcode.CmpEq_F64 or Opcode.CmpNe_F64 or Opcode.CmpLt_F64 or Opcode.CmpLe_F64 or Opcode.CmpGt_F64 or Opcode.CmpGe_F64 or
        Opcode.CmpEq_Decimal or Opcode.CmpNe_Decimal or Opcode.CmpLt_Decimal or Opcode.CmpLe_Decimal or Opcode.CmpGt_Decimal or Opcode.CmpGe_Decimal or
        Opcode.CmpEq_Any or Opcode.CmpNe_Any or Opcode.CmpStrictEq_Any or Opcode.CmpStrictNe_Any or
        Opcode.And_Bool or Opcode.Or_Bool or
        Opcode.ConcatString => -1,

        // Store operations
        Opcode.StoreLocal or Opcode.StoreGlobal => -1, // pop value
        Opcode.StoreField => -2, // pop value + obj
        Opcode.StoreElement => -3, // pop value + index + obj
        Opcode.Pop or Opcode.Return => -1,

        // Dup: push 1
        Opcode.Dup => 1,

        // Call: -(argCount + 1) + 1 = -argCount
        Opcode.Call or Opcode.CallVirt => -(instr.Operand1),
        Opcode.CallDynamic => -(instr.Operand1),

        // Return
        Opcode.ReturnVoid => 0,

        // Branch/flow: neutral
        Opcode.Branch => 0,
        Opcode.BranchTrue or Opcode.BranchFalse => -1,

        // Exception
        Opcode.Throw => -1,
        Opcode.EnterTry or Opcode.LeaveTry => 0,

        // Misc
        Opcode.Nop => 0,
        Opcode.NullCheck => 1, // peek + push
        Opcode.TypeCheck => 1, // peek + push

        // Delete
        Opcode.DeleteField => -1, // pop obj, push bool
        Opcode.DeleteIndex => -2, // pop obj + index, push bool

        _ => 0,
    };

    // ────────────────────────────────────────────────────────
    //  Emit single instruction
    // ────────────────────────────────────────────────────────

    private static void EmitInstruction(BytecodeWriter writer, Instruction instr)
    {
        switch (instr.Opcode)
        {
            // ── Nop ──
            case Opcode.Nop:
                writer.Write(Opcodes.Nop);
                break;

            // ── Load constants ──
            case Opcode.LoadConst_I32:
                writer.Write(Opcodes.LoadConstI32);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.LoadConst_I64:
                writer.Write(Opcodes.LoadConstI64);
                writer.WriteInt64(Convert.ToInt64(instr.OperandObject ?? instr.Operand0));
                break;
            case Opcode.LoadConst_U64:
                writer.Write(Opcodes.LoadConstU64);
                writer.WriteUInt64(Convert.ToUInt64(instr.OperandObject ?? 0UL));
                break;
            case Opcode.LoadConst_BigInt:
                writer.Write(Opcodes.LoadConstBigInt);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.LoadConst_F32:
                writer.Write(Opcodes.LoadConstF32);
                writer.WriteFloat(Convert.ToSingle(instr.OperandObject ?? 0f));
                break;
            case Opcode.LoadConst_F64:
                writer.Write(Opcodes.LoadConstF64);
                writer.WriteDouble(Convert.ToDouble(instr.OperandObject ?? 0.0));
                break;
            case Opcode.LoadConst_String:
                writer.Write(Opcodes.LoadConstString);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.LoadConst_Bool:
                writer.Write(Opcodes.LoadConstBool);
                writer.Write(instr.Operand0 != 0 ? (byte)1 : (byte)0);
                break;
            case Opcode.LoadConst_Null:
                writer.Write(Opcodes.LoadConstNull);
                break;
            case Opcode.LoadConst_Void:
                writer.Write(Opcodes.LoadConstVoid);
                break;

            // ── Variables ──
            case Opcode.LoadLocal:
                writer.Write(Opcodes.LoadLocal);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.StoreLocal:
                writer.Write(Opcodes.StoreLocal);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.LoadArg:
                writer.Write(Opcodes.LoadArg);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.LoadThis:
                writer.Write(Opcodes.LoadThis);
                break;
            case Opcode.LoadField:
                writer.Write(Opcodes.LoadField);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.StoreField:
                writer.Write(Opcodes.StoreField);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.LoadGlobal:
                writer.Write(Opcodes.LoadGlobal);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.StoreGlobal:
                writer.Write(Opcodes.StoreGlobal);
                writer.WriteInt32(instr.Operand0);
                break;

            // ── I32 arithmetic ──
            case Opcode.Add_I32: writer.Write(Opcodes.AddI32); break;
            case Opcode.Sub_I32: writer.Write(Opcodes.SubI32); break;
            case Opcode.Mul_I32: writer.Write(Opcodes.MulI32); break;
            case Opcode.Div_I32: writer.Write(Opcodes.DivI32); break;
            case Opcode.Mod_I32: writer.Write(Opcodes.ModI32); break;
            case Opcode.Neg_I32: writer.Write(Opcodes.NegI32); break;

            // ── I64 arithmetic ──
            case Opcode.Add_I64: writer.Write(Opcodes.AddI64); break;
            case Opcode.Sub_I64: writer.Write(Opcodes.SubI64); break;
            case Opcode.Mul_I64: writer.Write(Opcodes.MulI64); break;
            case Opcode.Div_I64: writer.Write(Opcodes.DivI64); break;
            case Opcode.Mod_I64: writer.Write(Opcodes.ModI64); break;
            case Opcode.Neg_I64: writer.Write(Opcodes.NegI64); break;

            // ── U64 arithmetic ──
            case Opcode.Add_U64: writer.Write(Opcodes.AddU64); break;
            case Opcode.Sub_U64: writer.Write(Opcodes.SubU64); break;
            case Opcode.Mul_U64: writer.Write(Opcodes.MulU64); break;
            case Opcode.Div_U64: writer.Write(Opcodes.DivU64); break;
            case Opcode.Mod_U64: writer.Write(Opcodes.ModU64); break;

            // ── F64 arithmetic ──
            case Opcode.Add_F64: writer.Write(Opcodes.AddF64); break;
            case Opcode.Sub_F64: writer.Write(Opcodes.SubF64); break;
            case Opcode.Mul_F64: writer.Write(Opcodes.MulF64); break;
            case Opcode.Div_F64: writer.Write(Opcodes.DivF64); break;
            case Opcode.Mod_F64: writer.Write(Opcodes.ModF64); break;
            case Opcode.Neg_F64: writer.Write(Opcodes.NegF64); break;

            // ── F32 arithmetic ──
            case Opcode.Add_F32: writer.Write(Opcodes.AddF32); break;
            case Opcode.Sub_F32: writer.Write(Opcodes.SubF32); break;
            case Opcode.Mul_F32: writer.Write(Opcodes.MulF32); break;
            case Opcode.Div_F32: writer.Write(Opcodes.DivF32); break;
            case Opcode.Neg_F32: writer.Write(Opcodes.NegF32); break;

            // ── I32 bitwise ──
            case Opcode.And_I32: writer.Write(Opcodes.AndI32); break;
            case Opcode.Or_I32: writer.Write(Opcodes.OrI32); break;
            case Opcode.Xor_I32: writer.Write(Opcodes.XorI32); break;
            case Opcode.Not_I32: writer.Write(Opcodes.NotI32); break;
            case Opcode.Shl_I32: writer.Write(Opcodes.ShlI32); break;
            case Opcode.Shr_I32: writer.Write(Opcodes.ShrI32); break;

            // ── I64 bitwise ──
            case Opcode.And_I64: writer.Write(Opcodes.AndI64); break;
            case Opcode.Or_I64: writer.Write(Opcodes.OrI64); break;
            case Opcode.Xor_I64: writer.Write(Opcodes.XorI64); break;
            case Opcode.Not_I64: writer.Write(Opcodes.NotI64); break;
            case Opcode.Shl_I64: writer.Write(Opcodes.ShlI64); break;
            case Opcode.Shr_I64: writer.Write(Opcodes.ShrI64); break;
            case Opcode.And_U64: writer.Write(Opcodes.AndU64); break;
            case Opcode.Or_U64: writer.Write(Opcodes.OrU64); break;
            case Opcode.Xor_U64: writer.Write(Opcodes.XorU64); break;
            case Opcode.Not_U64: writer.Write(Opcodes.NotU64); break;
            case Opcode.Shl_U64: writer.Write(Opcodes.ShlU64); break;
            case Opcode.Shr_U64: writer.Write(Opcodes.ShrU64); break;

            // ── I32 comparison ──
            case Opcode.CmpEq_I32: writer.Write(Opcodes.CmpEqI32); break;
            case Opcode.CmpNe_I32: writer.Write(Opcodes.CmpNeI32); break;
            case Opcode.CmpLt_I32: writer.Write(Opcodes.CmpLtI32); break;
            case Opcode.CmpLe_I32: writer.Write(Opcodes.CmpLeI32); break;
            case Opcode.CmpGt_I32: writer.Write(Opcodes.CmpGtI32); break;
            case Opcode.CmpGe_I32: writer.Write(Opcodes.CmpGeI32); break;

            // ── I64 comparison ──
            case Opcode.CmpEq_I64: writer.Write(Opcodes.CmpEqI64); break;
            case Opcode.CmpNe_I64: writer.Write(Opcodes.CmpNeI64); break;
            case Opcode.CmpLt_I64: writer.Write(Opcodes.CmpLtI64); break;
            case Opcode.CmpLe_I64: writer.Write(Opcodes.CmpLeI64); break;
            case Opcode.CmpGt_I64: writer.Write(Opcodes.CmpGtI64); break;
            case Opcode.CmpGe_I64: writer.Write(Opcodes.CmpGeI64); break;

            // ── U64 comparison ──
            case Opcode.CmpEq_U64: writer.Write(Opcodes.CmpEqU64); break;
            case Opcode.CmpNe_U64: writer.Write(Opcodes.CmpNeU64); break;
            case Opcode.CmpLt_U64: writer.Write(Opcodes.CmpLtU64); break;
            case Opcode.CmpLe_U64: writer.Write(Opcodes.CmpLeU64); break;
            case Opcode.CmpGt_U64: writer.Write(Opcodes.CmpGtU64); break;
            case Opcode.CmpGe_U64: writer.Write(Opcodes.CmpGeU64); break;

            // ── F64 comparison ──
            case Opcode.CmpEq_F64: writer.Write(Opcodes.CmpEqF64); break;
            case Opcode.CmpNe_F64: writer.Write(Opcodes.CmpNeF64); break;
            case Opcode.CmpLt_F64: writer.Write(Opcodes.CmpLtF64); break;
            case Opcode.CmpLe_F64: writer.Write(Opcodes.CmpLeF64); break;
            case Opcode.CmpGt_F64: writer.Write(Opcodes.CmpGtF64); break;
            case Opcode.CmpGe_F64: writer.Write(Opcodes.CmpGeF64); break;

            // ── F32 comparison ──
            case Opcode.CmpEq_F32: writer.Write(Opcodes.CmpEqF32); break;
            case Opcode.CmpNe_F32: writer.Write(Opcodes.CmpNeF32); break;
            case Opcode.CmpLt_F32: writer.Write(Opcodes.CmpLtF32); break;
            case Opcode.CmpLe_F32: writer.Write(Opcodes.CmpLeF32); break;
            case Opcode.CmpGt_F32: writer.Write(Opcodes.CmpGtF32); break;
            case Opcode.CmpGe_F32: writer.Write(Opcodes.CmpGeF32); break;

            // ── Logical ──
            case Opcode.And_Bool: writer.Write(Opcodes.AndBool); break;
            case Opcode.Or_Bool: writer.Write(Opcodes.OrBool); break;
            case Opcode.Not_Bool: writer.Write(Opcodes.NotBool); break;

            // ── Control flow ──
            case Opcode.Branch:
                writer.Write(Opcodes.Branch);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.BranchTrue:
                writer.Write(Opcodes.BranchTrue);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.BranchFalse:
                writer.Write(Opcodes.BranchFalse);
                writer.WriteInt32(instr.Operand0);
                break;

            // ── Functions ──
            case Opcode.Call:
                writer.Write(Opcodes.Call);
                writer.WriteInt32(instr.Operand0);
                writer.WriteInt32(instr.Operand1);
                break;

            case Opcode.Return: writer.Write(Opcodes.Return); break;
            case Opcode.ReturnVoid: writer.Write(Opcodes.ReturnVoid); break;

            // ── Object ──
            case Opcode.NewObject:
                writer.Write(Opcodes.NewObject);
                writer.WriteInt32(instr.Operand0);
                writer.WriteInt32(instr.Operand1);
                break;
            case Opcode.CopyObjectFields: writer.Write(Opcodes.CopyObjectFields); break;
            case Opcode.Dup: writer.Write(Opcodes.Dup); break;
            case Opcode.Pop: writer.Write(Opcodes.Pop); break;

            // ── String ──
            case Opcode.ConcatString: writer.Write(Opcodes.ConcatString); break;

            // ── Async ──
            case Opcode.Await: writer.Write(Opcodes.Await); break;

            // ── Convert ──
            case Opcode.Conv_I32_I64: writer.Write(Opcodes.ConvI32I64); break;
            case Opcode.Conv_I64_I32: writer.Write(Opcodes.ConvI64I32); break;
            case Opcode.Conv_I32_F64: writer.Write(Opcodes.ConvI32F64); break;
            case Opcode.Conv_F64_I32: writer.Write(Opcodes.ConvF64I32); break;
            case Opcode.Conv_I32_F32: writer.Write(Opcodes.ConvI32F32); break;
            case Opcode.Conv_F32_I32: writer.Write(Opcodes.ConvF32I32); break;
            case Opcode.Conv_U64_I64: writer.Write(Opcodes.ConvU64I64); break;
            case Opcode.Conv_I64_U64: writer.Write(Opcodes.ConvI64U64); break;
            case Opcode.Conv_U64_F64: writer.Write(Opcodes.ConvU64F64); break;
            case Opcode.Conv_F64_U64: writer.Write(Opcodes.ConvF64U64); break;
            case Opcode.Conv_U64_I32: writer.Write(Opcodes.ConvU64I32); break;
            case Opcode.Conv_I32_U64: writer.Write(Opcodes.ConvI32U64); break;
            case Opcode.Conv_F32_F64: writer.Write(Opcodes.ConvF32F64); break;
            case Opcode.Conv_F64_F32: writer.Write(Opcodes.ConvF64F32); break;

            // ── Decimal arithmetic ──
            case Opcode.Add_Decimal: writer.Write(Opcodes.AddDecimal); break;
            case Opcode.Sub_Decimal: writer.Write(Opcodes.SubDecimal); break;
            case Opcode.Mul_Decimal: writer.Write(Opcodes.MulDecimal); break;
            case Opcode.Div_Decimal: writer.Write(Opcodes.DivDecimal); break;
            case Opcode.Mod_Decimal: writer.Write(Opcodes.ModDecimal); break;
            case Opcode.Neg_Decimal: writer.Write(Opcodes.NegDecimal); break;

            // ── Decimal comparison ──
            case Opcode.CmpEq_Decimal: writer.Write(Opcodes.CmpEqDecimal); break;
            case Opcode.CmpNe_Decimal: writer.Write(Opcodes.CmpNeDecimal); break;
            case Opcode.CmpLt_Decimal: writer.Write(Opcodes.CmpLtDecimal); break;
            case Opcode.CmpLe_Decimal: writer.Write(Opcodes.CmpLeDecimal); break;
            case Opcode.CmpGt_Decimal: writer.Write(Opcodes.CmpGtDecimal); break;
            case Opcode.CmpGe_Decimal: writer.Write(Opcodes.CmpGeDecimal); break;

            // ── Missing emit cases ──
            case Opcode.LoadConst_Decimal:
                writer.Write(Opcodes.LoadConstDecimal);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.CallVirt:
                writer.Write(Opcodes.CallVirt);
                writer.WriteInt32(instr.Operand0);
                writer.WriteInt32(instr.Operand1);
                break;
            case Opcode.Throw: writer.Write(Opcodes.Throw); break;
            case Opcode.NewArray:
                writer.Write(Opcodes.NewArray);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.LoadElement: writer.Write(Opcodes.LoadElement); break;
            case Opcode.StoreElement: writer.Write(Opcodes.StoreElement); break;
            case Opcode.LoadFunc:
                writer.Write(Opcodes.LoadFunc);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.CallDynamic:
                writer.Write(Opcodes.CallDynamic);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.MakeClosure:
                writer.Write(Opcodes.MakeClosure);
                writer.WriteInt32(instr.Operand0);
                writer.WriteInt32(instr.Operand1);
                break;
            case Opcode.CmpEq_Any: writer.Write(Opcodes.CmpEqAny); break;
            case Opcode.CmpNe_Any: writer.Write(Opcodes.CmpNeAny); break;
            case Opcode.CmpStrictEq_Any: writer.Write(Opcodes.CmpStrictEqAny); break;
            case Opcode.CmpStrictNe_Any: writer.Write(Opcodes.CmpStrictNeAny); break;
            case Opcode.Pow_F64: writer.Write(Opcodes.PowF64); break;
            case Opcode.TypeOf: writer.Write(Opcodes.TypeOf); break;
            case Opcode.DeleteField:
                writer.Write(Opcodes.DeleteField);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.DeleteIndex: writer.Write(Opcodes.DeleteIndex); break;
            case Opcode.EnterTry:
                writer.Write(Opcodes.EnterTry);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.LeaveTry: writer.Write(Opcodes.LeaveTry); break;
            case Opcode.NewMap: writer.Write(Opcodes.NewMap); break;
            case Opcode.TypeCheck:
                writer.Write(Opcodes.TypeCheck);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.NullCheck: writer.Write(Opcodes.NullCheck); break;

            // ── DEFAULT: throw on unimplemented opcode ──
            default:
                throw new BytecodeCompilationException(
                    $"Opcode {instr.Opcode} is not implemented");
        }
    }

    // ────────────────────────────────────────────────────────
    //  String constant collection
    // ────────────────────────────────────────────────────────

    private static string[] CollectStringConstants(FunctionIR function)
    {
        var strings = new List<string>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.OperandObject is string s)
                {
                    if (instr.Opcode is Opcode.LoadConst_String or
                        Opcode.LoadConst_BigInt or
                        Opcode.Call or
                        Opcode.CallVirt or
                        Opcode.NewObject or
                        Opcode.LoadField or
                        Opcode.StoreField or
                        Opcode.DeleteField or
                        Opcode.LoadGlobal or
                        Opcode.StoreGlobal or
                        Opcode.LoadFunc or
                        Opcode.MakeClosure)
                    {
                        if (!indexes.TryGetValue(s, out var index))
                        {
                            index = strings.Count;
                            indexes.Add(s, index);
                            strings.Add(s);
                        }

                        instr.Operand0 = index;
                    }
                }
            }
        }
        return strings.ToArray();
    }

    private static decimal[] CollectDecimalConstants(FunctionIR function)
    {
        var decimals = new List<decimal>();
        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.Opcode == Opcode.LoadConst_Decimal)
                {
                    instr.Operand0 = decimals.Count;
                    decimals.Add(Convert.ToDecimal(instr.OperandObject ?? 0m));
                }
            }
        }
        return decimals.ToArray();
    }

    // ────────────────────────────────────────────────────────
    //  Patch branch targets (block ID → byte offset)
    // ────────────────────────────────────────────────────────

    private static void PatchBranchTargets(byte[] bytecode, List<BasicBlock> blocks, Dictionary<int, int> blockStarts)
    {
        var stream = new MemoryStream(bytecode);
        var reader = new BinaryReader(stream);
        var writer = new BinaryWriter(stream);

        while (stream.Position < stream.Length)
        {
            long opStart = stream.Position;
            byte op = reader.ReadByte();
            var fmt = OpcodeFormats.Get(op);

            if (fmt.IsBranch)
            {
                int blockId = reader.ReadInt32();
                if (blockStarts.TryGetValue(blockId, out int byteOffset))
                {
                    stream.Seek(-4, SeekOrigin.Current);
                    writer.Write(byteOffset);
                }
            }
            else
            {
                stream.Seek(fmt.OperandBytes, SeekOrigin.Current);
            }
        }
    }

    // ────────────────────────────────────────────────────────
    //  Bytecode writer
    // ────────────────────────────────────────────────────────

    private sealed class BytecodeWriter
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;

        public int Position => (int)_stream.Position;

        public BytecodeWriter()
        {
            _writer = new BinaryWriter(_stream);
        }

        public void Write(byte b) => _writer.Write(b);
        public void WriteInt32(int value) => _writer.Write(value);
        public void WriteInt64(long value) => _writer.Write(value);
        public void WriteUInt64(ulong value) => _writer.Write(value);
        public void WriteFloat(float value) => _writer.Write(value);
        public void WriteDouble(double value) => _writer.Write(value);
        public void WriteDecimal(decimal value) => _writer.Write(value);

        public byte[] ToArray() => _stream.ToArray();
    }
}
