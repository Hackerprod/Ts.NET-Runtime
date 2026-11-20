using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringReplaceAllEmptySearchTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("empty-ascii", "ab", "", "-", "002D 0061 002D 0062 002D")]
    [InlineData("empty-receiver", "", "", "x", "0078")]
    [InlineData("empty-template", "ab", "", "", "0061 0062")]
    [InlineData("empty-original-context", "ab", "", "[$`|$&|$'|$$]", "005B 007C 007C 0061 0062 007C 0024 005D 0061 005B 0061 007C 007C 0062 007C 0024 005D 0062 005B 0061 0062 007C 007C 007C 0024 005D")]
    [InlineData("empty-utf16", "😀", "", ".", "002E D83D 002E DE00 002E")]
    [InlineData("empty-no-rescan", "$&", "", "$$&", "0024 0026 0024 0024 0026 0026 0024 0026")]
    [InlineData("nonempty-plain", "ababa", "a", "!", "0021 0062 0021 0062 0021")]
    [InlineData("nonempty-tokens", "aba", "a", "[$&]", "005B 0061 005D 0062 005B 0061 005D")]
    [InlineData("nonempty-no-match", "ab", "z", "!", "0061 0062")]
    public void StringReplaceAll_MatchesEmptySearchAtUtf16Boundaries(string row, string input, string find, string template, string expected)
    {
        const string source = "function main(text: string, find: string, template: string): string { return text.replaceAll(find, template); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var function = Assert.Single(ir.Functions, item => item.Name == "main");
        var call = Assert.Single(function.Blocks.SelectMany(block => block.Instructions), item => item.Opcode == Opcode.Call);
        Assert.Equal("String::replaceAll", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(3, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? value = null;
        var error = Record.Exception(() => { value = vm.Execute(bytecode, "main", [TsValue.FromString(input), TsValue.FromString(find), TsValue.FromString(template)]); });
        var actual = error is not null ? $"exception:{error.GetType().FullName}" : value is TsStringValue str
            ? string.Join(" ", str.Value.Select(unit => ((int)unit).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)))
            : $"type:{value?.GetType().FullName ?? "null"}";
        output.WriteLine($"String.replaceAll row={row}; parser/binder=0/0; target=String::replaceAll; argc=3; expected=[{expected}]; actual=[{actual}]");
        Assert.True(error is null && value is TsStringValue && string.Equals(expected, actual, StringComparison.Ordinal),
            $"String.replaceAll result mismatch: expected=[{expected}]; actual=[{actual}]");
    }
}
