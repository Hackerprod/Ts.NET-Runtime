using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayConstructorSparseLengthTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("positive-length", "2", 1, "HH")]
    [InlineData("zero-length", "0", 1, "")]
    [InlineData("no-arguments", "", 0, "")]
    [InlineData("explicit-values", "2, 4", 2, "24")]
    public void Constructor_PreservesLengthAndSlots(string row, string arguments, int argc, string expected)
    {
        var source = $"function main(): number[] {{ return new Array<number>({arguments}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, item => item.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), item => item.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::ctor", target);
        Assert.Equal(argc, call.Operand1);

        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var array = Assert.IsType<TsArrayValue>(vm.Execute(BytecodeCompiler.Compile(ir), "main", [])).Value;
        var expectedMask = expected.Select(entry => entry == 'H').ToArray();
        var actualMask = Enumerable.Range(0, array.Count).Select(array.IsHole).ToArray();
        var expectedSlots = expected.Select(entry => entry == 'H' ? "hole" : $"Int32({entry})");
        var actualSlots = actualMask.Select((hole, i) => hole ? "hole" : array.Get(i) switch
        {
            TsNull => "TsNull(null)",
            TsInt32Value value => $"Int32({value.Value})",
            var value => $"{value.GetType().Name}({value})",
        });
        output.WriteLine($"row={row}; parser/binder=0/0; target={target}; argc={call.Operand1}; " +
            $"count expected/actual={expected.Length}/{array.Count}; " +
            $"mask expected/actual={Mask(expectedMask)}/{Mask(actualMask)}; " +
            $"slots expected/actual=[{string.Join(",", expectedSlots)}]/[{string.Join(",", actualSlots)}]");

        Assert.Equal(expected.Length, array.Count);
        Assert.Equal(expectedMask, actualMask);
        for (var i = 0; i < expected.Length; i++)
            if (!expectedMask[i])
                Assert.Equal(expected[i] - '0', Assert.IsType<TsInt32Value>(array.Get(i)).Value);
    }

    private static string Mask(IEnumerable<bool> holes) =>
        $"[{string.Join(",", holes.Select(hole => hole ? "H" : "P"))}]";
}
