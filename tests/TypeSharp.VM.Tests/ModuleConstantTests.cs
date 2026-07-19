using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.VM.Tests;

public class ModuleConstantTests
{
    [Fact]
    public void TopLevelConstNumber_IsAvailableInsideFunction()
    {
        var module = Compile("""
            const status = 3;

            function main(): number {
                return status;
            }
        """);

        var interpreter = new Interpreter.Interpreter();
        var result = interpreter.Execute(module, "main");

        Assert.NotNull(result);
        Assert.IsType<TsFloat64Value>(result);
        Assert.Equal(3.0, ((TsFloat64Value)result).Value);
    }

    [Fact]
    public void TopLevelConstAnnotatedInt32_IsAvailableInsideFunction()
    {
        var module = Compile("""
            const status: int32 = 3;

            function main(): int32 {
                return status;
            }
        """);

        var interpreter = new Interpreter.Interpreter();
        var result = interpreter.Execute(module, "main");

        Assert.NotNull(result);
        Assert.IsType<TsInt32Value>(result);
        Assert.Equal(3, ((TsInt32Value)result).Value);
    }

    [Fact]
    public void TopLevelConstString_IsAvailableInsideFunction()
    {
        var module = Compile("""
            const expected = "4006";

            function main(): string {
                return expected;
            }
        """);

        var interpreter = new Interpreter.Interpreter();
        var result = interpreter.Execute(module, "main");

        Assert.NotNull(result);
        Assert.IsType<TsStringValue>(result);
        Assert.Equal("4006", ((TsStringValue)result).Value);
    }

    private static BytecodeModule Compile(string code)
    {
        var lexer = new Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        var syntaxTree = parser.Parse();
        var binder = new Semantics.Binder.Binder();
        var boundTree = binder.Bind(syntaxTree);
        var irGen = new IRGenerator();
        var moduleIR = irGen.Generate(boundTree);
        return BytecodeCompiler.Compile(moduleIR);
    }
}
