using TypeSharp.Semantics.TypeSystem;

namespace TypeSharp.IR;

public enum Opcode
{
    // Arithmetic
    Add_I32, Add_I64, Add_U64, Add_F32, Add_F64, Add_Decimal,
    Sub_I32, Sub_I64, Sub_U64, Sub_F32, Sub_F64, Sub_Decimal,
    Mul_I32, Mul_I64, Mul_U64, Mul_F32, Mul_F64, Mul_Decimal,
    Div_I32, Div_I64, Div_U64, Div_F32, Div_F64, Div_Decimal,
    Mod_I32, Mod_I64, Mod_U64, Mod_F32, Mod_F64, Mod_Decimal,
    Neg_I32, Neg_I64, Neg_F32, Neg_F64, Neg_Decimal,

    // Bitwise
    And_I32, And_I64,
    Or_I32, Or_I64,
    Xor_I32, Xor_I64,
    Not_I32, Not_I64,
    Shl_I32, Shl_I64,
    Shr_I32, Shr_I64,

    // Comparison
    CmpEq_I32, CmpEq_I64, CmpEq_U64, CmpEq_F32, CmpEq_F64, CmpEq_Decimal,
    CmpNe_I32, CmpNe_I64, CmpNe_U64, CmpNe_F32, CmpNe_F64, CmpNe_Decimal,
    CmpLt_I32, CmpLt_I64, CmpLt_U64, CmpLt_F32, CmpLt_F64, CmpLt_Decimal,
    CmpLe_I32, CmpLe_I64, CmpLe_U64, CmpLe_F32, CmpLe_F64, CmpLe_Decimal,
    CmpGt_I32, CmpGt_I64, CmpGt_U64, CmpGt_F32, CmpGt_F64, CmpGt_Decimal,
    CmpGe_I32, CmpGe_I64, CmpGe_U64, CmpGe_F32, CmpGe_F64, CmpGe_Decimal,

    // Logical
    And_Bool, Or_Bool, Not_Bool,

    // Loads
    LoadConst_I32,
    LoadConst_I64,
    LoadConst_U64,
    LoadConst_F32,
    LoadConst_F64,
    LoadConst_Decimal,
    LoadConst_String,
    LoadConst_Bool,
    LoadConst_Null,

    // Variables
    LoadLocal,
    StoreLocal,
    LoadArg,
    LoadThis,
    LoadField,
    StoreField,

    // Control flow
    Branch,
    BranchTrue,
    BranchFalse,

    // Functions
    Call,
    CallVirt,
    Return,
    ReturnVoid,

    // Object
    NewObject,
    NewArray,
    NewMap,
    LoadElement,
    StoreElement,

    // Exception regions
    EnterTry,
    LeaveTry,

    // Cast/Convert
    Conv_I32_I64,
    Conv_I64_I32,
    Conv_I32_F64,
    Conv_F64_I32,
    Conv_I32_F32,
    Conv_F32_I32,
    Conv_U64_I64,
    Conv_I64_U64,
    Conv_U64_F64,
    Conv_F64_U64,
    Conv_U64_I32,
    Conv_I32_U64,
    Conv_F32_F64,
    Conv_F64_F32,

    // Async
    Await,

    // Special
    Nop,
    Throw,
    Pop,
    Dup,

    // String
    ConcatString,

    // Utilities
    TypeCheck,
    NullCheck,
}

public sealed class Instruction
{
    public Opcode Opcode { get; }
    public int Operand0 { get; set; }
    public int Operand1 { get; set; }
    public object? OperandObject { get; set; }
    public int Label { get; set; } = -1;

    public Instruction(Opcode opcode, int operand0 = 0, int operand1 = 0, object? operandObject = null)
    {
        Opcode = opcode;
        Operand0 = operand0;
        Operand1 = operand1;
        OperandObject = operandObject;
    }

    public override string ToString()
    {
        var operands = Opcode switch
        {
            Opcode.LoadConst_String => $"\"{OperandObject}\"",
            Opcode.LoadConst_Bool => Operand0 == 1 ? "true" : "false",
            Opcode.LoadConst_Null => "null",
            Opcode.LoadLocal or Opcode.StoreLocal or Opcode.LoadArg => $"v{Operand0}",
            Opcode.Branch or Opcode.BranchTrue or Opcode.BranchFalse => $"L{Operand0}",
            Opcode.Call or Opcode.CallVirt => $"method#{Operand0} args={Operand1}",
            Opcode.NewObject => $"type#{Operand0} args={Operand1}",
            Opcode.LoadField or Opcode.StoreField => $"field#{Operand0}",
            _ when Operand0 != 0 || Operand1 != 0 => $"{Operand0}, {Operand1}",
            _ => ""
        };

        return string.IsNullOrEmpty(operands) ? Opcode.ToString() : $"{Opcode} {operands}";
    }
}

public sealed class BasicBlock
{
    public int Id { get; }
    public List<Instruction> Instructions { get; } = new();
    public List<int> Predecessors { get; } = new();
    public List<int> Successors { get; } = new();

    public BasicBlock(int id)
    {
        Id = id;
    }

    public Instruction LastInstruction => Instructions[^1];

    public bool EndsInBranch => Instructions.Count > 0 &&
        (LastInstruction.Opcode is Opcode.Branch or Opcode.Return or Opcode.ReturnVoid or Opcode.Throw);
}

public sealed class FunctionIR
{
    public string Name { get; }
    public List<ParameterInfo> Parameters { get; }
    public TsType ReturnType { get; }
    public List<BasicBlock> Blocks { get; } = new();
    public int LocalCount { get; set; }
    public bool IsAsync { get; set; }

    public FunctionIR(string name, TsType returnType, List<ParameterInfo> parameters)
    {
        Name = name;
        ReturnType = returnType;
        Parameters = parameters;
    }

    public BasicBlock CreateBlock()
    {
        var block = new BasicBlock(Blocks.Count);
        Blocks.Add(block);
        return block;
    }
}

public sealed class ParameterInfo
{
    public string Name { get; }
    public TsType Type { get; }

    public ParameterInfo(string name, TsType type)
    {
        Name = name;
        Type = type;
    }
}

public sealed class ModuleIR
{
    public string Name { get; }
    public List<FunctionIR> Functions { get; } = new();
    public List<TsType> Types { get; } = new();
    public Dictionary<string, int> FunctionIndex { get; } = new();

    public ModuleIR(string name)
    {
        Name = name;
    }

    public int AddFunction(FunctionIR function)
    {
        int index = Functions.Count;
        Functions.Add(function);
        FunctionIndex[function.Name] = index;
        return index;
    }
}
