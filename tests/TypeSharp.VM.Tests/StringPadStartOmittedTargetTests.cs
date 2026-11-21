using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringPadStartOmittedTargetTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omitted-target", "", 1, "ABC")]
    [InlineData("explicit-undefined", "undefined", 2, "ABC")]
    [InlineData("no-growth", "2, \"x\"", 3, "ABC")]
    [InlineData("pad-growth", "5, \"x\"", 3, "xxABC")]
    public void StringPadStart_ReturnsReceiverWhenTargetIsOmitted(string row, string arguments, int argc, string expected)
    {
        var source = $"function main(): string {{ return \"ABC\".padStart({arguments}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("String::padStart", target);
        Assert.Equal(argc, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        output.WriteLine($"String.padStart row={row}; parser/binder=0/0; target={target}; argc={call.Operand1}; expected=[{expected}]");
        TsValue? result = null;
        var exception = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", []); });
        if (exception is not null)
        {
            output.WriteLine($"String.padStart row={row}; exception={exception.GetType().FullName}; message={exception.Message}");
            output.WriteLine(exception.StackTrace ?? "<no stack>");
        }
        Assert.Null(exception);

        output.WriteLine($"String.padStart row={row}; resultType={result?.GetType().FullName}; expected=[{expected}]; actual=[{(result as TsStringValue)?.Value}]");
        var actual = Assert.IsType<TsStringValue>(result).Value;
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"String.padStart result mismatch: expected=[{expected}]; actual=[{actual}]");
    }
}
