using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public class MathEmptyExtremaTests
{
    private readonly ITestOutputHelper _output;
    public MathEmptyExtremaTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("min", "7FF0000000000000")]
    [InlineData("max", "FFF0000000000000")]
    public void EmptyCall_ReturnsIdentity(string method, string expected)
    {
        var source = $"function main(): number {{ return Math.{method}(); }}";
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
        Assert.Equal($"Math::{method}", target);
        Assert.Equal(0, call.Operand1);
        _output.WriteLine($"Math.{method}; parser/binder=0/0; target={target}; argc={call.Operand1}");

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var value = Assert.IsType<TsFloat64Value>(interpreter.Execute(bytecode, "main", Array.Empty<TsValue>())).Value;
        var actual = BitConverter.DoubleToInt64Bits(value).ToString("X16");
        _output.WriteLine($"Math.{method}; result=Float64; expected={expected}; actual={actual}");
        Assert.Equal(expected, actual);
    }
}
