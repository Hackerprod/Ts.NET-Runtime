using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ParseIntExplicitRadixTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("base36", "z", 36, 35d)]
    [InlineData("base3-prefix", "102", 3, 11d)]
    [InlineData("decimal-suffix", "12px", 10, 12d)]
    [InlineData("invalid-low", "42", 1, double.NaN)]
    [InlineData("octal-prefix", "19", 8, 1d)]
    [InlineData("hex-control", "ff", 16, 255d)]
    [InlineData("decimal-control", "42", 10, 42d)]
    [InlineData("binary-control", "101", 2, 5d)]
    public void ParseInt_ExplicitRadixCore(string row, string input, int radix, double expected) =>
        Evaluate("core", row, input, radix, expected);

    public static IEnumerable<object[]> EdgeCases()
    {
        yield return ["whitespace-hex", "\ufeff\u2028-0xFtail", 16, -15d];
        yield return ["uppercase-hex", "+0Xff!", 16, 255d];
        yield return ["decimal-marker", "0x10", 10, 0d];
        yield return ["binary-marker", "0x10", 2, 0d];
        yield return ["base36-marker", "0x10", 36, 42804d];
        yield return ["bare-marker", "0x", 16, double.NaN];
        yield return ["negative-zero", "-0!", 10, -0.0];
        yield return ["nel", "\u008542", 10, double.NaN];
        yield return ["fullwidth-zero", "\uff10", 10, double.NaN];
        yield return ["rounding-tie", "9007199254740995", 10, 9007199254740996d];
        yield return ["signed-hex", "-0f", 16, -15d];
        yield return ["invalid-negative", "42", -2, double.NaN];
        yield return ["invalid-high", "42", 37, double.NaN];
        yield return ["positive-overflow", "1" + new string('0', 1024), 2, double.PositiveInfinity];
        yield return ["negative-overflow", "-1" + new string('0', 1024), 2, double.NegativeInfinity];
    }

    [Theory]
    [MemberData(nameof(EdgeCases))]
    public void ParseInt_ExplicitRadixEdges(string row, string input, int radix, double expected) =>
        Evaluate("edges", row, input, radix, expected);

    private void Evaluate(string phase, string row, string input, int radix, double expected)
    {
        string source = "function main(input: string): number { return parseInt(input, " +
            radix.ToString(CultureInfo.InvariantCulture) + "); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        string trace = $"parseInt phase={phase}; row={row}";
        output.WriteLine($"{trace}; parser/binder={parser.Diagnostics.Count()}/{binder.Diagnostics.All.Count()}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        output.WriteLine($"{trace}; target={call.OperandObject}; argc={call.Operand1}");
        Assert.Equal("parseInt", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(2, call.Operand1);
        var operands = instructions[..Array.IndexOf(instructions, call)][^(radix < 0 ? 3 : 2)..];
        foreach (var instruction in operands)
            output.WriteLine($"{trace}; opcode={instruction.Opcode}; operand0={instruction.Operand0}; object={instruction.OperandObject}");
        Assert.Equal(Opcode.LoadArg, operands[0].Opcode);
        Assert.Equal(0, operands[0].Operand0);
        // An ordinary unsuffixed integer literal is lowered according to its bound numeric type.
        if (operands[1].Opcode == Opcode.LoadConst_I32)
            Assert.Equal(Math.Abs(radix), operands[1].Operand0);
        else
        {
            Assert.Equal(Opcode.LoadConst_F64, operands[1].Opcode);
            Assert.Equal((double)Math.Abs(radix), Assert.IsType<double>(operands[1].OperandObject));
        }
        if (radix < 0)
        {
            Assert.Equal(Opcode.LoadConst_I32, operands[1].Opcode);
            Assert.Equal(2, operands[1].Operand0);
            Assert.Equal(Opcode.Neg_F64, operands[2].Opcode);
        }

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var bytecode = BytecodeCompiler.Compile(ir);
        var result = interpreter.Execute(bytecode, "main", [TsValue.FromString(input)]);
        var value = Assert.IsType<TsFloat64Value>(result).Value;
        long actualBits = BitConverter.DoubleToInt64Bits(value);
        long expectedBits = BitConverter.DoubleToInt64Bits(expected);
        output.WriteLine($"{trace}; type=TsFloat64Value; input={JsonSerializer.Serialize(input)}; radix={radix}; " +
            $"expected={expected.ToString("R", CultureInfo.InvariantCulture)}; expectedBits={expectedBits:X16}; " +
            $"actual={value.ToString("R", CultureInfo.InvariantCulture)}; actualBits={actualBits:X16}");
        Assert.True(double.IsNaN(expected) ? double.IsNaN(value) : actualBits == expectedBits,
            $"Final numeric result disagrees for {row}");
    }
}
