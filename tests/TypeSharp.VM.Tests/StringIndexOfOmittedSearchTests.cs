using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringIndexOfOmittedSearchTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omitted-hit", "'xundefined'.indexOf()", 1, 1)]
    [InlineData("omitted-miss", "'abc'.indexOf()", 1, -1)]
    [InlineData("explicit-undefined", "'xundefined'.indexOf(undefined)", 2, 1)]
    [InlineData("explicit-empty", "'abc'.indexOf('')", 2, 0)]
    [InlineData("explicit-position", "'ababa'.indexOf('ba', 2)", 3, 3)]
    public void StringIndexOf_SearchesForUndefinedWhenSearchValueIsOmitted(string row, string expression, int argc, int expected)
    {
        var source = $"function main(): int32 {{ return {expression}; }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        var diagnostics = $"{parser.Diagnostics.Count()}/{binder.Diagnostics.All.Count()}";
        output.WriteLine($"String.indexOf row={row}; parser/binder={diagnostics}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "String::indexOf");
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("String::indexOf", target);
        Assert.Equal(argc, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        output.WriteLine($"String.indexOf row={row}; parser/binder={diagnostics}; target={target}; argc={call.Operand1}; expected={expected}");
        TsValue? result = null;
        var exception = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", []); });
        if (exception is not null)
        {
            output.WriteLine($"String.indexOf row={row}; exception={exception.GetType().FullName}; message={exception.Message}");
            output.WriteLine(exception.StackTrace ?? "<no stack>");
        }
        Assert.Null(exception);

        output.WriteLine($"String.indexOf row={row}; resultType={result?.GetType().FullName}; expected={expected}; actual={(result as TsInt32Value)?.Value}");
        Assert.Equal(expected, Assert.IsType<TsInt32Value>(result).Value);
    }
}
