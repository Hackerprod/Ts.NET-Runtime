using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFilterCallbackReceiverTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("dense-identity", "25", "2051", "5", true, "source === a && x === 5 && i === 1")]
    [InlineData("dense-two-formal", "25", "2051", "5", false, "x === 5 && i === 1")]
    [InlineData("sparse-two-formal", "2H5", "2052", "5", false, "x === 5 && i === 2")]
    [InlineData("empty-two-formal", "", "", "", false, "true")]
    public void Filter_PassesOriginalReceiverAndPreservesSlots(string row, string initial,
        string expectedVisits, string expectedResult, bool withReceiver, string predicate)
    {
        var formals = withReceiver ? "x: number, i: number, source: number[]" : "x: number, i: number";
        var source = $$"""
            function main(a: number[], visits: number[]): number[] {
                return a.filter(({{formals}}): boolean => {
                    visits.push(x);
                    visits.push(i);
                    return {{predicate}};
                });
            }
            """;
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::filter", target);
        Assert.Equal(2, call.Operand1);

        var array = new TsArray(initial.Length);
        for (var i = 0; i < initial.Length; i++)
        {
            if (initial[i] == 'H') array.SetHole(i);
            else array.Add(TsValue.FromInt32(initial[i] - '0'));
        }
        var visits = new TsArray();
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var filtered = Assert.IsType<TsArrayValue>(vm.Execute(BytecodeCompiler.Compile(ir), "main",
            [new TsArrayValue(array), new TsArrayValue(visits)])).Value;

        output.WriteLine($"row={row}; parser/binder=0/0; target={target}; argc={call.Operand1}; " +
            $"formals={(withReceiver ? 3 : 2)}; fresh={!ReferenceEquals(array, filtered)}");
        output.WriteLine(Observation("source", initial, array));
        output.WriteLine(Observation("visits", expectedVisits, visits));
        output.WriteLine(Observation("result", expectedResult, filtered));
        Assert.NotSame(array, filtered);
        AssertSlots(initial, array);
        AssertSlots(expectedVisits, visits);
        AssertSlots(expectedResult, filtered);
    }

    private static string Observation(string name, string expected, TsArray actual)
    {
        var mask = Enumerable.Range(0, actual.Count).Select(actual.IsHole).ToArray();
        var slots = mask.Select((hole, i) => hole ? "hole" : actual.Get(i) switch
        {
            TsInt32Value value => $"Int32({value.Value})",
            var other => other?.GetType().Name ?? "<null>",
        });
        var expectedSlots = expected.Select(slot => slot == 'H' ? "hole" : $"Int32({slot})");
        return $"{name} count expected/actual={expected.Length}/{actual.Count}; " +
            $"mask expected/actual=[{string.Join(",", expected.Select(slot => slot == 'H' ? "H" : "P"))}]/" +
            $"[{string.Join(",", mask.Select(hole => hole ? "H" : "P"))}]; " +
            $"slots expected/actual=[{string.Join(",", expectedSlots)}]/[{string.Join(",", slots)}]";
    }

    private static void AssertSlots(string expected, TsArray actual)
    {
        Assert.Equal(expected.Length, actual.Count);
        Assert.Equal(expected.Select(slot => slot == 'H').ToArray(),
            Enumerable.Range(0, actual.Count).Select(actual.IsHole).ToArray());
        for (var i = 0; i < expected.Length; i++)
            if (expected[i] != 'H')
                Assert.Equal(expected[i] - '0', Assert.IsType<TsInt32Value>(actual.Get(i)).Value);
    }
}
