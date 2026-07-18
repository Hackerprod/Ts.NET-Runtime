namespace TypeSharp.VM.Bytecode;

public enum OperandKind : byte
{
    None,
    Int8,
    Int32,
    Int64,
    UInt64,
    Float32,
    Float64,
    Int32x2,
    Branch
}

public readonly struct OpcodeFormat
{
    public byte Code { get; }
    public OperandKind Operand { get; }
    public int OperandBytes { get; }
    public bool IsBranch { get; }

    public OpcodeFormat(byte code, OperandKind operand = OperandKind.None)
    {
        Code = code;
        Operand = operand;
        IsBranch = operand == OperandKind.Branch;
        OperandBytes = operand switch
        {
            OperandKind.None => 0,
            OperandKind.Int8 => 1,
            OperandKind.Int32 => 4,
            OperandKind.Int64 => 8,
            OperandKind.UInt64 => 8,
            OperandKind.Float32 => 4,
            OperandKind.Float64 => 8,
            OperandKind.Int32x2 => 8,
            OperandKind.Branch => 4,
            _ => 0
        };
    }
}

public static class Opcodes
{
    public const byte Nop                      = 0x00;
    public const byte LoadConstI32             = 0x01;
    public const byte LoadConstI64             = 0x02;
    public const byte LoadConstF32             = 0x03;
    public const byte LoadConstF64             = 0x04;
    public const byte LoadConstString          = 0x05;
    public const byte LoadConstBool            = 0x06;
    public const byte LoadConstNull            = 0x07;
    public const byte LoadConstU64             = 0x08;
    public const byte LoadConstDecimal         = 0x09;

    public const byte LoadLocal                = 0x10;
    public const byte StoreLocal               = 0x11;
    public const byte LoadArg                  = 0x12;
    public const byte LoadThis                 = 0x13;
    public const byte LoadField                = 0x14;
    public const byte StoreField               = 0x15;

    public const byte AddI32                   = 0x20;
    public const byte SubI32                   = 0x21;
    public const byte MulI32                   = 0x22;
    public const byte DivI32                   = 0x23;
    public const byte ModI32                   = 0x24;
    public const byte NegI32                   = 0x25;

    public const byte AddI64                   = 0x26;
    public const byte SubI64                   = 0x27;
    public const byte MulI64                   = 0x28;
    public const byte DivI64                   = 0x29;
    public const byte ModI64                   = 0x2A;
    public const byte NegI64                   = 0x2B;

    public const byte AddU64                   = 0x2C;
    public const byte SubU64                   = 0x2D;
    public const byte MulU64                   = 0x2E;
    public const byte DivU64                   = 0x2F;
    public const byte ModU64                   = 0x30;

    public const byte AddF64                   = 0x31;
    public const byte SubF64                   = 0x32;
    public const byte MulF64                   = 0x33;
    public const byte DivF64                   = 0x34;
    public const byte ModF64                   = 0x35;
    public const byte NegF64                   = 0x36;

    public const byte AddF32                   = 0x37;
    public const byte SubF32                   = 0x38;
    public const byte MulF32                   = 0x39;
    public const byte DivF32                   = 0x3A;
    public const byte NegF32                   = 0x3B;

    public const byte AndI32                   = 0x3C;
    public const byte OrI32                    = 0x3D;
    public const byte XorI32                   = 0x3E;
    public const byte NotI32                   = 0x3F;

    public const byte CmpEqI32                 = 0x40;
    public const byte CmpNeI32                 = 0x41;
    public const byte CmpLtI32                 = 0x42;
    public const byte CmpLeI32                 = 0x43;
    public const byte CmpGtI32                 = 0x44;
    public const byte CmpGeI32                 = 0x45;

    public const byte CmpEqI64                 = 0x46;
    public const byte CmpNeI64                 = 0x47;
    public const byte CmpLtI64                 = 0x48;
    public const byte CmpLeI64                 = 0x49;
    public const byte CmpGtI64                 = 0x4A;
    public const byte CmpGeI64                 = 0x4B;

    public const byte CmpEqU64                 = 0x4C;
    public const byte CmpNeU64                 = 0x4D;
    public const byte CmpLtU64                 = 0x4E;
    public const byte CmpLeU64                 = 0x4F;

    public const byte CmpEqF64                 = 0x50;
    public const byte CmpNeF64                 = 0x51;
    public const byte CmpLtF64                 = 0x52;
    public const byte CmpLeF64                 = 0x53;
    public const byte CmpGtF64                 = 0x54;
    public const byte CmpGeF64                 = 0x55;

