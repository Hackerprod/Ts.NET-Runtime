using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayMapCallbackReceiverTests(ITestOutputHelper output)
{
    [Fact]
    public void ArrayMap_PassesTheOriginalReceiver()
    {
        const string source = """
            function main(): string[] {
                const values: number[] = [2, 4];
                return values.map((value: number, index: number, source: number[]): string => {
                    if (source === values) return "same";
                    return "other";
                });
            }
            """;

        var mapped = ExecuteMap(source, []);
        Assert.Equal(2, mapped.Count);
        Assert.False(mapped.IsHole(0));
        Assert.False(mapped.IsHole(1));
        var actual = Enumerable.Range(0, mapped.Count)
            .Select(i => Assert.IsType<TsStringValue>(mapped.Get(i)).Value).ToArray();
        output.WriteLine($"row=original-receiver; parser/binder=0/0; target=Array::map; argc=2; expected=[same,same]; actual=[{string.Join(",", actual)}]");
        Assert.Equal(new[] { "same", "same" }, actual);
    }

    [Theory]
    [InlineData("short-dense", "24", "2041")]
    [InlineData("sparse", "2H4", "2042")]
    [InlineData("empty", "", "")]
    public void ArrayMap_PreservesTraversalForShortCallbacks(string row, string input, string expectedVisits)
    {
        const string source = """
            function main(values: number[], visits: number[]): number[] {
                return values.map((value: number, index: number): number => {
                    visits.push(value);
                    visits.push(index);
                    return value;
                });
            }
            """;
        var values = new TsArray();
        foreach (var entry in input)
        {
            if (entry == 'H')
                values.SetHole(values.Count);
            else
                values.Add(TsValue.FromInt32(entry - '0'));
        }

        var visits = new TsArray();
        AssertSlots(input, values);
        var mapped = ExecuteMap(source, [new TsArrayValue(values), new TsArrayValue(visits)]);
        output.WriteLine($"row={row}; parser/binder=0/0; target=Array::map; argc=2; expected={Format(input)}/{Format(expectedVisits)}/{Format(input)}; actual={Snapshot(values)}/{Snapshot(visits)}/{Snapshot(mapped)} (receiver/visits/mapped)");
        Assert.NotSame(values, mapped);
        AssertSlots(input, values);
        AssertSlots(expectedVisits, visits);
        AssertSlots(input, mapped);
    }

    private static TsArray ExecuteMap(string source, TsValue[] args)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, item => item.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), item => item.Opcode == Opcode.Call);
        Assert.Equal("Array::map", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(2, call.Operand1);

        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        return Assert.IsType<TsArrayValue>(vm.Execute(BytecodeCompiler.Compile(ir), "main", args)).Value;
    }

    private static void AssertSlots(string expected, TsArray array)
    {
        Assert.Equal(expected.Length, array.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i] == 'H', array.IsHole(i));
            if (expected[i] != 'H')
                Assert.Equal(expected[i] - '0', Assert.IsType<TsInt32Value>(array.Get(i)).Value);
        }
    }

    private static string Format(string entries) => $"[{string.Join(",", entries.AsEnumerable())}]";

    private static string Snapshot(TsArray array) => $"[{string.Join(",", Enumerable.Range(0, array.Count)
        .Select(i => array.IsHole(i) ? "H" : Assert.IsType<TsInt32Value>(array.Get(i)).Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))}]";
}
