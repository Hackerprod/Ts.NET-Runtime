using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class JsonStringifyUndefinedObjectFieldsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("literal-field", "({ missing: undefined, kept: 7 })", "{\"kept\":7}")]
    [InlineData("forwarded-field", "({ missing: value, kept: null })", "{\"kept\":null}")]
    [InlineData("nested-field", "([{ inner: { missing: value } }])", "[{\"inner\":{}}]")]
    [InlineData("only-undefined", "({ missing: value })", "{}")]
    [InlineData("null-control", "({ kept: null })", "{\"kept\":null}")]
    [InlineData("array-control", "([value])", "[null]")]
    [InlineData("ordinary-control", "({ first: false, second: 0, third: \"\" })", "{\"first\":false,\"second\":0,\"third\":\"\"}")]
    [InlineData("empty-control", "({})", "{}")]
    public void JsonStringify_OmitsUndefinedObjectFields(string row, string expression, string expected)
    {
        var source = $"function main(value: number | undefined): string {{ return JSON.stringify({expression}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("JSON::stringify", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(1, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);

        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsStringValue>(vm.Execute(bytecode, "main", [TsValue.Void])).Value;
        output.WriteLine($"JSON.stringify row={row}; parser/binder=0/0; target=JSON::stringify; argc=1; expected={expected}; actual={actual}");
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"JSON.stringify undefined object field mismatch: expected={expected}; actual={actual}");
    }
}
