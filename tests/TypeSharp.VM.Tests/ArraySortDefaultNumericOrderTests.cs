using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArraySortDefaultNumericOrderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omitted-Int32-number", "number", "", "2,10,1", "1,10,2")]
    [InlineData("explicit-ascending-comparator", "number", "(a: number, b: number): number => a - b", "2,10,1", "1,2,10")]
    [InlineData("omitted-string", "string", "", "s2,s10,s1", "s1,s10,s2")]
    [InlineData("omitted-mixed-legacy-control", "any", "", "2,s3,10,1", "s3,1,2,10")]
    public void Sort_UsesDefaultTextOrderForInt32WithoutChangingControls(
        string row, string type, string comparator, string input, string expected)
    {
        var source = $$"""
            function main(values: {{type}}[]): {{type}}[] {
                return values.sort({{comparator}});
            }
            """;
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        output.WriteLine($"row={row}; parser/binder={parser.Diagnostics.Count()}/{binder.Diagnostics.All.Count()}");
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("Array::sort", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(comparator.Length == 0 ? 1 : 2, call.Operand1);
        var provenance = "omitted";
        if (comparator.Length != 0)
        {
            var load = instructions[Array.IndexOf(instructions, call) - 1];
            Assert.Equal(Opcode.LoadFunc, load.Opcode);
            var name = Assert.IsType<string>(load.OperandObject);
            Assert.Single(ir.Functions, function => function.Name == name && function != main);
            provenance = $"LoadFunc({name})";
        }
        output.WriteLine($"row={row}; target={call.OperandObject}; argc={call.Operand1}; comparator={provenance}");

        var initial = ParseSlots(input);
        var expectedSlots = ParseSlots(expected);
        var array = new TsArray(initial.Length);
        foreach (var slot in initial)
            array.Add(slot);
        output.WriteLine($"row={row}; input=[{string.Join(",", initial.Select(Describe))}]");
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var value = vm.Execute(BytecodeCompiler.Compile(ir), "main", [new TsArrayValue(array)]);
        output.WriteLine($"row={row}; returned={value?.GetType().Name}; " +
            $"same={value is TsArrayValue result && ReferenceEquals(array, result.Value)}");
        var actual = Assert.IsType<TsArrayValue>(value).Value;
        var indexes = Enumerable.Range(0, actual.Count).ToArray();
        var mask = indexes.Select(actual.IsHole).ToArray();
        output.WriteLine($"row={row}; count expected/actual={expectedSlots.Length}/{actual.Count}; " +
            $"mask expected/actual=[{new string('P', expectedSlots.Length)}]/" +
            $"[{string.Concat(mask.Select(hole => hole ? 'H' : 'P'))}]");
        output.WriteLine($"row={row}; slots expected/actual=[{string.Join(",", expectedSlots.Select(Describe))}]/" +
            $"[{string.Join(",", indexes.Select(i => mask[i] ? "hole" : Describe(actual.Get(i))))}]");

        Assert.Same(array, actual);
        Assert.Equal(expectedSlots.Length, actual.Count);
        Assert.Equal(new bool[expectedSlots.Length], mask);
        var actualSlots = indexes.Select(actual.Get).ToArray();
        for (var i = 0; i < expectedSlots.Length; i++)
            Assert.IsType(expectedSlots[i].GetType(), actualSlots[i]);
        Assert.Equal(expectedSlots.Select(Primitive).ToArray(), actualSlots.Select(Primitive).ToArray());
    }

    private static TsValue[] ParseSlots(string text) => text.Split(',').Select(slot => slot[0] == 's'
        ? TsValue.FromString(slot[1..])
        : TsValue.FromInt32(int.Parse(slot, System.Globalization.CultureInfo.InvariantCulture))).ToArray();

    private static object Primitive(TsValue value) => value switch
    {
        TsInt32Value number => (object)number.Value,
        TsStringValue text => text.Value,
        _ => throw new InvalidOperationException("Unexpected primitive type"),
    };

    private static string Describe(TsValue? value) => value switch
    {
        TsInt32Value number => $"Int32({number.Value})",
        TsStringValue text => $"String(\"{text.Value}\")",
        _ => value?.GetType().Name ?? "<null>",
    };
}
