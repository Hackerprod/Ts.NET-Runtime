using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArraySliceHexTextBoundsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("hex-start", "[10,20,30]", "'0x1' as any", 2, "20,30")]
    [InlineData("hex-end", "[10,20,30]", "0, '0x2' as any", 3, "10,20")]
    [InlineData("hex-large-start", "[10,20,30]", "'0x10000000000000000000000000000' as any", 2, "")]
    [InlineData("hex-mixed-bound", "[10,20,30,40]", "'  0X1  ' as any, -1", 3, "20,30")]
    [InlineData("hex-large-end", "[10,20,30]", "1, '0x10000000000000000000000000000' as any", 3, "20,30")]
    [InlineData("hex-sparse", "values", "'0x1' as any, '0x4' as any", 3, "H,U,O")]
    [InlineData("hex-zero", "[10,20,30]", "1, '0x0' as any", 3, "")]
    [InlineData("hex-numeric-start", "[10,20,30]", "0x1", 2, "20,30")]
    [InlineData("hex-bad", "[10,20,30]", "'0xg' as any", 2, "10,20,30")]
    [InlineData("hex-plus", "[10,20,30]", "'+0x1' as any", 2, "10,20,30")]
    [InlineData("hex-minus", "[10,20,30]", "'-0x1' as any", 2, "10,20,30")]
    [InlineData("hex-invalid-tail", "[10,20,30]", "'0x10000000000000000000000000000g' as any", 2, "10,20,30")]
    [InlineData("decimal-text", "[10,20,30]", "'1' as any, '2' as any", 3, "20")]
    [InlineData("negative-number", "[10,20,30]", "-2", 2, "20,30")]
    [InlineData("undefined-end", "[10,20,30]", "1, undefined", 3, "20,30")]
    [InlineData("omitted-end", "[10,20,30]", "1", 2, "20,30")]
    [InlineData("no-bounds", "[10,20,30]", "", 1, "10,20,30")]
    public void ArraySlice_InterpretsUnsignedHexTextBounds(
        string row, string receiver, string bounds, int argc, string expected)
    {
        string source = Source(receiver, bounds);
        var tokens = new Lexer(source).Tokenize();
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Error);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var tree = parser.Parse();
        Assert.True(parser.Diagnostics.Count == 0, "Parser: " + string.Join("; ", parser.Diagnostics));
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        Assert.True(binder.Diagnostics.All.Count == 0, "Binder: " + string.Join("; ", binder.Diagnostics.All));

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions);
        Assert.Equal("main", main.Name);
        bool sparse = receiver == "values";
        Assert.Equal(sparse ? 1 : 0, main.Parameters.Count);
        var call = Assert.Single(main.Blocks.SelectMany(b => b.Instructions), i => i.Opcode == Opcode.Call);
        Assert.Equal("Array::slice", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(argc, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(bytecode);
        var entry = Assert.Single(bytecode.Functions);
        Assert.Equal("main", entry.Name);
        Assert.Equal(main.Parameters.Count, entry.ParameterCount);

        var token = TsValue.FromObject(new TsObject("SliceToken"));
        var values = new TsArray();
        values.Add(TsValue.FromInt32(10));
        values.SetHole(1);
        values.Add(TsValue.Void);
        values.Add(token);
        values.Add(TsValue.FromInt32(40));
        var wrapper = new TsArrayValue(values);

        object[] ReadSlots(TsArray array) => Enumerable.Range(0, array.Count).Select<int, object>(index =>
        {
            var item = array.Get(index);
            if (array.IsHole(index))
            {
                Assert.Same(TsValue.Void, item);
                return "H";
            }
            if (ReferenceEquals(item, TsValue.Void)) return "U";
            if (ReferenceEquals(item, token)) return "O";
            return Assert.IsType<TsInt32Value>(item).Value;
        }).ToArray();

        object[]? before = sparse ? ReadSlots(values) : null;
        if (sparse) Assert.Equal(new object[] { 10, "H", "U", "O", 40 }, before!);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = Assert.IsType<TsArrayValue>(vm.Execute(bytecode, "main", sparse ? [wrapper] : []));
        if (sparse)
        {
            Assert.NotSame(wrapper, result);
            Assert.NotSame(values, result.Value);
            Assert.Equal(before!, ReadSlots(values));
        }

        var actual = ReadSlots(result.Value);
        object[] expectedSlots = expected.Length == 0 ? [] : expected.Split(',').Select<string, object>(
            slot => int.TryParse(slot, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number) ? number : slot).ToArray();
        output.WriteLine($"C314 {row} lexer=0 parser=0 binder=0 verified main/{entry.ParameterCount} Array::slice/{argc} expected=[{expected}] actual=[{string.Join(',', actual)}]");
        Assert.Equal(expectedSlots.Length, result.Value.Count);
        Assert.Equal(expectedSlots, actual);
    }

    internal static string Source(string receiver, string bounds) =>
        receiver == "values"
            ? $"function main(values: any[]): any[] {{ return values.slice({bounds}); }}"
            : $"function main(): any[] {{ const values: any[] = {receiver}; return values.slice({bounds}); }}";
}
