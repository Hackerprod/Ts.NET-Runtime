using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFlatTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("dense", "", 1, "[[12][3]]", "[123]")]
    [InlineData("default-depth", "", 1, "[1[2[3]]4]", "[12[3]4]")]
    [InlineData("undefined", "undefined", 2, "[1[2[3]]4]", "[12[3]4]")]
    [InlineData("zero-with-hole", "0", 2, "[H1[2[3]]4]", "[1[2[3]]4]")]
    [InlineData("depth-two", "2", 2, "[1[2[3]]4]", "[1234]")]
    [InlineData("sparse-visited-levels", "1", 2, "[H[HU2]3]", "[U23]")]
    [InlineData("empty", "", 1, "[]", "[]")]
    [InlineData("negative", "-1", 2, "[1[2[3]]4]", "[1[2[3]]4]")]
    [InlineData("fractional", "1.8", 2, "[1[2[3]]4]", "[12[3]4]")]
    public void Flat_OrdinaryNativeArrays(string row, string depth, int argc, string input, string expected)
    {
        int position = 0;
        var receiver = MakeArray(input, ref position);
        Assert.Equal(input, Snapshot(receiver.Value));
        TsArrayValue? survivor = row switch
        {
            "zero-with-hole" => Assert.IsType<TsArrayValue>(receiver.Value.Get(2)),
            "negative" => Assert.IsType<TsArrayValue>(receiver.Value.Get(1)),
            "default-depth" or "undefined" or "fractional" =>
                Assert.IsType<TsArrayValue>(Assert.IsType<TsArrayValue>(receiver.Value.Get(1)).Value.Get(1)),
            _ => null
        };

        var source = $"function main(a: any[]): any[] {{ return a.flat({depth}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, f => f.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(b => b.Instructions), i => i.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::flat", target);
        Assert.Equal(argc, call.Operand1);
        output.WriteLine($"flat row={row}; parser/binder=0/0; target={target}; argc={call.Operand1}");

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue value;
        try
        {
            value = vm.Execute(bytecode, "main", [receiver]);
        }
        catch (Exception exception)
        {
            output.WriteLine($"flat row={row}; exception={exception.GetType().FullName}: {exception.Message}");
            throw;
        }

        var result = Assert.IsType<TsArrayValue>(value);
        Assert.NotSame(receiver, result);
        Assert.NotSame(receiver.Value, result.Value);
        var actual = Snapshot(result.Value);
        var after = Snapshot(receiver.Value);
        output.WriteLine($"flat row={row}; receiver={after}; expected={expected}; actual={actual}");
        Assert.Equal(input, after);
        Assert.Equal(expected, actual);
        if (survivor is not null)
            Assert.Same(survivor, result.Value.Get(row is "zero-with-hole" or "negative" ? 1 : 2));
    }

    private static TsArrayValue MakeArray(string slots, ref int offset)
    {
        var array = new TsArray();
        offset++; // '['
        while (slots[offset] != ']')
        {
            if (slots[offset] == '[')
            {
                array.Add(MakeArray(slots, ref offset));
                continue;
            }
            char slot = slots[offset++];
            if (slot == 'H') array.SetHole(array.Count);
            else array.Add(slot == 'U' ? TsValue.Void : TsValue.FromInt32(slot - '0'));
        }
        offset++; // ']'
        return new TsArrayValue(array);
    }

    private static string Snapshot(TsArray array) => "[" + string.Concat(
        Enumerable.Range(0, array.Count).Select(i =>
        {
            if (array.IsHole(i)) return "H";
            var value = array.Get(i);
            return value switch
            {
                TsArrayValue nested => Snapshot(nested.Value),
                _ when ReferenceEquals(value, TsValue.Void) => "U",
                _ => Assert.IsType<TsInt32Value>(value).Value.ToString()
            };
        })) + "]";
}
