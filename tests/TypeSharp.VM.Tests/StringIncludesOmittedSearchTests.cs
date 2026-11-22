using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringIncludesOmittedSearchTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omitted-hit")]
    [InlineData("omitted-miss")]
    [InlineData("explicit-undefined")]
    [InlineData("explicit-undefined-position")]
    [InlineData("explicit-empty")]
    public void StringIncludes_SearchesForUndefinedWhenSearchValueIsOmitted(string row)
    {
        var (expression, expectedArgc, expected) = row switch
        {
            "omitted-hit" => ("'xundefined'.includes()", 1, true),
            "omitted-miss" => ("'abc'.includes()", 1, false),
            "explicit-undefined" => ("'xundefined'.includes(undefined)", 2, true),
            "explicit-undefined-position" => ("'xundefined'.includes(undefined, 2)", 3, false),
            "explicit-empty" => ("'abc'.includes('')", 2, true),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, null),
        };

        var source = $"function main(): boolean {{ return {expression}; }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("String::includes", target);
        Assert.Equal(expectedArgc, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var exception = Record.Exception(() =>
        {
            result = interpreter.Execute(bytecode, "main", []);
        });

        output.WriteLine("C163 String.includes omitted search " + JsonSerializer.Serialize(new
        {
            row,
            expression,
            parserCount = parser.Diagnostics.Count(),
            binderCount = binder.Diagnostics.All.Count(),
            target,
            argc = call.Operand1,
            expected,
            resultType = result?.GetType().FullName,
            actual = (result as TsBoolValue)?.Value,
            exceptionType = exception?.GetType().FullName,
            exceptionMessage = exception?.Message,
            innerExceptionType = exception?.InnerException?.GetType().FullName,
            innerExceptionMessage = exception?.InnerException?.Message,
            exceptionStack = exception?.StackTrace,
        }));

        Assert.Null(exception);
        Assert.Equal(expected, Assert.IsType<TsBoolValue>(result).Value);
    }
}
