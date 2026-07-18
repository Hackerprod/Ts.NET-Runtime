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

public static class BytecodeCompiler
{
    private const byte OP_NOP = 0x00;
    private const byte OP_LOAD_CONST_I32 = 0x01;
    private const byte OP_LOAD_CONST_I64 = 0x02;
    private const byte OP_LOAD_CONST_F32 = 0x03;
    private const byte OP_LOAD_CONST_F64 = 0x04;
    private const byte OP_LOAD_CONST_STRING = 0x05;
    private const byte OP_LOAD_CONST_BOOL = 0x06;
    private const byte OP_LOAD_CONST_NULL = 0x07;
    private const byte OP_LOAD_LOCAL = 0x10;
    private const byte OP_STORE_LOCAL = 0x11;
    private const byte OP_LOAD_ARG = 0x12;
    private const byte OP_LOAD_THIS = 0x13;
    private const byte OP_LOAD_FIELD = 0x14;
    private const byte OP_STORE_FIELD = 0x15;
    private const byte OP_ADD_I32 = 0x20;
    private const byte OP_SUB_I32 = 0x21;
    private const byte OP_MUL_I32 = 0x22;
    private const byte OP_DIV_I32 = 0x23;
    private const byte OP_MOD_I32 = 0x24;
    private const byte OP_NEG_I32 = 0x25;
    private const byte OP_ADD_I64 = 0x26;
    private const byte OP_SUB_I64 = 0x27;
    private const byte OP_MUL_I64 = 0x28;
    private const byte OP_DIV_I64 = 0x29;
    private const byte OP_ADD_F64 = 0x2A;
    private const byte OP_SUB_F64 = 0x2B;
    private const byte OP_MUL_F64 = 0x2C;
    private const byte OP_DIV_F64 = 0x2D;
    private const byte OP_ADD_F32 = 0x2E;
    private const byte OP_SUB_F32 = 0x2F;
    private const byte OP_MUL_F32 = 0x30;
    private const byte OP_DIV_F32 = 0x31;
    private const byte OP_MOD_F64 = 0x32;
    private const byte OP_NEG_F64 = 0x33;
    private const byte OP_AND_I32 = 0x34;
    private const byte OP_OR_I32 = 0x35;
    private const byte OP_XOR_I32 = 0x36;
    private const byte OP_NOT_I32 = 0x37;
    private const byte OP_SHL_I32 = 0x38;
    private const byte OP_SHR_I32 = 0x39;
    private const byte OP_AND_I64 = 0x3A;
    private const byte OP_OR_I64 = 0x3B;
    private const byte OP_CMP_EQ_I32 = 0x40;
    private const byte OP_CMP_NE_I32 = 0x41;
    private const byte OP_CMP_LT_I32 = 0x42;
    private const byte OP_CMP_LE_I32 = 0x43;
    private const byte OP_CMP_GT_I32 = 0x44;
    private const byte OP_CMP_GE_I32 = 0x45;
    private const byte OP_CMP_EQ_I64 = 0x46;
    private const byte OP_CMP_NE_I64 = 0x47;
    private const byte OP_CMP_EQ_F64 = 0x48;
    private const byte OP_CMP_NE_F64 = 0x49;
    private const byte OP_CMP_LT_F64 = 0x4A;
    private const byte OP_CMP_LE_F64 = 0x4B;
    private const byte OP_CMP_GT_F64 = 0x4C;
    private const byte OP_CMP_GE_F64 = 0x4D;
    private const byte OP_AND_BOOL = 0x50;
    private const byte OP_OR_BOOL = 0x51;
    private const byte OP_NOT_BOOL = 0x52;
    private const byte OP_BRANCH = 0x60;
    private const byte OP_BRANCH_TRUE = 0x61;
    private const byte OP_BRANCH_FALSE = 0x62;
    private const byte OP_CALL = 0x70;
    private const byte OP_CALL_HOST = 0x71;
    private const byte OP_RETURN = 0x80;
    private const byte OP_RETURN_VOID = 0x81;
    private const byte OP_NEW_OBJECT = 0x90;
    private const byte OP_DUP = 0x91;
    private const byte OP_CONCAT_STRING = 0xA0;
    private const byte OP_AWAIT = 0xB0;
    private const byte OP_THROW = 0xC0;
    private const byte OP_POP = 0xD0;
    private const byte OP_CONV_I32_I64 = 0xE0;
    private const byte OP_CONV_I64_I32 = 0xE1;
    private const byte OP_CONV_I32_F64 = 0xE2;
    private const byte OP_CONV_F64_I32 = 0xE3;

