using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public class ArrayJoinBooleanElementsTests
{
    private readonly ITestOutputHelper _output;
    public ArrayJoinBooleanElementsTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("direct-booleans-custom", 2, "true|false")]
    [InlineData("non-boolean-omitted", 1, "7,,x")]
    [InlineData("non-boolean-explicit-undefined", 2, "x,,,7")]
    public void ArrayJoin_LowercasesOnlyDirectBooleanFields(string row, int argc, string expected)
    {
        var array = new TsArray();
        TsValue? separator;
        string source;
        switch (row)
        {
            case "direct-booleans-custom":
                array.Add(TsValue.FromBool(true));
                array.Add(TsValue.FromBool(false));
                Assert.True(Assert.IsType<TsBoolValue>(array.Get(0)).Value);
                Assert.False(Assert.IsType<TsBoolValue>(array.Get(1)).Value);
                separator = TsValue.FromString("|");
                Assert.Equal("|", Assert.IsType<TsStringValue>(separator).Value);
                source = "function main(a: boolean[], sep: string): string { return a.join(sep); }";
                break;
            case "non-boolean-omitted":
                array.Add(TsValue.FromInt32(7));
                array.Add(TsValue.Null);
                array.Add(TsValue.FromString("x"));
                Assert.Equal(7, Assert.IsType<TsInt32Value>(array.Get(0)).Value);
                Assert.Same(TsValue.Null, array.Get(1));
                Assert.Equal("x", Assert.IsType<TsStringValue>(array.Get(2)).Value);
                separator = null;
                source = "function main(a: any[]): string { return a.join(); }";
                break;
            case "non-boolean-explicit-undefined":
                array.Add(TsValue.FromString("x"));
                array.SetHole(1);
                array.Add(TsValue.Void);
                array.Add(TsValue.FromInt32(7));
                Assert.Equal("x", Assert.IsType<TsStringValue>(array.Get(0)).Value);
                Assert.Same(TsValue.Void, array.Get(1));
                Assert.Same(TsValue.Void, array.Get(2));
                Assert.Equal(7, Assert.IsType<TsInt32Value>(array.Get(3)).Value);
                separator = TsValue.Void;
                source = "function main(a: any[], sep: any): string { return a.join(sep); }";
                break;
            default:
                throw new ArgumentException(nameof(row));
        }
        Assert.Equal(row switch
        {
            "direct-booleans-custom" => "00",
            "non-boolean-omitted" => "000",
            _ => "0100"
        }, string.Concat(Enumerable.Range(0, array.Count).Select(i => array.IsHole(i) ? "1" : "0")));
        var receiver = new TsArrayValue(array);
        TsValue[] args = separator is null ? [receiver] : [receiver, separator];
        Assert.Equal(argc, args.Length);
        if (row == "non-boolean-explicit-undefined")
            Assert.Same(TsValue.Void, args[1]);

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
        Assert.Equal("Array::join", target);
        Assert.Equal(argc, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsStringValue>(interpreter.Execute(bytecode, "main", args));
        var indices = Enumerable.Range(0, array.Count);
        var slots = string.Join(",", indices.Select(i => Describe(array.Get(i))));
        var holes = string.Concat(indices.Select(i => array.IsHole(i) ? "1" : "0"));
        var separatorDescription = args.Length == 1 ? "omitted" : Describe(args[1]);
        _output.WriteLine($"Array.join row={row}; slots=[{slots}]; holes={holes}; separator={separatorDescription}; parser/binder=0/0; target={target}; argc={call.Operand1}; result={actual.GetType().Name}; expected={JsonSerializer.Serialize(expected)}; actual={JsonSerializer.Serialize(actual.Value)}");
        Assert.True(string.Equals(expected, actual.Value, StringComparison.Ordinal),
            $"Array.join element mismatch: expected={JsonSerializer.Serialize(expected)}; actual={JsonSerializer.Serialize(actual.Value)}");
    }

    private static string Describe(TsValue value) => $"{value.GetType().Name}:{JsonSerializer.Serialize(value.ToString())}";
}
