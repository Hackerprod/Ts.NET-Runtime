using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class NumberParseFloatPrefixTests(ITestOutputHelper output)
{
    [Fact]
    public void NumberParseFloat_UsesLongestDecimalPrefix()
    {
        var tokens = new Lexer("function main(text: string): number { return Number.parseFloat(text); }").Tokenize();
        var lexerCount = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerCount);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = Assert.Single(main.Blocks).Instructions.ToArray();
        Assert.Equal(3, instructions.Length);
        var load = instructions[0];
        Assert.Equal(Opcode.LoadArg, load.Opcode);
        Assert.Equal(0, load.Operand0);
        var call = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal(Opcode.Call, instructions[1].Opcode);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Number::parseFloat", target);
        Assert.Equal(1, call.Operand1);
        Assert.Equal(Opcode.Return, instructions[2].Opcode);
        var code = BytecodeCompiler.Compile(ir);

        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        Assert.Equal(0.0, negativeZero);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(negativeZero));
        (string Id, string Input, double Expected)[] rows =
        [
            ("suffix", "12.5px", 12.5),
            ("incomplete-exponent", "1e+kg", 1),
            ("negative-zero", "-0.0px", negativeZero),
            ("grouping", "1,234", 1),
            ("finite-control", "12.5", 12.5),
            ("no-prefix-control", "junk2.5", double.NaN),
            ("infinity-control", "Infinity", double.PositiveInfinity)
        ];

        var observations = rows.Select(row =>
        {
            var input = Assert.IsType<TsStringValue>(TsValue.FromString(row.Input));
            var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
            TsValue? result = null;
            var escape = Record.Exception(() => { result = interpreter.Execute(code, "main", [input]); });
            var actual = result as TsFloat64Value;
            output.WriteLine("C178 Number.parseFloat " + JsonSerializer.Serialize(new
            {
                row = row.Id,
                input = input.Value,
                inputType = input.GetType().Name,
                returnType = result?.GetType().Name,
                lexerCount,
                parserCount = parser.Diagnostics.Count(),
                binderCount = binder.Diagnostics.All.Count(),
                target,
                argc = call.Operand1,
                loadArg = load.Operand0,
                actual = actual is null ? null : Describe(actual.Value),
                expected = Describe(row.Expected),
                escape = escape?.ToString()
            }));
            return (row, result, escape);
        }).ToArray();

        var mismatches = observations.Where(observation =>
        {
            Assert.Null(observation.escape);
            var actual = Assert.IsType<TsFloat64Value>(observation.result).Value;
            var expected = observation.row.Expected;
            return double.IsNaN(expected) ? !double.IsNaN(actual)
                : BitConverter.DoubleToInt64Bits(actual) != BitConverter.DoubleToInt64Bits(expected);
        }).Select(observation => observation.row.Id).ToArray();
        Assert.True(mismatches.Length == 0, "C178_NUMBER_PARSEFLOAT_MISMATCH_IDS: " + string.Join(", ", mismatches));
    }

    private static object Describe(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        return new
        {
            classification = double.IsNaN(value) ? "NaN"
                : double.IsPositiveInfinity(value) ? "+Infinity"
                : double.IsNegativeInfinity(value) ? "-Infinity"
                : bits == long.MinValue ? "-0"
                : value.ToString("R", CultureInfo.InvariantCulture),
            bits = double.IsNaN(value) ? null : bits.ToString("X16", CultureInfo.InvariantCulture)
        };
    }
}
