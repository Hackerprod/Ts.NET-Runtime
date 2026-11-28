using System.Buffers.Binary;

namespace TypeSharp.VM.Bytecode;

public sealed class BytecodeVerificationException : Exception
{
    public BytecodeVerificationException(string message) : base(message) { }
}

public static partial class BytecodeVerifier
{
    public static void Verify(BytecodeModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var function in module.Functions)
        {
            if (!names.Add(function.Name))
                Fail(function, "duplicate function name");
            if (function.ParameterCount < 0 || function.LocalCount < 0)
                Fail(function, "invalid parameter/local counts");
            if (function.RestParameterIndex < -1 || function.RestParameterIndex >= function.ParameterCount)
                Fail(function, "invalid rest parameter index");
            if (function.RestParameterIndex >= 0 && function.RestParameterIndex != function.ParameterCount - 1)
                Fail(function, "rest parameter must be the last parameter");
            VerifyFunction(function);
        }
    }

    public static void VerifyFunction(BytecodeFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        var code = function.Instructions;
        var boundaries = new HashSet<int> { 0 };
        var branches = new List<int>();
        bool structured = code.Length > 0 && code[0] == Opcodes.GStart;
        bool generatorCalls = false;
        int ip = 0;

        while (ip < code.Length)
        {
            int instructionOffset = ip;
            byte opcode = code[ip++];
            if (!OpcodeFormats.TryGet(opcode, out var format))
                Fail(function, $"Unknown opcode 0x{opcode:X2} at {instructionOffset}");
            if (code.Length - ip < format.OperandBytes)
                Fail(function, $"truncated operand at {instructionOffset}");

            if (opcode == Opcodes.GStart && (instructionOffset != 0 || !function.IsGenerator || function.IsAsync))
                Fail(function, "GStart requires a synchronous generator at offset zero");
            if (opcode is >= Opcodes.GBlock and <= Opcodes.GEndFinally && !structured)
                Fail(function, "generator region instruction without GStart");
            generatorCalls |= opcode == Opcodes.GCallMember;
            ValidateOperands(function, opcode, code, ip, branches);
            ip += format.OperandBytes;
            boundaries.Add(ip);
        }

        foreach (var target in branches)
        {
            if (!boundaries.Contains(target))
                Fail(function, $"branch target {target} is not an instruction boundary");
        }
        if (structured)
            VerifyStructuredGenerator(function);
        else if (generatorCalls)
            VerifyGeneratorMemberStack(function);
    }

    // Called only after full verification; step by actual instruction width,
    // never look for magic bytes inside an operand.
    internal static bool UsesGeneratorExtensions(BytecodeModule module)
    {
        foreach (var function in module.Functions)
        {
            var code = function.Instructions;
            for (int offset = 0; offset < code.Length; offset += OpcodeFormats.InstructionSize(code[offset]))
                if (code[offset] is >= Opcodes.GStart and <= Opcodes.GCallMember)
                    return true;
        }
        return false;
    }

    private static void ValidateOperands(BytecodeFunction function, byte opcode, byte[] code, int offset,
        List<int> branches)
    {
        switch (opcode)
        {
            case Opcodes.LoadLocal:
            case Opcodes.StoreLocal:
            {
                int first = ReadInt32(code, offset);
                if (first < 0 || first >= function.LocalCount + function.ParameterCount) Fail(function, $"local index {first} is out of range");
                break;
            }
            case Opcodes.LoadArg:
            {
                int first = ReadInt32(code, offset);
                if (first < 0 || first >= function.ParameterCount) Fail(function, $"argument index {first} is out of range");
                break;
            }
            case Opcodes.LoadConstString:
            case Opcodes.LoadConstBigInt:
            case Opcodes.LoadField:
            case Opcodes.HasFieldLiteral:
            case Opcodes.StoreField:
            case Opcodes.Call:
            case Opcodes.CallHost:
            case Opcodes.CallVirt:
            case Opcodes.NewObject:
            case Opcodes.LoadFunc:
            case Opcodes.MakeClosure:
            {
                int first = ReadInt32(code, offset);
                if (first < 0 || first >= function.StringConstants.Length) Fail(function, $"string index {first} is out of range");
                if (opcode is Opcodes.Call or Opcodes.CallHost or Opcodes.CallVirt or Opcodes.NewObject && ReadInt32(code, offset + 4) < 0)
                    Fail(function, "negative argument count");
                break;
            }
            case Opcodes.LoadConstDecimal:
            {
                int first = ReadInt32(code, offset);
                if (first < 0 || first >= function.DecimalConstants.Length) Fail(function, $"decimal index {first} is out of range");
                break;
            }
            case Opcodes.NewArray:
            {
                int first = ReadInt32(code, offset);
                if (first < 0) Fail(function, "negative array capacity");
                break;
            }
            case Opcodes.Branch:
            case Opcodes.BranchTrue:
            case Opcodes.BranchFalse:
            case Opcodes.EnterTry:
            {
                int first = ReadInt32(code, offset);
                if (first < 0 || first > function.Instructions.Length) Fail(function, $"branch target {first} is out of range");
                branches.Add(first);
                break;
            }
            case Opcodes.LoadConstBool:
                if (code[offset] > 1) Fail(function, "boolean literal must be 0 or 1");
                break;
            case Opcodes.DeleteField:
            {
                int first = ReadInt32(code, offset);
                if (first < 0 || first >= function.StringConstants.Length) Fail(function, $"string index {first} is out of range");
                break;
            }
        }
    }

    private static int ReadInt32(byte[] code, int offset) => BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(offset, 4));

    private static void Fail(BytecodeFunction function, string message) =>
        throw new BytecodeVerificationException($"Bytecode verification failed for '{function.Name}': {message}");
}
