using System.Buffers.Binary;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.VM.Tests;

public class LiteralFieldPresenceBytecodeTests
{
    private static BytecodeModule Compile(string source)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(binder.Diagnostics.GetErrors());
        return BytecodeCompiler.Compile(new IRGenerator().Generate(bound));
    }

    [Fact]
    public void InOperatorRoundTripsDistinctLiteralKeys()
    {
        const string source = """
            function main(): bool {
                let o = { x: undefined, xy: 0 };
                if ('x' in o) { return 'X' in o; }
                return true;
            }
            """;
        var module = Compile(source);
        var function = Assert.Single(module.Functions);
        Assert.Equal((byte)0x18, Opcodes.HasFieldLiteral);
        Assert.Equal(OperandKind.Int32, OpcodeFormats.Get(Opcodes.HasFieldLiteral).Operand);
        Assert.Equal(5, OpcodeFormats.InstructionSize(Opcodes.HasFieldLiteral));
        var keys = new List<string>();
        var code = function.Instructions;
        for (int ip = 0; ip < code.Length; ip += OpcodeFormats.InstructionSize(code[ip]))
        {
            if (code[ip] != Opcodes.HasFieldLiteral) continue;
            int index = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(ip + 1, 4));
            Assert.InRange(index, 0, function.StringConstants.Length - 1);
            keys.Add(function.StringConstants[index]);
        }
        Assert.Equal(new[] { "x", "X" }, keys);

        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, module);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(restored, "main");
        Assert.False(Assert.IsType<TsBoolValue>(result).Value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)] // equal to the size of the one-entry table
    public void InOperatorVerifierRejectsInvalidStringIndex(int index)
    {
        var code = new byte[] { Opcodes.LoadArg, 0, 0, 0, 0, Opcodes.HasFieldLiteral, 0, 0, 0, 0, Opcodes.Return };
        BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(6, 4), index);
        var ex = Assert.Throws<BytecodeVerificationException>(() => BytecodeVerifier.VerifyFunction(Function(code)));
        Assert.Contains($"string index {index} is out of range", ex.Message);
    }

    [Fact]
    public void InOperatorVerifierRejectsTruncatedOperand()
    {
        var code = new byte[] { Opcodes.LoadArg, 0, 0, 0, 0, Opcodes.HasFieldLiteral, 0, 0, 0 };
        var ex = Assert.Throws<BytecodeVerificationException>(() => BytecodeVerifier.VerifyFunction(Function(code)));
        Assert.Contains("truncated operand at 5", ex.Message);
    }

    [Fact]
    public void InOperatorReadsHistoricalTsbcWithoutReinterpretingIt()
    {
        // Fixed TSBC v2: LOAD_ARG 0; LOAD_FIELD 0; VOID; STRICT_NE; RETURN.
        // StringConstants contains "x". The old lowering reports true for a missing key.
        using var stream = new MemoryStream(Convert.FromBase64String(
            "VFNCQwIAAAAGbGVnYWN5AQAAAARtYWluAQAAAAAAAAAAAP////8NAAAAEgAAAAAUAAAAAAundQEAAAABeAAAAAAAAAAAAAAAAA=="));
        var restored = BytecodeSerializer.Deserialize(stream);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var args = new[] { TsValue.FromObject(new TsHeap().AllocateObject("Object")) };
        Assert.True(Assert.IsType<TsBoolValue>(interpreter.Execute(restored, "main", args)).Value);

        var recompiled = Compile("function main(o: any): bool { return 'x' in o; }");
        Assert.False(Assert.IsType<TsBoolValue>(interpreter.Execute(recompiled, "main", args)).Value);
    }

    private static BytecodeFunction Function(byte[] code) =>
        new("main", code, 1, 0, false, new[] { "x" }, Array.Empty<long>(), Array.Empty<double>());
}
