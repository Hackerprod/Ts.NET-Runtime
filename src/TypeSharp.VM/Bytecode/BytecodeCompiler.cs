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

    public BytecodeFunction(string name, byte[] instructions, int parameterCount, int localCount,
        bool isAsync, string[] stringConstants, long[] integerConstants, double[] doubleConstants)
    {
        Name = name;
        Instructions = instructions;
        ParameterCount = parameterCount;
        LocalCount = localCount;
        IsAsync = isAsync;
        StringConstants = stringConstants;
        IntegerConstants = integerConstants;
        DoubleConstants = doubleConstants;
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
    // ── Load constants ──
    private const byte OP_NOP = 0x00;
    private const byte OP_LOAD_CONST_I32 = 0x01;
    private const byte OP_LOAD_CONST_I64 = 0x02;
    private const byte OP_LOAD_CONST_F32 = 0x03;
    private const byte OP_LOAD_CONST_F64 = 0x04;
    private const byte OP_LOAD_CONST_STRING = 0x05;
    private const byte OP_LOAD_CONST_BOOL = 0x06;
    private const byte OP_LOAD_CONST_NULL = 0x07;
    private const byte OP_LOAD_CONST_U64 = 0x08;

    // ── Variables ──
    private const byte OP_LOAD_LOCAL = 0x10;
    private const byte OP_STORE_LOCAL = 0x11;
    private const byte OP_LOAD_ARG = 0x12;
    private const byte OP_LOAD_THIS = 0x13;
    private const byte OP_LOAD_FIELD = 0x14;
    private const byte OP_STORE_FIELD = 0x15;

    // ── I32 arithmetic ──
    private const byte OP_ADD_I32 = 0x20;
    private const byte OP_SUB_I32 = 0x21;
    private const byte OP_MUL_I32 = 0x22;
    private const byte OP_DIV_I32 = 0x23;
    private const byte OP_MOD_I32 = 0x24;
    private const byte OP_NEG_I32 = 0x25;

    // ── I64 arithmetic ──
    private const byte OP_ADD_I64 = 0x26;
    private const byte OP_SUB_I64 = 0x27;
    private const byte OP_MUL_I64 = 0x28;
    private const byte OP_DIV_I64 = 0x29;
    private const byte OP_MOD_I64 = 0x2A;
    private const byte OP_NEG_I64 = 0x2B;

    // ── U64 arithmetic ──
    private const byte OP_ADD_U64 = 0x2C;
    private const byte OP_SUB_U64 = 0x2D;
    private const byte OP_MUL_U64 = 0x2E;
    private const byte OP_DIV_U64 = 0x2F;
    private const byte OP_MOD_U64 = 0x30;

    // ── F64 arithmetic ──
    private const byte OP_ADD_F64 = 0x31;
    private const byte OP_SUB_F64 = 0x32;
    private const byte OP_MUL_F64 = 0x33;
    private const byte OP_DIV_F64 = 0x34;
    private const byte OP_MOD_F64 = 0x35;
    private const byte OP_NEG_F64 = 0x36;

    // ── F32 arithmetic ──
    private const byte OP_ADD_F32 = 0x37;
    private const byte OP_SUB_F32 = 0x38;
    private const byte OP_MUL_F32 = 0x39;
    private const byte OP_DIV_F32 = 0x3A;
    private const byte OP_NEG_F32 = 0x3B;

    // ── I32 bitwise ──
    private const byte OP_AND_I32 = 0x3C;
    private const byte OP_OR_I32 = 0x3D;
    private const byte OP_XOR_I32 = 0x3E;
    private const byte OP_NOT_I32 = 0x3F;

    // ── I32 comparison ──
    private const byte OP_CMP_EQ_I32 = 0x40;
    private const byte OP_CMP_NE_I32 = 0x41;
    private const byte OP_CMP_LT_I32 = 0x42;
    private const byte OP_CMP_LE_I32 = 0x43;
    private const byte OP_CMP_GT_I32 = 0x44;
    private const byte OP_CMP_GE_I32 = 0x45;

    // ── I64 comparison ──
    private const byte OP_CMP_EQ_I64 = 0x46;
    private const byte OP_CMP_NE_I64 = 0x47;
    private const byte OP_CMP_LT_I64 = 0x48;
    private const byte OP_CMP_LE_I64 = 0x49;
    private const byte OP_CMP_GT_I64 = 0x4A;
    private const byte OP_CMP_GE_I64 = 0x4B;

    // ── U64 comparison ──
    private const byte OP_CMP_EQ_U64 = 0x4C;
    private const byte OP_CMP_NE_U64 = 0x4D;
    private const byte OP_CMP_LT_U64 = 0x4E;
    private const byte OP_CMP_LE_U64 = 0x4F;

    // ── F64 comparison ──
    private const byte OP_CMP_EQ_F64 = 0x50;
    private const byte OP_CMP_NE_F64 = 0x51;
    private const byte OP_CMP_LT_F64 = 0x52;
    private const byte OP_CMP_LE_F64 = 0x53;
    private const byte OP_CMP_GT_F64 = 0x54;
    private const byte OP_CMP_GE_F64 = 0x55;

    // ── F32 comparison ──
    private const byte OP_CMP_EQ_F32 = 0x56;
    private const byte OP_CMP_NE_F32 = 0x57;
    private const byte OP_CMP_LT_F32 = 0x58;
    private const byte OP_CMP_LE_F32 = 0x59;
    private const byte OP_CMP_GT_F32 = 0x5A;
    private const byte OP_CMP_GE_F32 = 0x5B;

    // ── Logical ──
    private const byte OP_AND_BOOL = 0x5C;
    private const byte OP_OR_BOOL = 0x5D;
    private const byte OP_NOT_BOOL = 0x5E;

    // ── I64 bitwise ──
    private const byte OP_AND_I64 = 0x60;
    private const byte OP_OR_I64 = 0x61;
    private const byte OP_XOR_I64 = 0x62;
    private const byte OP_NOT_I64 = 0x63;
    private const byte OP_SHL_I32 = 0x64;
    private const byte OP_SHR_I32 = 0x65;
    private const byte OP_SHL_I64 = 0x66;
    private const byte OP_SHR_I64 = 0x67;

    // ── Control flow ──
    private const byte OP_BRANCH = 0x70;
    private const byte OP_BRANCH_TRUE = 0x71;
    private const byte OP_BRANCH_FALSE = 0x72;

    // ── Functions ──
    private const byte OP_CALL = 0x73;
    private const byte OP_CALL_HOST = 0x74;
    private const byte OP_RETURN = 0x75;
    private const byte OP_RETURN_VOID = 0x76;

    // ── Object ──
    private const byte OP_NEW_OBJECT = 0x80;
    private const byte OP_NEW_ARRAY = 0x81;
    private const byte OP_NEW_MAP = 0x82;
    private const byte OP_DUP = 0x83;
    private const byte OP_POP = 0x84;

    // ── String ──
    private const byte OP_CONCAT_STRING = 0x85;

    // ── Async ──
    private const byte OP_AWAIT = 0x86;

    // ── Exception ──
    private const byte OP_THROW = 0x87;

    // ── Convert (I32 ↔ I64, I32 ↔ F64, I32 ↔ F32) ──
    private const byte OP_CONV_I32_I64 = 0x90;
    private const byte OP_CONV_I64_I32 = 0x91;
    private const byte OP_CONV_I32_F64 = 0x92;
    private const byte OP_CONV_F64_I32 = 0x93;
    private const byte OP_CONV_I32_F32 = 0x94;
    private const byte OP_CONV_F32_I32 = 0x95;

    // ── Convert (U64) ──
    private const byte OP_CONV_U64_I64 = 0x96;
    private const byte OP_CONV_I64_U64 = 0x97;
    private const byte OP_CONV_U64_F64 = 0x98;
    private const byte OP_CONV_F64_U64 = 0x99;
    private const byte OP_CONV_U64_I32 = 0x9A;
    private const byte OP_CONV_I32_U64 = 0x9B;

    // ── Convert (F32 ↔ F64) ──
    private const byte OP_CONV_F32_F64 = 0x9C;
    private const byte OP_CONV_F64_F32 = 0x9D;

    // ── Utilities ──
    private const byte OP_TYPE_CHECK = 0xA0;
    private const byte OP_NULL_CHECK = 0xA1;

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

        return new BytecodeModule(module.Name, functions);
    }

    public static BytecodeFunction CompileFunction(FunctionIR function)
    {
        var writer = new BytecodeWriter();

        var stringConstants = CollectStringConstants(function);
        var intConstants = Array.Empty<long>();
        var doubleConstants = Array.Empty<double>();

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

        return new BytecodeFunction(
            function.Name,
            bytecode,
            function.Parameters.Count,
            function.LocalCount,
            function.IsAsync,
            stringConstants,
            intConstants,
            doubleConstants);
    }

    // ────────────────────────────────────────────────────────
    //  Emit single instruction
    // ────────────────────────────────────────────────────────

    private static void EmitInstruction(BytecodeWriter writer, Instruction instr)
    {
        switch (instr.Opcode)
        {
            // ── Nop ──
            case Opcode.Nop:
                writer.Write(OP_NOP);
                break;

            // ── Load constants ──
            case Opcode.LoadConst_I32:
                writer.Write(OP_LOAD_CONST_I32);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.LoadConst_I64:
                writer.Write(OP_LOAD_CONST_I64);
                writer.WriteInt64(Convert.ToInt64(instr.OperandObject ?? instr.Operand0));
                break;
            case Opcode.LoadConst_U64:
                writer.Write(OP_LOAD_CONST_U64);
                writer.WriteUInt64(Convert.ToUInt64(instr.OperandObject ?? 0UL));
                break;
            case Opcode.LoadConst_F32:
                writer.Write(OP_LOAD_CONST_F32);
                writer.WriteFloat(Convert.ToSingle(instr.OperandObject ?? 0f));
                break;
            case Opcode.LoadConst_F64:
                writer.Write(OP_LOAD_CONST_F64);
                writer.WriteDouble(Convert.ToDouble(instr.OperandObject ?? 0.0));
                break;
            case Opcode.LoadConst_String:
                writer.Write(OP_LOAD_CONST_STRING);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.LoadConst_Bool:
                writer.Write(OP_LOAD_CONST_BOOL);
                writer.Write(instr.Operand0 != 0 ? (byte)1 : (byte)0);
                break;
            case Opcode.LoadConst_Null:
                writer.Write(OP_LOAD_CONST_NULL);
                break;

            // ── Variables ──
            case Opcode.LoadLocal:
                writer.Write(OP_LOAD_LOCAL);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.StoreLocal:
                writer.Write(OP_STORE_LOCAL);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.LoadArg:
                writer.Write(OP_LOAD_ARG);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.LoadThis:
                writer.Write(OP_LOAD_THIS);
                break;
            case Opcode.LoadField:
                writer.Write(OP_LOAD_FIELD);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.StoreField:
                writer.Write(OP_STORE_FIELD);
                writer.WriteInt32(instr.Operand0);
                break;

            // ── I32 arithmetic ──
            case Opcode.Add_I32: writer.Write(OP_ADD_I32); break;
            case Opcode.Sub_I32: writer.Write(OP_SUB_I32); break;
            case Opcode.Mul_I32: writer.Write(OP_MUL_I32); break;
            case Opcode.Div_I32: writer.Write(OP_DIV_I32); break;
            case Opcode.Mod_I32: writer.Write(OP_MOD_I32); break;
            case Opcode.Neg_I32: writer.Write(OP_NEG_I32); break;

            // ── I64 arithmetic ──
            case Opcode.Add_I64: writer.Write(OP_ADD_I64); break;
            case Opcode.Sub_I64: writer.Write(OP_SUB_I64); break;
            case Opcode.Mul_I64: writer.Write(OP_MUL_I64); break;
            case Opcode.Div_I64: writer.Write(OP_DIV_I64); break;
            case Opcode.Mod_I64: writer.Write(OP_MOD_I64); break;
            case Opcode.Neg_I64: writer.Write(OP_NEG_I64); break;

            // ── U64 arithmetic ──
            case Opcode.Add_U64: writer.Write(OP_ADD_U64); break;
            case Opcode.Sub_U64: writer.Write(OP_SUB_U64); break;
            case Opcode.Mul_U64: writer.Write(OP_MUL_U64); break;
            case Opcode.Div_U64: writer.Write(OP_DIV_U64); break;
            case Opcode.Mod_U64: writer.Write(OP_MOD_U64); break;

            // ── F64 arithmetic ──
            case Opcode.Add_F64: writer.Write(OP_ADD_F64); break;
            case Opcode.Sub_F64: writer.Write(OP_SUB_F64); break;
            case Opcode.Mul_F64: writer.Write(OP_MUL_F64); break;
            case Opcode.Div_F64: writer.Write(OP_DIV_F64); break;
            case Opcode.Mod_F64: writer.Write(OP_MOD_F64); break;
            case Opcode.Neg_F64: writer.Write(OP_NEG_F64); break;

            // ── F32 arithmetic ──
            case Opcode.Add_F32: writer.Write(OP_ADD_F32); break;
            case Opcode.Sub_F32: writer.Write(OP_SUB_F32); break;
            case Opcode.Mul_F32: writer.Write(OP_MUL_F32); break;
            case Opcode.Div_F32: writer.Write(OP_DIV_F32); break;
            case Opcode.Neg_F32: writer.Write(OP_NEG_F32); break;

            // ── I32 bitwise ──
            case Opcode.And_I32: writer.Write(OP_AND_I32); break;
            case Opcode.Or_I32: writer.Write(OP_OR_I32); break;
            case Opcode.Xor_I32: writer.Write(OP_XOR_I32); break;
            case Opcode.Not_I32: writer.Write(OP_NOT_I32); break;
            case Opcode.Shl_I32: writer.Write(OP_SHL_I32); break;
            case Opcode.Shr_I32: writer.Write(OP_SHR_I32); break;

            // ── I64 bitwise ──
            case Opcode.And_I64: writer.Write(OP_AND_I64); break;
            case Opcode.Or_I64: writer.Write(OP_OR_I64); break;
            case Opcode.Xor_I64: writer.Write(OP_XOR_I64); break;
            case Opcode.Not_I64: writer.Write(OP_NOT_I64); break;
            case Opcode.Shl_I64: writer.Write(OP_SHL_I64); break;
            case Opcode.Shr_I64: writer.Write(OP_SHR_I64); break;

            // ── I32 comparison ──
            case Opcode.CmpEq_I32: writer.Write(OP_CMP_EQ_I32); break;
            case Opcode.CmpNe_I32: writer.Write(OP_CMP_NE_I32); break;
            case Opcode.CmpLt_I32: writer.Write(OP_CMP_LT_I32); break;
            case Opcode.CmpLe_I32: writer.Write(OP_CMP_LE_I32); break;
            case Opcode.CmpGt_I32: writer.Write(OP_CMP_GT_I32); break;
            case Opcode.CmpGe_I32: writer.Write(OP_CMP_GE_I32); break;

            // ── I64 comparison ──
            case Opcode.CmpEq_I64: writer.Write(OP_CMP_EQ_I64); break;
            case Opcode.CmpNe_I64: writer.Write(OP_CMP_NE_I64); break;
            case Opcode.CmpLt_I64: writer.Write(OP_CMP_LT_I64); break;
            case Opcode.CmpLe_I64: writer.Write(OP_CMP_LE_I64); break;
            case Opcode.CmpGt_I64: writer.Write(OP_CMP_GT_I64); break;
            case Opcode.CmpGe_I64: writer.Write(OP_CMP_GE_I64); break;

            // ── U64 comparison ──
            case Opcode.CmpEq_U64: writer.Write(OP_CMP_EQ_U64); break;
            case Opcode.CmpNe_U64: writer.Write(OP_CMP_NE_U64); break;
            case Opcode.CmpLt_U64: writer.Write(OP_CMP_LT_U64); break;
            case Opcode.CmpLe_U64: writer.Write(OP_CMP_LE_U64); break;

            // ── F64 comparison ──
            case Opcode.CmpEq_F64: writer.Write(OP_CMP_EQ_F64); break;
            case Opcode.CmpNe_F64: writer.Write(OP_CMP_NE_F64); break;
            case Opcode.CmpLt_F64: writer.Write(OP_CMP_LT_F64); break;
            case Opcode.CmpLe_F64: writer.Write(OP_CMP_LE_F64); break;
            case Opcode.CmpGt_F64: writer.Write(OP_CMP_GT_F64); break;
            case Opcode.CmpGe_F64: writer.Write(OP_CMP_GE_F64); break;

            // ── F32 comparison ──
            case Opcode.CmpEq_F32: writer.Write(OP_CMP_EQ_F32); break;
            case Opcode.CmpNe_F32: writer.Write(OP_CMP_NE_F32); break;
            case Opcode.CmpLt_F32: writer.Write(OP_CMP_LT_F32); break;
            case Opcode.CmpLe_F32: writer.Write(OP_CMP_LE_F32); break;
            case Opcode.CmpGt_F32: writer.Write(OP_CMP_GT_F32); break;
            case Opcode.CmpGe_F32: writer.Write(OP_CMP_GE_F32); break;

            // ── Logical ──
            case Opcode.And_Bool: writer.Write(OP_AND_BOOL); break;
            case Opcode.Or_Bool: writer.Write(OP_OR_BOOL); break;
            case Opcode.Not_Bool: writer.Write(OP_NOT_BOOL); break;

            // ── Control flow ──
            case Opcode.Branch:
                writer.Write(OP_BRANCH);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.BranchTrue:
                writer.Write(OP_BRANCH_TRUE);
                writer.WriteInt32(instr.Operand0);
                break;
            case Opcode.BranchFalse:
                writer.Write(OP_BRANCH_FALSE);
                writer.WriteInt32(instr.Operand0);
                break;

            // ── Functions ──
            case Opcode.Call:
                writer.Write(OP_CALL);
                writer.WriteInt32(instr.Operand0);
                writer.WriteInt32(instr.Operand1);
                break;

            case Opcode.Return: writer.Write(OP_RETURN); break;
            case Opcode.ReturnVoid: writer.Write(OP_RETURN_VOID); break;

            // ── Object ──
            case Opcode.NewObject:
                writer.Write(OP_NEW_OBJECT);
                writer.WriteInt32(instr.Operand0);
                writer.WriteInt32(instr.Operand1);
                break;
            case Opcode.Dup: writer.Write(OP_DUP); break;
            case Opcode.Pop: writer.Write(OP_POP); break;

            // ── String ──
            case Opcode.ConcatString: writer.Write(OP_CONCAT_STRING); break;

            // ── Async ──
            case Opcode.Await: writer.Write(OP_AWAIT); break;

            // ── Convert ──
            case Opcode.Conv_I32_I64: writer.Write(OP_CONV_I32_I64); break;
            case Opcode.Conv_I64_I32: writer.Write(OP_CONV_I64_I32); break;
            case Opcode.Conv_I32_F64: writer.Write(OP_CONV_I32_F64); break;
            case Opcode.Conv_F64_I32: writer.Write(OP_CONV_F64_I32); break;
            case Opcode.Conv_I32_F32: writer.Write(OP_CONV_I32_F32); break;
            case Opcode.Conv_F32_I32: writer.Write(OP_CONV_F32_I32); break;
            case Opcode.Conv_U64_I64: writer.Write(OP_CONV_U64_I64); break;
            case Opcode.Conv_I64_U64: writer.Write(OP_CONV_I64_U64); break;
            case Opcode.Conv_U64_F64: writer.Write(OP_CONV_U64_F64); break;
            case Opcode.Conv_F64_U64: writer.Write(OP_CONV_F64_U64); break;
            case Opcode.Conv_U64_I32: writer.Write(OP_CONV_U64_I32); break;
            case Opcode.Conv_I32_U64: writer.Write(OP_CONV_I32_U64); break;

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
        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.OperandObject is string s)
                {
                    if (instr.Opcode is Opcode.LoadConst_String or
                        Opcode.Call or
                        Opcode.NewObject or
                        Opcode.LoadField or
                        Opcode.StoreField)
                    {
                        instr.Operand0 = strings.Count;
                        strings.Add(s);
                    }
                }
            }
        }
        return strings.ToArray();
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
            byte op = reader.ReadByte();

            switch (op)
            {
                case OP_BRANCH:
                case OP_BRANCH_TRUE:
                case OP_BRANCH_FALSE:
                {
                    int blockId = reader.ReadInt32();
                    if (blockStarts.TryGetValue(blockId, out int byteOffset))
                    {
                        stream.Seek(-4, SeekOrigin.Current);
                        writer.Write(byteOffset);
                    }
                    break;
                }
                case OP_LOAD_CONST_I32:
                case OP_LOAD_CONST_I64:
                case OP_LOAD_CONST_U64:
                case OP_LOAD_LOCAL:
                case OP_STORE_LOCAL:
                case OP_LOAD_ARG:
                    reader.ReadInt32();
                    break;
                case OP_LOAD_CONST_F32:
                    reader.ReadSingle();
                    break;
                case OP_LOAD_CONST_F64:
                    reader.ReadDouble();
                    break;
                case OP_LOAD_CONST_BOOL:
                    reader.ReadByte();
                    break;
                case OP_CALL:
                    reader.ReadInt32();
                    reader.ReadInt32();
                    break;
                case OP_NEW_OBJECT:
                    reader.ReadInt32();
                    reader.ReadInt32();
                    break;
                case OP_LOAD_FIELD:
                case OP_STORE_FIELD:
                    reader.ReadInt32();
                    break;
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

        public byte[] ToArray() => _stream.ToArray();
    }
}
