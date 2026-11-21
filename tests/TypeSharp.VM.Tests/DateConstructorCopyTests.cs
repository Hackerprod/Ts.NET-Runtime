using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class DateConstructorCopyTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("1234", 1234)]
    [InlineData("-5678", -5678)]
    public void ConstructFromNativeDate_CopiesTimestampIntoDistinctObject(string literal, double expected)
    {
        var source = $"function main(): any[] {{ const original = new Date({literal}); const copy = new Date(original); return [original, copy, original.getTime(), copy.getTime()]; }}";
        var result = Assert.IsType<TsArrayValue>(Execute(source, literal, 2, 1)).Value;
        Assert.Equal(4, result.Count);

        var original = Assert.IsType<TsObjectValue>(result.Get(0));
        var copy = Assert.IsType<TsObjectValue>(result.Get(1));
        Assert.Equal("Date", original.Value.TypeName);
        Assert.Equal("Date", copy.Value.TypeName);
        Assert.NotSame(original, copy);
        Assert.NotSame(original.Value, copy.Value);

        var originalTime = Assert.IsType<TsFloat64Value>(result.Get(2)).Value;
        var copyTime = Assert.IsType<TsFloat64Value>(result.Get(3)).Value;
        output.WriteLine($"copy source={literal}; independent Date wrappers and objects; original={originalTime}; copy={copyTime}; expected={expected}");
        Assert.Equal(expected, originalTime);
        Assert.Equal(expected, copyTime);
    }

    [Theory]
    [InlineData("1234", 1, 1234)]
    [InlineData("1970, 0, 1, 0, 0, 2, 0", 7, 2000)]
    public void ConstructFromOrdinaryArguments_PreservesNativeTimestamp(string arguments, int constructorArity, double expected)
    {
        var source = $"function main(): number {{ return new Date({arguments}).getTime(); }}";
        var actual = Assert.IsType<TsFloat64Value>(Execute(source, arguments, 1, constructorArity)).Value;
        output.WriteLine($"control arguments={arguments}; actual={actual}; expected={expected}");
        Assert.Equal(expected, actual);
    }

    private TsValue Execute(string source, string row, int count, int constructorArity)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var constructors = instructions.Where(i => i.Opcode == Opcode.NewObject).ToArray();
        var calls = instructions.Where(i => i.Opcode == Opcode.Call).ToArray();
        Assert.Equal(count, constructors.Length);
        Assert.Equal(count, calls.Length);
        Assert.All(constructors, i =>
        {
            Assert.Equal("Date", Assert.IsType<string>(i.OperandObject));
            Assert.Equal(constructorArity, i.Operand1);
        });
        Assert.All(calls, i =>
        {
            Assert.Equal("Date::getTime", Assert.IsType<string>(i.OperandObject));
            Assert.Equal(1, i.Operand1);
        });
        output.WriteLine($"row={row}; parser/binder=0/0; NewObject Date count={constructors.Length} argc={constructorArity}; Call Date::getTime count={calls.Length} argc=1");

        var bytecode = BytecodeCompiler.Compile(ir);
        return new TypeSharp.VM.Interpreter.Interpreter().Execute(bytecode, "main", []);
    }
}
