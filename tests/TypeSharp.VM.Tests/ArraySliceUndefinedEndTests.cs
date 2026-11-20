using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public class ArraySliceUndefinedEndTests
{
    private readonly ITestOutputHelper _output;

    public ArraySliceUndefinedEndTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("omitted-end", "1", 2, "1HU4", "HU4")]
    [InlineData("undefined-literal", "1, undefined", 3, "1HU4", "HU4")]
    [InlineData("undefined-argument", "-2, end", 3, "1HU4", "U4")]
    [InlineData("undefined-trailing-hole", "1, end", 3, "12H", "2H")]
    [InlineData("undefined-empty", "1, end", 3, "", "")]
    [InlineData("undefined-start-past-length", "4, end", 3, "1HU4", "")]
    [InlineData("explicit-zero", "1, 0", 3, "1HU4", "")]
    [InlineData("negative-end", "1, -1", 3, "1HU4", "HU")]
    [InlineData("positive-end", "1, 2", 3, "1HU4", "H")]
    public void ArraySlice_ExplicitUndefinedEnd(string row, string bounds, int argc, string input, string expected)
    {
        var array = new TsArray();
        foreach (char slot in input)
        {
            if (slot == 'H') array.SetHole(array.Count);
            else array.Add(slot == 'U' ? TsValue.Void : TsValue.FromInt32(slot - '0'));
        }
        Assert.Equal(input, Snapshot(array));
        var wrapper = new TsArrayValue(array);

        var source = $"function main(a: any[], end: number | undefined): any[] {{ return a.slice({bounds}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, f => f.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(b => b.Instructions), i => i.Opcode == Opcode.Call);
        Assert.Equal("Array::slice", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(argc, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var result = Assert.IsType<TsArrayValue>(interpreter.Execute(bytecode, "main", [wrapper, TsValue.Void]));
        Assert.NotSame(wrapper, result);
        Assert.NotSame(array, result.Value);
        var receiver = Snapshot(array);
        Assert.Equal(input, receiver);
        var actual = Snapshot(result.Value);
        _output.WriteLine($"slice row={row}; parser/binder=0/0; target=Array::slice; argc={call.Operand1}; receiver=[{receiver}]; expected=[{expected}]; actual=[{actual}]");
        Assert.True(expected == actual, $"Array.slice result mismatch: expected=[{expected}]; actual=[{actual}]");
    }

    private static string Snapshot(TsArray array) => string.Concat(Enumerable.Range(0, array.Count).Select(i =>
    {
        if (array.IsHole(i)) return 'H';
        var value = array.Get(i);
        return ReferenceEquals(value, TsValue.Void) ? 'U' : (char)('0' + Assert.IsType<TsInt32Value>(value).Value);
    }));
}
