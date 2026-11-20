using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public class MathPowNonFiniteExponentTests
{
    private readonly ITestOutputHelper _output;
    public MathPowNonFiniteExponentTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("one-pos-inf", "3FF0000000000000 7FF0000000000000", "NaN")]
    [InlineData("one-neg-inf", "3FF0000000000000 FFF0000000000000", "NaN")]
    [InlineData("minus-one-pos-inf", "BFF0000000000000 7FF0000000000000", "NaN")]
    [InlineData("minus-one-neg-inf", "BFF0000000000000 FFF0000000000000", "NaN")]
    [InlineData("one-nan", "3FF0000000000000 7FF8000000000000", "NaN")]
    [InlineData("minus-one-nan", "BFF0000000000000 7FF8000000000000", "NaN")]
    [InlineData("nan-zero", "7FF8000000000000 0000000000000000", "3FF0000000000000")]
    [InlineData("minus-one-odd", "BFF0000000000000 4008000000000000", "BFF0000000000000")]
    [InlineData("minus-one-even", "BFF0000000000000 4000000000000000", "3FF0000000000000")]
    [InlineData("two-pos-inf", "4000000000000000 7FF0000000000000", "7FF0000000000000")]
    [InlineData("half-pos-inf", "3FE0000000000000 7FF0000000000000", "0000000000000000")]
    [InlineData("negative-zero-odd", "8000000000000000 4008000000000000", "8000000000000000")]
    [InlineData("negative-fraction", "C000000000000000 3FE0000000000000", "NaN")]
    public void MathPow_NonFiniteExponentSemantics(string row, string argumentBits, string expected)
    {
        var bits = argumentBits.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var args = bits.Select(bitPattern => TsValue.FromFloat64(BitConverter.Int64BitsToDouble(
            unchecked((long)Convert.ToUInt64(bitPattern, 16))))).ToArray();
        var parameters = string.Join(", ", bits.Select((_, i) => $"x{i}: number"));
        var arguments = string.Join(", ", bits.Select((_, i) => $"x{i}"));
        var source = $"function main({parameters}): number {{ return Math.pow({arguments}); }}";

        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("Math::pow", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(args.Length, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var value = Assert.IsType<TsFloat64Value>(interpreter.Execute(bytecode, "main", args)).Value;
        var actual = double.IsNaN(value) ? "NaN" : BitConverter.DoubleToInt64Bits(value).ToString("X16");
        _output.WriteLine($"Math.pow row={row}; parser/binder=0/0; target=Math::pow; argc={call.Operand1}; result=Float64; expected={expected}; actual={actual}");
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"Math.pow binary64 mismatch: expected={expected}; actual={actual}");
    }
}
