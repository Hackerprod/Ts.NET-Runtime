using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringCharCodeAtOmittedIndexTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omitted-ascii", "", "65")]
    [InlineData("omitted-empty", "", "NaN")]
    [InlineData("omitted-surrogate-pair", "", "55357")]
    [InlineData("explicit-zero", "0", "65")]
    [InlineData("explicit-one", "1", "66")]
    [InlineData("explicit-fraction", "1.75", "66")]
    [InlineData("explicit-negative", "-1", "NaN")]
    [InlineData("explicit-past-end", "3", "NaN")]
    public void StringCharCodeAt_DefaultsOmittedIndexToZero(string row, string expression, string expected)
    {
        var text = row switch { "omitted-empty" => "", "omitted-surrogate-pair" => "\U0001F600", _ => "ABC" };
        var source = $"function main(text: string): number {{ return text.charCodeAt({expression}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, item => item.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), item => item.Opcode == Opcode.Call);
        Assert.Equal("String::charCodeAt", Assert.IsType<string>(call.OperandObject));
        int argc = expression.Length == 0 ? 1 : 2;
        Assert.Equal(argc, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);

        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() => { result = vm.Execute(bytecode, "main", [TsValue.FromString(text)]); });
        var actual = error is not null ? $"throw:{error.GetType().Name}" : result switch
        {
            TsInt32Value number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TsFloat64Value number when double.IsNaN(number.Value) => "NaN",
            _ => $"unexpected-type:{result?.GetType().Name}"
        };
        output.WriteLine($"String.charCodeAt row={row}; parser/binder=0/0; target=String::charCodeAt; argc={argc}; expected={expected}; actual={actual}");
        Assert.True(expected == actual, $"String.charCodeAt omitted index mismatch: expected={expected}; actual={actual}");
    }
}
