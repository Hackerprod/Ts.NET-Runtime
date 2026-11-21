using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayIncludesOmittedTargetTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omitted-dense", "[1].includes()", 1, false)]
    [InlineData("omitted-hole", "new Array(1).includes()", 1, true)]
    [InlineData("explicit-undefined-hole", "new Array(1).includes(undefined)", 2, true)]
    [InlineData("omitted-empty", "[].includes()", 1, false)]
    [InlineData("explicit-hit", "[1, 2].includes(2, 1)", 3, true)]
    [InlineData("explicit-miss", "[1, 2].includes(1, 1)", 3, false)]
    public void ArrayIncludes_SearchesForUndefinedWhenTargetIsOmitted(string row, string expression, int argc, bool expected)
    {
        var source = $"function main(): boolean {{ return {expression}; }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "Array::includes");
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::includes", target);
        Assert.Equal(argc, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        output.WriteLine($"Array.includes row={row}; parser/binder=0/0; target={target}; argc={call.Operand1}; expected={expected}");
        TsValue? result = null;
        var exception = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", []); });
        if (exception is not null)
        {
            output.WriteLine($"Array.includes row={row}; exception={exception.GetType().FullName}; message={exception.Message}");
            output.WriteLine(exception.StackTrace ?? "<no stack>");
        }
        Assert.Null(exception);

        output.WriteLine($"Array.includes row={row}; resultType={result?.GetType().FullName}; expected={expected}; actual={(result as TsBoolValue)?.Value}");
        Assert.Equal(expected, Assert.IsType<TsBoolValue>(result).Value);
    }
}
