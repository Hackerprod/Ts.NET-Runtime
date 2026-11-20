using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringPadEndNegativeWidthTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("negative-integer", "ab", "-1", "xy", "0061 0062")]
    [InlineData("negative-fraction", "ab", "-2.75", "xy", "0061 0062")]
    [InlineData("negative-infinity", "ab", "-Infinity", "xy", "0061 0062")]
    [InlineData("negative-empty-receiver", "", "-2", "", "")]
    [InlineData("negative-truncates-to-zero", "ab", "-0.75", "xy", "0061 0062")]
    [InlineData("negative-zero", "ab", "-0", "xy", "0061 0062")]
    [InlineData("positive-no-grow", "ab", "1", "xy", "0061 0062")]
    [InlineData("positive-pads", "ab", "5.75", "xy", "0061 0062 0078 0079 0078")]
    [InlineData("positive-empty-fill", "ab", "5", "", "0061 0062")]
    public void StringPadEnd_ClampsNegativeWidth(string row, string input, string width, string fill, string expected)
    {
        const string source = "function main(text: string, width: number, fill: string): string { return text.padEnd(width, fill); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var function = Assert.Single(ir.Functions, item => item.Name == "main");
        var call = Assert.Single(function.Blocks.SelectMany(block => block.Instructions), item => item.Opcode == Opcode.Call);
        Assert.Equal("String::padEnd", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(3, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var widthValue = TsValue.FromFloat64(double.Parse(width, System.Globalization.CultureInfo.InvariantCulture));
        TsValue? value = null;
        var error = Record.Exception(() => { value = vm.Execute(bytecode, "main", [TsValue.FromString(input), widthValue, TsValue.FromString(fill)]); });
        var actual = error is not null ? $"exception:{error.GetType().FullName}" : value is TsStringValue str
            ? string.Join(" ", str.Value.Select(unit => ((int)unit).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)))
            : $"type:{value?.GetType().FullName ?? "null"}";
        output.WriteLine($"String.padEnd row={row}; parser/binder=0/0; target=String::padEnd; argc=3; expected=[{expected}]; actual=[{actual}]");
        Assert.True(error is null && value is TsStringValue && string.Equals(expected, actual, StringComparison.Ordinal),
            $"String.padEnd result mismatch: expected=[{expected}]; actual=[{actual}]");
    }
}
