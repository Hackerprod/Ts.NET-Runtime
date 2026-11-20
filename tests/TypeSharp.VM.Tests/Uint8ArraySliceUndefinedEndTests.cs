using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class Uint8ArraySliceUndefinedEndTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("forwarded", "begin, end", 1, "undefined", "20,30,40")]
    [InlineData("literal", "begin, undefined", 2, "undefined", "30,40")]
    [InlineData("negative-start", "begin, end", -2, "undefined", "30,40")]
    [InlineData("omitted", "begin", 1, "undefined", "20,30,40")]
    [InlineData("explicit-zero", "begin, end", 1, "0", "")]
    [InlineData("explicit-negative", "begin, end", 1, "-1", "20,30")]
    [InlineData("explicit-positive", "begin, end", 1, "2", "20")]
    [InlineData("start-at-length", "begin, end", 4, "undefined", "")]
    [InlineData("start-past-length", "begin, end", 9, "undefined", "")]
    public void Uint8ArraySlice_UsesLengthForUndefinedEnd(string row, string arguments, int begin, string end, string expected)
    {
        var source = $"function main(bytes: Uint8Array, begin: number, end: number | undefined): Uint8Array {{ return bytes.slice({arguments}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var bytes = new TsUint8ArrayValue([10, 20, 30, 40]);
        var endValue = end == "undefined" ? TsValue.Void : TsValue.FromInt32(int.Parse(end, System.Globalization.CultureInfo.InvariantCulture));
        var sliced = Assert.IsType<TsUint8ArrayValue>(vm.Execute(bytecode, "main", [bytes, TsValue.FromInt32(begin), endValue]));
        Assert.NotSame(bytes, sliced);
        var actual = string.Join(",", Enumerable.Range(0, sliced.Length).Select(sliced.Get));
        if (sliced.Length > 0)
            sliced.Set(0, TsValue.FromInt32(99));
        var original = string.Join(",", Enumerable.Range(0, bytes.Length).Select(bytes.Get));
        Assert.Equal("10,20,30,40", original);
        output.WriteLine($"Uint8Array.slice row={row}; parser/binder=0/0; begin={begin}; end={end}; original=[{original}]; expected=[{expected}]; actual=[{actual}]");
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"Uint8Array.slice result mismatch: expected=[{expected}]; actual=[{actual}]");
    }
}
