using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public class ArrayJoinUndefinedSeparatorTests
{
    private readonly ITestOutputHelper _output;
    public ArrayJoinUndefinedSeparatorTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("dense-undefined", "dense", "undefined", "a,b")]
    [InlineData("sparse-undefined", "sparse", "undefined", "a,,,b")]
    [InlineData("dense-omitted", "dense", "omitted", "a,b")]
    [InlineData("sparse-omitted", "sparse", "omitted", "a,,,b")]
    [InlineData("dense-null", "dense", "null", "anullb")]
    [InlineData("dense-empty-string", "dense", "empty-string", "ab")]
    [InlineData("dense-literal-undefined", "dense", "literal-undefined", "aundefinedb")]
    [InlineData("sparse-custom", "sparse", "custom", "a|||b")]
    [InlineData("empty-undefined", "empty", "undefined", "")]
    public void ArrayJoin_ExplicitUndefinedSeparator(string row, string shape, string separator, string expected)
    {
        var array = new TsArray();
        if (shape is "dense" or "sparse")
        {
            array.Add(TsValue.FromString("a"));
            if (shape == "sparse")
            {
                array.SetHole(1);
                array.Add(TsValue.Null);
                Assert.True(array.IsHole(1));
                Assert.Same(TsValue.Null, array.Get(2));
            }
            array.Add(TsValue.FromString("b"));
        }
        var receiver = new TsArrayValue(array);
        var args = separator == "omitted" ? [receiver] : new TsValue[]
        {
            receiver,
            separator switch
            {
                "undefined" => TsValue.Void,
                "null" => TsValue.Null,
                "empty-string" => TsValue.FromString(""),
                "literal-undefined" => TsValue.FromString("undefined"),
                "custom" => TsValue.FromString("|"),
                _ => throw new ArgumentException(nameof(separator))
            }
        };
        var source = separator == "omitted"
            ? "function main(a: any[]): string { return a.join(); }"
            : "function main(a: any[], sep: any): string { return a.join(sep); }";

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
        Assert.Equal("Array::join", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(args.Length, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsStringValue>(interpreter.Execute(bytecode, "main", args)).Value;
        _output.WriteLine($"Array.join row={row}; shape={shape}; separator={separator}; parser/binder=0/0; target=Array::join; argc={call.Operand1}; result=String; expected=[{expected}]; actual=[{actual}]");
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"Array.join separator mismatch: expected=[{expected}]; actual=[{actual}]");
    }
}
