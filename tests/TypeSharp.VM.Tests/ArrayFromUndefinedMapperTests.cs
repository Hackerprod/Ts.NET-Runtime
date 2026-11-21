using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFromUndefinedMapperTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omitted", "", 1, 4)]
    [InlineData("explicit-undefined", ", undefined", 2, 4)]
    [InlineData("callable", ", (v: int32, i: int32) => v + i", 2, 5)]
    [InlineData("explicit-null", ", null", 2, null)]
    public void ArrayFrom_TreatsUndefinedMapperAsOmitted(string row, string suffix, int argc, int? expectedSecond)
    {
        var source = $"function main(): any[] {{ let source: any = {{ length: 2, 0: 3, 1: 4 }}; return Array.from(source{suffix}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, item => item.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), item => item.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::from", target);
        Assert.Equal(argc, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);

        const string expectedMessage = "Value 'null' is not callable";
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var error = Record.Exception(() => { result = vm.Execute(bytecode, "main", []); });
        int[]? actual = null;
        if (error is null)
        {
            var array = Assert.IsType<TsArrayValue>(result).Value;
            Assert.Equal(2, array.Count);
            actual = [Assert.IsType<TsInt32Value>(array.Get(0)).Value, Assert.IsType<TsInt32Value>(array.Get(1)).Value];
        }

        var expectedDisplay = expectedSecond is int second ? $"array:[3,{second}]" : $"throw:InvalidOperationException: {expectedMessage}";
        var actualDisplay = error is null ? $"array:[{string.Join(",", actual!)}]" : $"throw:{error.GetType().Name}: {error.Message}";
        output.WriteLine($"Array.from row={row}; parser/binder=0/0; target={target}; argc={call.Operand1}; expected={expectedDisplay}; actual={actualDisplay}");
        if (expectedSecond is int expected)
        {
            Assert.True(error is null, $"Array.from VM execution failed: row={row}; actual={actualDisplay}");
            Assert.Equal(new[] { 3, expected }, actual);
        }
        else
        {
            Assert.Equal(expectedMessage, Assert.IsType<InvalidOperationException>(error).Message);
        }
    }
}
