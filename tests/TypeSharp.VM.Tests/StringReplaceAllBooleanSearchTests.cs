using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringReplaceAllBooleanSearchTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("boolean-true-repeat", "true/true", true, "<$&>", "<true>/<true>")]
    [InlineData("boolean-false-repeat", "false:false", false, "[$$:$&]", "[$:false]:[$:false]")]
    [InlineData("string-search-control", "true/true", "true", "<$&>", "<true>/<true>")]
    [InlineData("literal-template-control", "true/true", true, "ok", "ok/ok")]
    [InlineData("boolean-no-match-control", "false/false", true, "<$&>", "false/false")]
    public void StringReplaceAll_AppliesReplacementTemplate(string row, string input, object find, string template, string expected)
    {
        const string source = "function main(text: string, find: any, template: string): string { return text.replaceAll(find, template); }";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = Assert.Single(main.Blocks).Instructions;
        Assert.Collection(instructions,
            first => AssertLoadArg(first, 0),
            second => AssertLoadArg(second, 1),
            third => AssertLoadArg(third, 2),
            call =>
            {
                Assert.Equal(Opcode.Call, call.Opcode);
                Assert.Equal("String::replaceAll", Assert.IsType<string>(call.OperandObject));
                Assert.Equal(3, call.Operand1);
            },
            ret => Assert.Equal(Opcode.Return, ret.Opcode));

        TsValue search;
        string searchValue;
        if (find is bool flag)
        {
            var booleanSearch = Assert.IsType<TsBoolValue>(TsValue.FromBool(flag));
            Assert.Equal(flag, booleanSearch.Value);
            search = booleanSearch;
            searchValue = booleanSearch.Value ? "true" : "false";
        }
        else
        {
            var text = Assert.IsType<string>(find);
            var stringSearch = Assert.IsType<TsStringValue>(TsValue.FromString(text));
            Assert.Equal(text, stringSearch.Value);
            search = stringSearch;
            searchValue = stringSearch.Value;
        }

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() =>
        {
            result = vm.Execute(bytecode, "main", [TsValue.FromString(input), search, TsValue.FromString(template)]);
        });

        output.WriteLine($"String.replaceAll row={row}; parser/binder=0/0; LoadArg=0,1,2; Call target=String::replaceAll argc=3; Return; search={search.GetType().Name}:[{searchValue}]; exception={error?.GetType().Name ?? "none"}; result={result?.GetType().Name ?? "null"}; actual=[{(result as TsStringValue)?.Value}]; expected=[{expected}]");
        Assert.Null(error);
        var actual = Assert.IsType<TsStringValue>(result).Value;
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"String.replaceAll result mismatch: expected=[{expected}]; actual=[{actual}]");
    }

    private static void AssertLoadArg(Instruction instruction, int index)
    {
        Assert.Equal(Opcode.LoadArg, instruction.Opcode);
        Assert.Equal(index, instruction.Operand0);
    }
}
