using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayIndexOfOmittedSearchTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omitted-dense-hit", "return [undefined].indexOf();", 1, 0)]
    [InlineData("omitted-dense-miss", "return [17].indexOf();", 1, -1)]
    [InlineData("omitted-sparse-hit", "const a = new Array(3); a[2] = undefined; return a.indexOf();", 1, 2)]
    [InlineData("omitted-holes-only", "return new Array(3).indexOf();", 1, -1)]
    [InlineData("omitted-empty", "return [].indexOf();", 1, -1)]
    [InlineData("explicit-undefined", "return [undefined].indexOf(undefined);", 2, 0)]
    [InlineData("explicit-sparse-undefined", "const a = new Array(3); a[2] = undefined; return a.indexOf(undefined);", 2, 2)]
    [InlineData("explicit-from-index", "return [7, 8, 7].indexOf(7, 1);", 3, 2)]
    public void ArrayIndexOf_SearchesForUndefinedWhenSearchValueIsOmitted(string row, string body, int argc, int expected)
    {
        var source = $"function main(): number {{ {body} }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        var diagnostics = $"{parser.Diagnostics.Count()}/{binder.Diagnostics.All.Count()}";
        output.WriteLine($"Array.indexOf row={row}; parser/binder={diagnostics}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "Array::indexOf");
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::indexOf", target);
        output.WriteLine($"Array.indexOf row={row}; parser/binder={diagnostics}; target={target}; argc={call.Operand1}; expected={expected}");
        Assert.Equal(argc, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var exception = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", []); });
        if (exception is not null)
        {
            output.WriteLine($"Array.indexOf row={row}; exception={exception.GetType().FullName}; message={exception.Message}");
            output.WriteLine(exception.StackTrace ?? "<no stack>");
        }
        Assert.Null(exception);

        output.WriteLine($"Array.indexOf row={row}; resultType={result?.GetType().FullName}; expected={expected}; actual={(result as TsInt32Value)?.Value}");
        Assert.Equal(expected, Assert.IsType<TsInt32Value>(result).Value);
    }
}
