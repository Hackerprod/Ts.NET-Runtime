using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayLastIndexOfOmittedSearchTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omitted-dense-hit", "const a: any[] = [undefined,7,undefined]; return a.lastIndexOf();", 1, 2)]
    [InlineData("omitted-dense-miss", "return [7].lastIndexOf();", 1, -1)]
    [InlineData("omitted-sparse-miss", "const a=[7]; a[2]=7; return a.lastIndexOf();", 1, -1)]
    [InlineData("omitted-sparse-hit", "const a: any[] = [undefined]; a[2]=undefined; a[4]=7; return a.lastIndexOf();", 1, 2)]
    [InlineData("omitted-empty", "return [].lastIndexOf();", 1, -1)]
    [InlineData("omitted-holes-only", "const a: any[] = new Array(3); return a.lastIndexOf();", 1, -1)]
    [InlineData("explicit-undefined", "const a: any[] = [undefined,7,undefined]; return a.lastIndexOf(undefined);", 2, 2)]
    [InlineData("explicit-undefined-from-index", "const a: any[] = [7,undefined]; return a.lastIndexOf(undefined,undefined);", 3, -1)]
    [InlineData("explicit-from-index-strict", "const a: any[] = [7,'7',7]; return a.lastIndexOf(7,1);", 3, 0)]
    public void ArrayLastIndexOf_SearchesForUndefinedWhenSearchValueIsOmitted(string row, string body, int argc, int expected)
    {
        var source = $"function main(): number {{ {body} }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        var diagnostics = $"{parser.Diagnostics.Count()}/{binder.Diagnostics.All.Count()}";
        output.WriteLine($"Array.lastIndexOf row={row}; parser/binder={diagnostics}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "Array::lastIndexOf");
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::lastIndexOf", target);
        output.WriteLine($"Array.lastIndexOf row={row}; parser/binder={diagnostics}; target={target}; argc={call.Operand1}; expected={expected}");
        Assert.Equal(argc, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var exception = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", []); });
        if (exception is not null)
        {
            output.WriteLine($"Array.lastIndexOf row={row}; exception={exception.GetType().FullName}; message={exception.Message}");
            output.WriteLine(exception.StackTrace ?? "<no stack>");
        }
        Assert.Null(exception);

        output.WriteLine($"Array.lastIndexOf row={row}; resultType={result?.GetType().FullName}; expected={expected}; actual={(result as TsInt32Value)?.Value}");
        Assert.Equal(expected, Assert.IsType<TsInt32Value>(result).Value);
    }
}
