using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringSplitUndefinedSeparatorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omitted-text", "a,b", "", 1, "[\"a,b\"]")]
    [InlineData("omitted-empty", "", "", 1, "[\"\"]")]
    [InlineData("explicit-undefined", "aundefinedb", "undefined", 2, "[\"aundefinedb\"]")]
    [InlineData("explicit-undefined-limit-one", "aundefinedb", "undefined, 1", 3, "[\"aundefinedb\"]")]
    [InlineData("explicit-undefined-limit-zero", "aundefinedb", "undefined, 0", 3, "[]")]
    [InlineData("explicit-undefined-limit-wrapped-zero", "aundefinedb", "undefined, 4294967296", 3, "[]")]
    [InlineData("explicit-undefined-limit-undefined", "aundefinedb", "undefined, undefined", 3, "[\"aundefinedb\"]")]
    [InlineData("literal-undefined", "aundefinedb", "\"undefined\"", 2, "[\"a\",\"b\"]")]
    [InlineData("empty-separator", "abc", "\"\"", 2, "[\"a\",\"b\",\"c\"]")]
    [InlineData("comma-limited", "a,b,c", "\",\", 1", 3, "[\"a\"]")]
    public void StringSplit_DistinguishesAbsentSeparator(string row, string text, string expression, int argc, string expected)
    {
        var source = $"function main(text: string): string[] {{ return text.split({expression}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, item => item.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), item => item.Opcode == Opcode.Call);
        Assert.Equal("String::split", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(argc, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);

        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() => { result = vm.Execute(bytecode, "main", [TsValue.FromString(text)]); });
        var actual = error is not null ? $"throw:{error.GetType().Name}" : result is TsArrayValue array
            ? JsonSerializer.Serialize(Enumerable.Range(0, array.Value.Count)
                .Select(index => Assert.IsType<TsStringValue>(array.Value.Get(index)).Value))
            : $"unexpected-type:{result?.GetType().Name}";
        output.WriteLine($"String.split row={row}; parser/binder=0/0; target=String::split; argc={argc}; expected={expected}; actual={actual}");
        Assert.True(expected == actual, $"String.split absent separator mismatch: expected={expected}; actual={actual}");
    }
}