    public static BytecodeModule Compile(TypeSharp.IR.ModuleIR module)
    {
        var functions = new BytecodeFunction[module.Functions.Count];

        for (int i = 0; i < module.Functions.Count; i++)
        {
            functions[i] = CompileFunction(module.Functions[i]);
        }

        return new BytecodeModule(module.Name, functions);
    }

    public static BytecodeFunction CompileFunction(TypeSharp.IR.FunctionIR function)
    {
        var writer = new BytecodeWriter();

        var stringConstants = CollectStringConstants(function);
        var intConstants = CollectIntConstants(function);
        var doubleConstants = CollectDoubleConstants(function);

        var blockStarts = new Dictionary<int, int>();

        foreach (var block in function.Blocks)
        {
            blockStarts[block.Id] = writer.Position;
            foreach (var instr in block.Instructions)
            {
                EmitInstruction(writer, instr);
            }
        }

        // Patch branch targets
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

    private static void EmitInstruction(BytecodeWriter writer, TypeSharp.IR.Instruction instr)
    {
        switch (instr.Opcode)
        {
            case TypeSharp.IR.Opcode.Nop: writer.Write(OP_NOP); break;
            case TypeSharp.IR.Opcode.LoadConst_I32:
                writer.Write(OP_LOAD_CONST_I32);
                writer.WriteInt32(instr.Operand0);
                break;
            case TypeSharp.IR.Opcode.LoadConst_I64:
                writer.Write(OP_LOAD_CONST_I64);
                writer.WriteInt64(instr.Operand0);
                break;
            case TypeSharp.IR.Opcode.LoadConst_F32:
                writer.Write(OP_LOAD_CONST_F32);
                writer.WriteFloat(Convert.ToSingle(instr.OperandObject));
                break;
            case TypeSharp.IR.Opcode.LoadConst_F64:
                writer.Write(OP_LOAD_CONST_F64);
                writer.WriteDouble(Convert.ToDouble(instr.OperandObject));
                break;
            case TypeSharp.IR.Opcode.LoadConst_String:
                writer.Write(OP_LOAD_CONST_STRING);
                writer.WriteInt32(instr.Operand0);
                break;
            case TypeSharp.IR.Opcode.LoadConst_Bool:
                writer.Write(OP_LOAD_CONST_BOOL);
                writer.Write(instr.Operand0 != 0 ? (byte)1 : (byte)0);
                break;
            case TypeSharp.IR.Opcode.LoadConst_Null:
                writer.Write(OP_LOAD_CONST_NULL);
                break;
            case TypeSharp.IR.Opcode.LoadLocal:
                writer.Write(OP_LOAD_LOCAL);
                writer.WriteInt32(instr.Operand0);
                break;
            case TypeSharp.IR.Opcode.StoreLocal:
                writer.Write(OP_STORE_LOCAL);
                writer.WriteInt32(instr.Operand0);
                break;
            case TypeSharp.IR.Opcode.LoadArg:
                writer.Write(OP_LOAD_ARG);
                writer.WriteInt32(instr.Operand0);
                break;
            case TypeSharp.IR.Opcode.LoadThis:
                writer.Write(OP_LOAD_THIS);
                break;
            case TypeSharp.IR.Opcode.LoadField:
            {
                writer.Write(OP_LOAD_FIELD);
                writer.WriteInt32(instr.Operand0);
                break;
            }
            case TypeSharp.IR.Opcode.StoreField:
            {
                writer.Write(OP_STORE_FIELD);
                writer.WriteInt32(instr.Operand0);
                break;
            }
            case TypeSharp.IR.Opcode.Add_I32: writer.Write(OP_ADD_I32); break;
            case TypeSharp.IR.Opcode.Sub_I32: writer.Write(OP_SUB_I32); break;
            case TypeSharp.IR.Opcode.Mul_I32: writer.Write(OP_MUL_I32); break;
            case TypeSharp.IR.Opcode.Div_I32: writer.Write(OP_DIV_I32); break;
            case TypeSharp.IR.Opcode.Mod_I32: writer.Write(OP_MOD_I32); break;
            case TypeSharp.IR.Opcode.Neg_I32: writer.Write(OP_NEG_I32); break;
            case TypeSharp.IR.Opcode.Add_I64: writer.Write(OP_ADD_I64); break;
            case TypeSharp.IR.Opcode.Sub_I64: writer.Write(OP_SUB_I64); break;
            case TypeSharp.IR.Opcode.Mul_I64: writer.Write(OP_MUL_I64); break;
            case TypeSharp.IR.Opcode.Div_I64: writer.Write(OP_DIV_I64); break;
            case TypeSharp.IR.Opcode.Add_F64: writer.Write(OP_ADD_F64); break;
            case TypeSharp.IR.Opcode.Sub_F64: writer.Write(OP_SUB_F64); break;
            case TypeSharp.IR.Opcode.Mul_F64: writer.Write(OP_MUL_F64); break;
            case TypeSharp.IR.Opcode.Div_F64: writer.Write(OP_DIV_F64); break;
            case TypeSharp.IR.Opcode.Mod_F64: writer.Write(OP_MOD_F64); break;
            case TypeSharp.IR.Opcode.Neg_F64: writer.Write(OP_NEG_F64); break;
            case TypeSharp.IR.Opcode.And_Bool: writer.Write(OP_AND_BOOL); break;
            case TypeSharp.IR.Opcode.Or_Bool: writer.Write(OP_OR_BOOL); break;
            case TypeSharp.IR.Opcode.Not_Bool: writer.Write(OP_NOT_BOOL); break;
            case TypeSharp.IR.Opcode.CmpEq_I32: writer.Write(OP_CMP_EQ_I32); break;
            case TypeSharp.IR.Opcode.CmpNe_I32: writer.Write(OP_CMP_NE_I32); break;
            case TypeSharp.IR.Opcode.CmpLt_I32: writer.Write(OP_CMP_LT_I32); break;
            case TypeSharp.IR.Opcode.CmpLe_I32: writer.Write(OP_CMP_LE_I32); break;
            case TypeSharp.IR.Opcode.CmpGt_I32: writer.Write(OP_CMP_GT_I32); break;
            case TypeSharp.IR.Opcode.CmpGe_I32: writer.Write(OP_CMP_GE_I32); break;
            case TypeSharp.IR.Opcode.CmpEq_I64: writer.Write(OP_CMP_EQ_I64); break;
            case TypeSharp.IR.Opcode.CmpNe_I64: writer.Write(OP_CMP_NE_I64); break;
            case TypeSharp.IR.Opcode.CmpEq_F64: writer.Write(OP_CMP_EQ_F64); break;
            case TypeSharp.IR.Opcode.CmpNe_F64: writer.Write(OP_CMP_NE_F64); break;
            case TypeSharp.IR.Opcode.CmpLt_F64: writer.Write(OP_CMP_LT_F64); break;
            case TypeSharp.IR.Opcode.CmpLe_F64: writer.Write(OP_CMP_LE_F64); break;
            case TypeSharp.IR.Opcode.CmpGt_F64: writer.Write(OP_CMP_GT_F64); break;
            case TypeSharp.IR.Opcode.CmpGe_F64: writer.Write(OP_CMP_GE_F64); break;
            case TypeSharp.IR.Opcode.Branch:
                writer.Write(OP_BRANCH);
                writer.WriteInt32(instr.Operand0);
                break;
            case TypeSharp.IR.Opcode.BranchTrue:
                writer.Write(OP_BRANCH_TRUE);
                writer.WriteInt32(instr.Operand0);
                break;
            case TypeSharp.IR.Opcode.BranchFalse:
                writer.Write(OP_BRANCH_FALSE);
                writer.WriteInt32(instr.Operand0);
                break;
            case TypeSharp.IR.Opcode.Return: writer.Write(OP_RETURN); break;
            case TypeSharp.IR.Opcode.ReturnVoid: writer.Write(OP_RETURN_VOID); break;
            case TypeSharp.IR.Opcode.Call:
            {
                writer.Write(OP_CALL);
                writer.WriteInt32(instr.Operand0);
                writer.WriteInt32(instr.Operand1);
                break;
            }
            case TypeSharp.IR.Opcode.ConcatString:
                writer.Write(OP_CONCAT_STRING);
                break;
            case TypeSharp.IR.Opcode.Await:
                writer.Write(OP_AWAIT);
                break;
            case TypeSharp.IR.Opcode.Pop:
                writer.Write(OP_POP);
                break;
            case TypeSharp.IR.Opcode.NewObject:
            {
                writer.Write(OP_NEW_OBJECT);
                writer.WriteInt32(instr.Operand0);
                writer.WriteInt32(instr.Operand1);
                break;
            }
            case TypeSharp.IR.Opcode.Dup:
                writer.Write(OP_DUP);
                break;
            default:
                writer.Write(OP_NOP);
                break;
        }
    }

    private static string[] CollectStringConstants(TypeSharp.IR.FunctionIR function)
    {
        var strings = new List<string>();
        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.OperandObject is string s)
                {
                    if (instr.Opcode == TypeSharp.IR.Opcode.LoadConst_String ||
                        instr.Opcode == TypeSharp.IR.Opcode.Call ||
                        instr.Opcode == TypeSharp.IR.Opcode.NewObject ||
                        instr.Opcode == TypeSharp.IR.Opcode.LoadField ||
                        instr.Opcode == TypeSharp.IR.Opcode.StoreField)
                    {
                        instr.Operand0 = strings.Count;
                        strings.Add(s);
                    }
                }
            }
        }
        return strings.ToArray();
    }

    private static long[] CollectIntConstants(TypeSharp.IR.FunctionIR function)
    {
        return Array.Empty<long>();
    }

    private static double[] CollectDoubleConstants(TypeSharp.IR.FunctionIR function)
    {
        return Array.Empty<double>();
    }

    private static void PatchBranchTargets(byte[] bytecode, List<BasicBlock> blocks, Dictionary<int, int> blockStarts)
    {
        var stream = new MemoryStream(bytecode);
        var reader = new BinaryReader(stream);
        var writer = new BinaryWriter(stream);

        while (stream.Position < stream.Length)
        {
            long pos = stream.Position;
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
                case OP_CALL_HOST:
                    reader.ReadInt32(); // func name index
                    reader.ReadInt32(); // arg count
                    break;
                case OP_NEW_OBJECT:
                    reader.ReadInt32(); // type name index
                    reader.ReadInt32(); // arg count
                    break;
                case OP_LOAD_FIELD:
                case OP_STORE_FIELD:
                    reader.ReadInt32(); // field name index
                    break;
            }
        }
    }

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
        public void WriteFloat(float value) => _writer.Write(value);
        public void WriteDouble(double value) => _writer.Write(value);

        public byte[] ToArray() => _stream.ToArray();
    }
}