    public const byte CmpEqF32                 = 0x56;
    public const byte CmpNeF32                 = 0x57;
    public const byte CmpLtF32                 = 0x58;
    public const byte CmpLeF32                 = 0x59;
    public const byte CmpGtF32                 = 0x5A;
    public const byte CmpGeF32                 = 0x5B;

    public const byte AndBool                  = 0x5C;
    public const byte OrBool                   = 0x5D;
    public const byte NotBool                  = 0x5E;

    public const byte AndI64                   = 0x60;
    public const byte OrI64                    = 0x61;
    public const byte XorI64                   = 0x62;
    public const byte NotI64                   = 0x63;
    public const byte ShlI32                   = 0x64;
    public const byte ShrI32                   = 0x65;
    public const byte ShlI64                   = 0x66;
    public const byte ShrI64                   = 0x67;

    public const byte Branch                   = 0x70;
    public const byte BranchTrue               = 0x71;
    public const byte BranchFalse              = 0x72;

    public const byte Call                     = 0x73;
    public const byte CallHost                 = 0x74;
    public const byte Return                   = 0x75;
    public const byte ReturnVoid               = 0x76;
    public const byte CallVirt                 = 0x77;

    public const byte NewObject                = 0x80;
    public const byte NewArray                 = 0x81;
    public const byte NewMap                   = 0x82;
    public const byte Dup                      = 0x83;
    public const byte Pop                      = 0x84;

    public const byte ConcatString             = 0x85;

    public const byte Await                    = 0x86;

    public const byte Throw                    = 0x87;

    public const byte ConvI32I64               = 0x90;
    public const byte ConvI64I32               = 0x91;
    public const byte ConvI32F64               = 0x92;
    public const byte ConvF64I32               = 0x93;
    public const byte ConvI32F32               = 0x94;
    public const byte ConvF32I32               = 0x95;
    public const byte ConvU64I64               = 0x96;
    public const byte ConvI64U64               = 0x97;
    public const byte ConvU64F64               = 0x98;
    public const byte ConvF64U64               = 0x99;
    public const byte ConvU64I32               = 0x9A;
    public const byte ConvI32U64               = 0x9B;
    public const byte ConvF32F64               = 0x9C;
    public const byte ConvF64F32               = 0x9D;

    public const byte TypeCheck                = 0xA0;
    public const byte NullCheck                = 0xA1;

    public const byte AddDecimal               = 0xB0;
    public const byte SubDecimal               = 0xB1;
    public const byte MulDecimal               = 0xB2;
    public const byte DivDecimal               = 0xB3;
    public const byte ModDecimal               = 0xB4;
    public const byte NegDecimal               = 0xB5;

    public const byte CmpEqDecimal             = 0xB6;
    public const byte CmpNeDecimal             = 0xB7;
    public const byte CmpLtDecimal             = 0xB8;
    public const byte CmpLeDecimal             = 0xB9;
    public const byte CmpGtDecimal             = 0xBA;
    public const byte CmpGeDecimal             = 0xBB;
}

public static class OpcodeFormats
{
    private static readonly Dictionary<byte, OpcodeFormat> _table = BuildTable();

    public static OpcodeFormat Get(byte opcode) =>
        _table.TryGetValue(opcode, out var fmt) ? fmt : new OpcodeFormat(opcode, OperandKind.None);

    public static bool TryGet(byte opcode, out OpcodeFormat format) => _table.TryGetValue(opcode, out format);

    public static int InstructionSize(byte opcode) => 1 + Get(opcode).OperandBytes;

