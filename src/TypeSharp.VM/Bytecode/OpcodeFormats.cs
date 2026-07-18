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

public static class OpcodeFormats
{
    private static readonly Dictionary<byte, OpcodeFormat> _table = BuildTable();

    public static OpcodeFormat Get(byte opcode) =>
        _table.TryGetValue(opcode, out var fmt) ? fmt : new OpcodeFormat(opcode, OperandKind.None);

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
