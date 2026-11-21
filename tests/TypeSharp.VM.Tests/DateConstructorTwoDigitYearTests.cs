using System.Globalization;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class DateConstructorTwoDigitYearTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("short-year-seven-components", "95, 5, 15, 12, 34, 56, 789", 7, 803219696789d)]
    [InlineData("short-year-two-components", "73, 0", 2, 94694400000d)]
    [InlineData("four-digit-seven-components", "1995, 5, 15, 12, 34, 56, 789", 7, 803219696789d)]
    [InlineData("four-digit-two-components", "1973, 0", 2, 94694400000d)]
    [InlineData("epoch-ms-95", "95", 1, 95d)]
    [InlineData("epoch-ms-73", "73", 1, 73d)]
    public void ConstructDate_ReturnsNativeUtcTimestamp(string row, string arguments, int constructorArity, double expected)
    {
        var source = $"function main(): number {{ return new Date({arguments}).getTime(); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var constructor = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.NewObject);
        Assert.Equal("Date", Assert.IsType<string>(constructor.OperandObject));
        Assert.Equal(constructorArity, constructor.Operand1);
        var call = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("Date::getTime", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(1, call.Operand1);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode is
            Opcode.CallDynamic or Opcode.CallDynamicArray or Opcode.CallVirt);

        var bytecode = BytecodeCompiler.Compile(ir);
        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(bytecode, "main", []);
        var actual = Assert.IsType<TsFloat64Value>(result).Value;
        Assert.True(double.IsFinite(actual));

        var actualText = actual.ToString("R", CultureInfo.InvariantCulture);
        var expectedText = expected.ToString("R", CultureInfo.InvariantCulture);
        output.WriteLine($"C120 row={row}; arguments={arguments}; parser/binder=0/0; NewObject Date count=1 argc={constructorArity}; Call Date::getTime count=1 argc=1; dynamic/virtual=0; tag=TsFloat64Value; finite=true; actual={actualText}; expected={expectedText}");
        Assert.True(expected == actual, $"C120_DATE_COMPONENT_YEAR_MISMATCH row={row}; actual={actualText}; expected={expectedText}");
    }
}