    private static Dictionary<byte, OpcodeFormat> BuildTable()
    {
        var t = new Dictionary<byte, OpcodeFormat>();

        void Reg(byte code, OperandKind op = OperandKind.None) => t[code] = new OpcodeFormat(code, op);

        // ── Load constants ──
        Reg(0x00);                     // NOP
        Reg(0x01, OperandKind.Int32);  // LOAD_CONST_I32
        Reg(0x02, OperandKind.Int64);  // LOAD_CONST_I64 ← was broken (ReadInt32)
        Reg(0x03, OperandKind.Float32);// LOAD_CONST_F32
        Reg(0x04, OperandKind.Float64);// LOAD_CONST_F64
        Reg(0x05, OperandKind.Int32);  // LOAD_CONST_STRING (index)
        Reg(0x06, OperandKind.Int8);   // LOAD_CONST_BOOL
        Reg(0x07);                     // LOAD_CONST_NULL
        Reg(0x08, OperandKind.UInt64); // LOAD_CONST_U64 ← was broken (ReadInt32)
        Reg(0x09, OperandKind.Int32);  // LOAD_CONST_DECIMAL (index)

        // ── Variables ──
        Reg(0x10, OperandKind.Int32);  // LOAD_LOCAL
        Reg(0x11, OperandKind.Int32);  // STORE_LOCAL
        Reg(0x12, OperandKind.Int32);  // LOAD_ARG
        Reg(0x13);                     // LOAD_THIS
        Reg(0x14, OperandKind.Int32);  // LOAD_FIELD (string index)
        Reg(0x15, OperandKind.Int32);  // STORE_FIELD (string index)

        // ── I32 arithmetic ──
        Reg(0x20); Reg(0x21); Reg(0x22); Reg(0x23); Reg(0x24); Reg(0x25);

        // ── I64 arithmetic ──
        Reg(0x26); Reg(0x27); Reg(0x28); Reg(0x29); Reg(0x2A); Reg(0x2B);

        // ── U64 arithmetic ──
        Reg(0x2C); Reg(0x2D); Reg(0x2E); Reg(0x2F); Reg(0x30);

        // ── F64 arithmetic ──
        Reg(0x31); Reg(0x32); Reg(0x33); Reg(0x34); Reg(0x35); Reg(0x36);

        // ── F32 arithmetic ──
        Reg(0x37); Reg(0x38); Reg(0x39); Reg(0x3A); Reg(0x3B);

        // ── I32 bitwise ──
        Reg(0x3C); Reg(0x3D); Reg(0x3E); Reg(0x3F);

        // ── I32 comparison ──
        Reg(0x40); Reg(0x41); Reg(0x42); Reg(0x43); Reg(0x44); Reg(0x45);

        // ── I64 comparison ──
        Reg(0x46); Reg(0x47); Reg(0x48); Reg(0x49); Reg(0x4A); Reg(0x4B);

        // ── U64 comparison ──
        Reg(0x4C); Reg(0x4D); Reg(0x4E); Reg(0x4F);

        // ── F64 comparison ──
        Reg(0x50); Reg(0x51); Reg(0x52); Reg(0x53); Reg(0x54); Reg(0x55);

        // ── F32 comparison ──
        Reg(0x56); Reg(0x57); Reg(0x58); Reg(0x59); Reg(0x5A); Reg(0x5B);

        // ── Logical ──
        Reg(0x5C); Reg(0x5D); Reg(0x5E);

        // ── I64 bitwise + shifts ──
        Reg(0x60); Reg(0x61); Reg(0x62); Reg(0x63);
        Reg(0x64); Reg(0x65); Reg(0x66); Reg(0x67);

        // ── Control flow ──
        Reg(0x70, OperandKind.Branch);  // BRANCH
        Reg(0x71, OperandKind.Branch);  // BRANCH_TRUE
        Reg(0x72, OperandKind.Branch);  // BRANCH_FALSE

        // ── Functions ──
        Reg(0x73, OperandKind.Int32x2); // CALL (funcIdx + argCount)
        Reg(0x74, OperandKind.Int32x2); // CALL_HOST
        Reg(0x75);                       // RETURN
        Reg(0x76);                       // RETURN_VOID
        Reg(0x77, OperandKind.Int32x2); // CALL_VIRT

        // ── Object ──
        Reg(0x80, OperandKind.Int32x2); // NEW_OBJECT (typeIdx + argCount)
        Reg(0x81, OperandKind.Int32);   // NEW_ARRAY (capacity)
        Reg(0x82);                       // NEW_MAP
        Reg(0x83);                       // DUP
        Reg(0x84);                       // POP

        // ── String ──
        Reg(0x85);                       // CONCAT_STRING

        // ── Async ──
        Reg(0x86);                       // AWAIT

        // ── Exception ──
        Reg(0x87);                       // THROW

        // ── Convert ──
        Reg(0x90); Reg(0x91); Reg(0x92); Reg(0x93);
        Reg(0x94); Reg(0x95); Reg(0x96); Reg(0x97);
        Reg(0x98); Reg(0x99); Reg(0x9A); Reg(0x9B);
        Reg(0x9C); Reg(0x9D);

        // ── Utilities ──
        Reg(0xA0, OperandKind.Int32);   // TYPE_CHECK (type index)
        Reg(0xA1);                       // NULL_CHECK

        // ── Decimal arithmetic ──
        Reg(0xB0); Reg(0xB1); Reg(0xB2); Reg(0xB3); Reg(0xB4); Reg(0xB5);

        // ── Decimal comparison ──
        Reg(0xB6); Reg(0xB7); Reg(0xB8); Reg(0xB9); Reg(0xBA); Reg(0xBB);

        return t;
    }
}
