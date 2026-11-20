using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class Uint8ArraySubarrayUndefinedEndTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("forwarded", "begin, end", "populated", 1, "undefined", "10,15,20")]
    [InlineData("literal", "begin, undefined", "populated", 2, "undefined", "15,20")]
    [InlineData("negative-start", "begin, end", "populated", -2, "undefined", "15,20")]
    [InlineData("omitted", "begin", "populated", 1, "undefined", "10,15,20")]
    [InlineData("explicit-zero", "begin, end", "populated", 1, "0", "")]
    [InlineData("explicit-positive", "begin, end", "populated", 1, "3", "10,15")]
    [InlineData("explicit-negative", "begin, end", "populated", 1, "-1", "10,15")]
    [InlineData("empty-source", "begin, end", "empty", 0, "undefined", "")]
    public void Uint8ArraySubarray_UsesLengthForUndefinedEnd(string row, string arguments, string input, int begin, string end, string expected)
    {
        var source = $"function main(bytes: Uint8Array, begin: number, end: number | undefined): Uint8Array {{ return bytes.subarray({arguments}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("Uint8Array::subarray", Assert.IsType<string>(call.OperandObject));
        var argc = arguments == "begin" ? 2 : 3;
        Assert.Equal(argc, call.Operand1);
        var bytecode = BytecodeCompiler.Compile(ir);

        var bytes = new TsUint8ArrayValue(input == "empty" ? [] : [5, 10, 15, 20]);
        var endValue = end == "undefined" ? TsValue.Void : TsValue.FromInt32(int.Parse(end, System.Globalization.CultureInfo.InvariantCulture));
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = Assert.IsType<TsUint8ArrayValue>(vm.Execute(bytecode, "main", [bytes, TsValue.FromInt32(begin), endValue]));
        var actual = string.Join(",", Enumerable.Range(0, result.Length).Select(result.Get));
        output.WriteLine($"Uint8Array.subarray row={row}; parser/binder=0/0; target=Uint8Array::subarray; argc={argc}; input={input}; begin={begin}; end={end}; expected=[{expected}]; actual=[{actual}]");
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"Uint8Array.subarray end mismatch: expected=[{expected}]; actual=[{actual}]");
    }
}
