using System.Globalization;
using System.Numerics;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ParseIntFixedWidthBigIntRadixTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("unsigned-positive", "z", "u64", 36, double.NaN)]
    [InlineData("signed-positive", "z", "i64", 36, double.NaN)]
    [InlineData("signed-negative-decimal-fallback", "42", "i64", -2, 42d)]
    [InlineData("genuine-bigint", "z", "bigint", 36, double.NaN)]
    [InlineData("int32", "z", "i32", 36, 35d)]
    [InlineData("float32", "z", "f32", 36, 35d)]
    [InlineData("float64", "z", "f64", 36, 35d)]
    public void ParseInt_PreservesFixedWidthBigIntRadixCompatibility(
        string row, string text, string kind, int radix, double expected)
    {
        const string source = "function main(input: string, radix: any): number { return parseInt(input, radix); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        int parserCount = parser.Diagnostics.Count();
        int binderCount = binder.Diagnostics.All.Count();
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        var block = Assert.Single(main.Blocks, candidate => candidate.Instructions.Contains(call));
        int callIndex = block.Instructions.IndexOf(call);
        Assert.True(callIndex >= 2);
        var inputLoad = block.Instructions[callIndex - 2];
        var radixLoad = block.Instructions[callIndex - 1];
        string target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("parseInt", target);
        Assert.Equal(2, call.Operand1);
        Assert.Equal(Opcode.LoadArg, inputLoad.Opcode);
        Assert.Equal(0, inputLoad.Operand0);
        Assert.Equal(Opcode.LoadArg, radixLoad.Opcode);
        Assert.Equal(1, radixLoad.Operand0);

        TsValue[] args = [TsValue.FromString(text), kind switch
        {
            "u64" => TsValue.FromUInt64((ulong)radix),
            "i64" => TsValue.FromInt64(radix),
            "bigint" => TsValue.FromBigInt(new BigInteger(radix)),
            "i32" => TsValue.FromInt32(radix),
            "f32" => TsValue.FromFloat32(radix),
            "f64" => TsValue.FromFloat64(radix),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        }];
        var input = Assert.IsType<TsStringValue>(args[0]);
        Assert.Equal(text, input.Value);
        string radixValue;
        string? radixBits = null;
        var culture = CultureInfo.InvariantCulture;
        switch (kind)
        {
            case "u64":
                var u64 = Assert.IsType<TsUInt64Value>(args[1]).Value;
                Assert.Equal((ulong)radix, u64);
                radixValue = u64.ToString(culture);
                break;
            case "i64":
                var i64 = Assert.IsType<TsInt64Value>(args[1]).Value;
                Assert.Equal((long)radix, i64);
                radixValue = i64.ToString(culture);
                break;
            case "bigint":
                var big = Assert.IsType<TsBigIntValue>(args[1]).Value;
                Assert.Equal(new BigInteger(radix), big);
                radixValue = big.ToString(culture);
                break;
            case "i32":
                var i32 = Assert.IsType<TsInt32Value>(args[1]).Value;
                Assert.Equal(radix, i32);
                radixValue = i32.ToString(culture);
                break;
            case "f32":
                var f32 = Assert.IsType<TsFloat32Value>(args[1]).Value;
                Assert.Equal((float)radix, f32);
                radixValue = f32.ToString("R", culture);
                radixBits = BitConverter.SingleToInt32Bits(f32).ToString("X8", culture);
                Assert.Equal("42100000", radixBits);
                break;
            case "f64":
                var f64 = Assert.IsType<TsFloat64Value>(args[1]).Value;
                Assert.Equal((double)radix, f64);
                radixValue = f64.ToString("R", culture);
                radixBits = BitConverter.DoubleToInt64Bits(f64).ToString("X16", culture);
                Assert.Equal("4042000000000000", radixBits);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(bytecode, "main", args); });
        var actual = (result as TsFloat64Value)?.Value;
        long? actualBits = actual is double value ? BitConverter.DoubleToInt64Bits(value) : null;
        long expectedBits = BitConverter.DoubleToInt64Bits(expected);
        output.WriteLine("parseInt fixed-width " + JsonSerializer.Serialize(new
        {
            row, parserCount, binderCount, target, argc = call.Operand1,
            inputOp = inputLoad.Opcode.ToString(), inputArg = inputLoad.Operand0,
            radixOp = radixLoad.Opcode.ToString(), radixArg = radixLoad.Operand0,
            input = input.Value, inputType = input.GetType().Name,
            radixType = args[1].GetType().Name, radixValue, radixBits,
            actualType = result?.GetType().Name, actual = actual?.ToString("R", culture),
            actualBits = actualBits?.ToString("X16", culture),
            expectedType = nameof(TsFloat64Value), expected = expected.ToString("R", culture),
            expectedBits = expectedBits.ToString("X16", culture), escaped = escaped?.GetType().FullName
        }));
        Assert.Null(escaped);
        double numeric = Assert.IsType<TsFloat64Value>(result).Value;
        Assert.True(double.IsNaN(expected) ? double.IsNaN(numeric) : actualBits == expectedBits,
            $"Fixed-width parseInt final outcome disagrees for {row}");
    }
}
