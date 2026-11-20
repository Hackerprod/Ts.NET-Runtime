using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public class ArrayShiftEmptyTests
{
    private readonly ITestOutputHelper _output;

    public ArrayShiftEmptyTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("empty", "undefined", "[]")]
    [InlineData("drained", "undefined", "[]")]
    [InlineData("dense", "s:head", "[s:tail]")]
    [InlineData("null-head", "null", "[s:tail]")]
    [InlineData("undefined-head", "undefined", "[s:tail]")]
    [InlineData("sparse", "undefined", "[<hole>,s:tail]")]
    public void ArrayShift_EmptyReceiver(string row, string expected, string remainder)
    {
        var array = new TsArray();
        var tail = TsValue.FromString("tail");
        var head = TsValue.FromString("head");
        switch (row)
        {
            case "empty":
                break;
            case "drained":
                array.Add(head);
                array.RemoveAt(0);
                break;
            case "dense":
                array.Add(head);
                array.Add(tail);
                break;
            case "null-head":
                array.Add(TsValue.Null);
                array.Add(tail);
                break;
            case "undefined-head":
                array.Add(TsValue.Void);
                array.Add(tail);
                break;
            case "sparse":
                array.Set(2, tail);
                break;
            default:
                throw new ArgumentException(nameof(row));
        }
        var before = Snapshot(array);
        Assert.Equal(row switch
        {
            "empty" or "drained" => "[]",
            "dense" => "[s:head,s:tail]",
            "null-head" => "[null,s:tail]",
            "undefined-head" => "[undefined,s:tail]",
            "sparse" => "[<hole>,<hole>,s:tail]",
            _ => throw new ArgumentException(nameof(row))
        }, before);

        const string source = "function main(a: any[]): any { return a.shift(); }";
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
        Assert.Equal("Array::shift", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(1, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Encode(interpreter.Execute(bytecode, "main", [new TsArrayValue(array)]));
        var after = Snapshot(array);
        Assert.Equal(remainder, after);
        _output.WriteLine($"shift row={row}; parser/binder=0/0; target=Array::shift; argc={call.Operand1}; before={before}; after={after}; expected={expected}; actual={actual}");
        Assert.True(expected == actual, $"Array.shift result mismatch: expected={expected}; actual={actual}");
    }

    private static string Snapshot(TsArray array) =>
        "[" + string.Join(",", Enumerable.Range(0, array.Count)
            .Select(i => array.IsHole(i) ? "<hole>" : Encode(array.Get(i)))) + "]";

    private static string Encode(TsValue? value)
    {
        if (ReferenceEquals(value, TsValue.Void)) return "undefined";
        if (ReferenceEquals(value, TsValue.Null)) return "null";
        return "s:" + Assert.IsType<TsStringValue>(value).Value;
    }
}
