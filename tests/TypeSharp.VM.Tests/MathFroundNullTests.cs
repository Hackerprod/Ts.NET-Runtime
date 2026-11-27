using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class MathFroundNullTests(ITestOutputHelper output)
{
    private const string LiteralSource = "function main(): number { return Math.fround(null); }";
    private const string ArgumentSource = "function main(value: any): number { return Math.fround(value); }";
    private const string OmittedSource = "function main(): number { return Math.fround(); }";
    private const string Target = "Math::fround";

    [Fact]
    public void LiteralNull_ReturnsPositiveFloat64Zero() =>
        Check("literal-null", LiteralSource, Opcodes.LoadConstNull, [], "0000000000000000");

    [Fact]
    public void HostNull_ReturnsPositiveFloat64Zero()
    {
        var input = Assert.IsType<TsNull>(TsValue.Null);
        Assert.Equal(TsValueType.Null, input.ValueType);
        Check("host-null", ArgumentSource, Opcodes.LoadArg, [input], "0000000000000000");
    }

    [Fact]
    public void ExplicitUndefinedAndOmission_ReturnFloat64NaN()
    {
        var input = Assert.IsType<TsVoid>(TsValue.Void);
        Assert.Equal(TsValueType.Void, input.ValueType);
        Check("host-undefined", ArgumentSource, Opcodes.LoadArg, [input], "NaN");
        Check("omitted", OmittedSource, null, [], "NaN");
    }

    [Fact]
    public void SignedZeroAndBinary32Rounding_ArePreserved()
    {
        Check("positive-zero", ArgumentSource, Opcodes.LoadArg, [TsValue.FromFloat64(0d)], "0000000000000000");
        Check("negative-zero", ArgumentSource, Opcodes.LoadArg,
            [TsValue.FromFloat64(BitConverter.Int64BitsToDouble(long.MinValue))], "8000000000000000");
        Check("tie-down", ArgumentSource, Opcodes.LoadArg, [TsValue.FromFloat64(16777217d)], "4170000000000000");
    }

    private void Check(string row, string source, byte? load, TsValue[] arguments, string expected)
    {
        var tokens = new Lexer(source, "math-fround-null.ts").Tokenize();
        Assert.Equal(0, tokens.Count(token => token.Kind == TokenKind.Error));
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.All);

        var module = BytecodeCompiler.Compile(new IRGenerator().Generate(bound));
        BytecodeVerifier.Verify(module);
        var main = Assert.Single(module.Functions);
        Assert.Equal("main", main.Name);
        Assert.Equal(arguments.Length, main.ParameterCount);
        using var reader = new BinaryReader(new MemoryStream(main.Instructions, writable: false));
        if (load is byte opcode)
            Assert.Equal(opcode, reader.ReadByte());
        if (load == Opcodes.LoadArg)
            Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(Opcodes.Call, reader.ReadByte());
        Assert.Equal(Target, main.StringConstants[reader.ReadInt32()]);
        int argc = load is null ? 0 : 1;
        Assert.Equal(argc, reader.ReadInt32());
        Assert.Equal(Opcodes.Return, reader.ReadByte());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        output.WriteLine($"ROUTE {row} diagnostics=0/0/0 code={Convert.ToHexString(main.Instructions)} target={Target} argc={argc}");

        var result = Assert.IsType<TsFloat64Value>(
            new TypeSharp.VM.Interpreter.Interpreter().Execute(module, main.Name, arguments));
        Assert.Equal(TsValueType.Float64, result.ValueType);
        string actual = double.IsNaN(result.Value) ? "NaN" : BitConverter.DoubleToInt64Bits(result.Value).ToString("X16");
        output.WriteLine($"RESULT {row} {result.ValueType} actual={actual} expected={expected}");
        Assert.True(expected == actual, $"Math.fround {row}: expected Float64 {expected}, observed {actual}");
    }
}
