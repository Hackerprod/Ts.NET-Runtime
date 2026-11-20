using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringCharAtOmittedIndexTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omitted-first", "ABC", "omitted", "0041")]
    [InlineData("omitted-empty", "", "omitted", "empty")]
    [InlineData("explicit-zero", "ABC", "zero", "0041")]
    [InlineData("explicit-one", "ABC", "one", "0042")]
    [InlineData("explicit-negative", "ABC", "negative", "empty")]
    [InlineData("explicit-past", "ABC", "past", "empty")]
    public void StringCharAt_DefaultsOmittedIndexToZero(string row, string text, string indexKind, string expected)
    {
        const string omitted = "function main(text: string): string { return text.charAt(); }";
        const string explicitIndex = "function main(text: string, index: number): string { return text.charAt(index); }";
        var hasIndex = indexKind != "omitted";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(hasIndex ? explicitIndex : omitted).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, item => item.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), item => item.Opcode == Opcode.Call);
        Assert.Equal("String::charAt", Assert.IsType<string>(call.OperandObject));
        var argc = hasIndex ? 2 : 1;
        Assert.Equal(argc, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);

        var index = indexKind switch { "zero" => 0, "one" => 1, "negative" => -1, "past" => 3, "omitted" => 0, _ => throw new ArgumentOutOfRangeException(nameof(indexKind)) };
        TsValue[] args = hasIndex ? [TsValue.FromString(text), TsValue.FromFloat64(index)] : [TsValue.FromString(text)];
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() => { result = vm.Execute(bytecode, "main", args); });
        var actual = error is not null
            ? $"throw:{error.GetType().Name}"
            : Assert.IsType<TsStringValue>(result).Value switch
            {
                "" => "empty",
                { Length: 1 } s => ((int)s[0]).ToString("X4"),
                var s => $"unexpected-length:{s.Length}"
            };
        output.WriteLine($"String.charAt row={row}; parser/binder=0/0; target=String::charAt; argc={argc}; expected={expected}; actual={actual}");
        Assert.True(expected == actual, $"String.charAt omitted index mismatch: expected={expected}; actual={actual}");
    }
}
