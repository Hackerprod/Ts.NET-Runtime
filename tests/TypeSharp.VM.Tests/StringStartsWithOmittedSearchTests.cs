using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringStartsWithOmittedSearchTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omitted-hit")]
    [InlineData("omitted-miss")]
    [InlineData("explicit-undefined")]
    [InlineData("explicit-undefined-position-hit")]
    [InlineData("explicit-undefined-position-miss")]
    [InlineData("explicit-empty")]
    public void StringStartsWith_SearchesForUndefinedWhenSearchValueIsOmitted(string row)
    {
        var (expression, expectedArgc, expected) = row switch
        {
            "omitted-hit" => ("'undefined-value'.startsWith()", 1, true),
            "omitted-miss" => ("'xundefined'.startsWith()", 1, false),
            "explicit-undefined" => ("'undefined-value'.startsWith(undefined)", 2, true),
            "explicit-undefined-position-hit" => ("'xundefined'.startsWith(undefined, 1)", 3, true),
            "explicit-undefined-position-miss" => ("'xundefined'.startsWith(undefined, 0)", 3, false),
            "explicit-empty" => ("'abc'.startsWith('')", 2, true),
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
        Assert.Equal("String::startsWith", target);
        Assert.Equal(expectedArgc, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var exception = Record.Exception(() =>
        {
            result = interpreter.Execute(bytecode, "main", []);
        });

        output.WriteLine("C164 String.startsWith omitted search " + JsonSerializer.Serialize(new
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
