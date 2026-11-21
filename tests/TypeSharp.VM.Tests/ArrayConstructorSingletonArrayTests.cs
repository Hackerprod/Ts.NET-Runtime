using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayConstructorSingletonArrayTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("call-dense")]
    [InlineData("call-empty")]
    [InlineData("call-sparse")]
    [InlineData("new-dense")]
    [InlineData("new-empty")]
    [InlineData("new-sparse")]
    public void Constructor_KeepsSingletonArrayArgument(string row)
    {
        var construct = row.StartsWith("call-", StringComparison.Ordinal) ? "Array(input)" : "new Array<any>(input)";
        var source = $"function main(input: any[]): any[] {{ return {construct}; }}";
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
        Assert.Equal(1, call.Operand1);

        var backing = new TsArray();
        string[] inputExpected;
        switch (row.Split('-')[1])
        {
            case "dense":
                backing.Add(TsValue.FromInt32(3));
                backing.Add(TsValue.FromInt32(4));
                inputExpected = ["I32:3", "I32:4"];
                break;
            case "empty":
                inputExpected = [];
                break;
            case "sparse":
                backing.SetHole(0);
                backing.Add(TsValue.Void);
                inputExpected = ["H:Void", "P:Void"];
                Assert.Same(TsValue.Void, backing.Get(0));
                Assert.Same(TsValue.Void, backing.Get(1));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(row));
        }
        var input = new TsArrayValue(backing);
        var inputBefore = Slots(backing);
        Assert.Equal(inputExpected, inputBefore);
        var elements = Enumerable.Range(0, backing.Count).Select(backing.Get).ToArray();
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = Assert.IsType<TsArrayValue>(vm.Execute(BytecodeCompiler.Compile(ir), "main", [input]));
        Assert.Same(backing, input.Value);
        var inputAfter = Slots(input.Value);
        Assert.Equal(inputBefore, inputAfter);
        for (var i = 0; i < elements.Length; i++)
            Assert.Same(elements[i], input.Value.Get(i));

        Assert.NotSame(input, result);
        Assert.NotSame(backing, result.Value);
        var first = result.Value.Count != 0 && !result.Value.IsHole(0) ? result.Value.Get(0) as TsArrayValue : null;
        var actual = new Vector(result.Value.Count, Slots(result.Value), ReferenceEquals(input, first), ReferenceEquals(backing, first?.Value));
        var expected = new Vector(1, ["P:Array"], true, true);
        var actualJson = JsonSerializer.Serialize(actual);
        var expectedJson = JsonSerializer.Serialize(expected);
        output.WriteLine("C105 native singleton row " + JsonSerializer.Serialize(new
        {
            row, route = "vm", source, parserDiagnostics = 0, binderDiagnostics = 0, target,
            argc = call.Operand1, inputCount = backing.Count, inputBefore, inputAfter, unchanged = true, fresh = true, actual, expected,
        }));
        Assert.True(actualJson == expectedJson, $"C105_ARRAY_SINGLETON_MISMATCH {row} expected={expectedJson} actual={actualJson}");
    }

    private sealed record Vector(int count, string[] slots, bool sameWrapper, bool sameBacking);

    private static string[] Slots(TsArray array) => Enumerable.Range(0, array.Count).Select(i => array.Get(i) switch
    {
        TsVoid => array.IsHole(i) ? "H:Void" : "P:Void",
        TsInt32Value number when !array.IsHole(i) => $"I32:{number.Value}",
        TsArrayValue when !array.IsHole(i) => "P:Array",
        var value => $"unexpected:{array.IsHole(i)}:{value.ValueType}",
    }).ToArray();
}
