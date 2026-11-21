using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class NumberNullAndBlankTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("null", null, 0d)]
    [InlineData("empty", "", 0d)]
    [InlineData("ascii-blank", " \t\v\f\r\n", 0d)]
    [InlineData("ecma-blank", "\u00A0\uFEFF\u2028\u2029", 0d)]
    [InlineData("undefined", null, double.NaN)]
    [InlineData("omitted", null, 0d)]
    [InlineData("false", false, 0d)]
    [InlineData("true", true, 1d)]
    [InlineData("padded-decimal", " \t12.5\r\n", 12.5d)]
    [InlineData("negative-zero-number", null, 0d)]
    [InlineData("negative-zero-string", "-0", 0d)]
    [InlineData("non-ecma-space", "\u0085", double.NaN)]
    public void Number_ConvertsPrimitive(string row, object? input, double expected)
    {
        var omitted = row == "omitted";
        var source = omitted
            ? "function main(): number { return Number(); }"
            : "function main(input: any): number { return Number(input); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = Assert.Single(main.Blocks).Instructions;
        var argc = omitted ? 0 : 1;
        if (omitted)
        {
            Assert.Collection(instructions, AssertCall, AssertReturn);
        }
        else
        {
            Assert.Collection(instructions, load =>
            {
                Assert.Equal(Opcode.LoadArg, load.Opcode);
                Assert.Equal(0, load.Operand0);
            }, AssertCall, AssertReturn);
        }

        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        if (row is "negative-zero-number" or "negative-zero-string")
            expected = negativeZero;

        TsValue? argument = row switch
        {
            "null" => Assert.IsType<TsNull>(TsValue.Null),
            "undefined" => Assert.IsType<TsVoid>(TsValue.Void),
            "omitted" => null,
            "negative-zero-number" => Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(negativeZero)),
            _ when input is bool flag => Assert.IsType<TsBoolValue>(TsValue.FromBool(flag)),
            _ => Assert.IsType<TsStringValue>(TsValue.FromString(Assert.IsType<string>(input)))
        };

        switch (argument)
        {
            case TsFloat64Value number:
                Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(number.Value));
                break;
            case TsBoolValue boolean:
                Assert.Equal(Assert.IsType<bool>(input), boolean.Value);
                break;
            case TsStringValue text:
                Assert.True(string.Equals(Assert.IsType<string>(input), text.Value, StringComparison.Ordinal));
                break;
        }

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() =>
        {
            result = vm.Execute(bytecode, "main", argument is null ? [] : [argument]);
        });

        var actualText = result is TsFloat64Value actualNumber ? DescribeNumber(actualNumber.Value) : "n/a";
        output.WriteLine($"Number row={row}; parser/binder=0/0; LoadArg={(omitted ? "none" : "0")}; Call target=Number argc={argc}; Return; argument={DescribeArgument(argument)}; exception={error?.GetType().Name ?? "none"}; result={result?.GetType().Name ?? "null"}; actual={actualText}; expected={DescribeNumber(expected)}");
        Assert.Null(error);
        var actual = Assert.IsType<TsFloat64Value>(result).Value;
        var matches = double.IsNaN(expected) ? double.IsNaN(actual)
            : expected == 0d ? BitConverter.DoubleToInt64Bits(actual) == BitConverter.DoubleToInt64Bits(expected)
            : actual == expected;
        Assert.True(matches, $"Number result mismatch: expected={DescribeNumber(expected)}; actual={DescribeNumber(actual)}");

        void AssertCall(Instruction call)
        {
            Assert.Equal(Opcode.Call, call.Opcode);
            Assert.Equal("Number", Assert.IsType<string>(call.OperandObject));
            Assert.Equal(argc, call.Operand1);
        }
    }

    private static void AssertReturn(Instruction instruction) => Assert.Equal(Opcode.Return, instruction.Opcode);

    private static string DescribeArgument(TsValue? argument) => argument switch
    {
        TsStringValue text => $"TsStringValue:[{string.Concat(text.Value.Select(unit => $"\\u{(int)unit:X4}"))}]",
        TsBoolValue boolean => $"TsBoolValue:{(boolean.Value ? "true" : "false")}",
        TsFloat64Value number => $"TsFloat64Value:{DescribeNumber(number.Value)}",
        TsNull => "TsNull:null",
        TsVoid => "TsVoid:undefined",
        null => "omitted",
        _ => throw new InvalidOperationException()
    };

    private static string DescribeNumber(double value) => FormattableString.Invariant(
        $"{value:R} (bits=0x{BitConverter.DoubleToInt64Bits(value):X16})");
}
