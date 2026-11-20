using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public class StringPadStartUndefinedFillTests
{
    private readonly ITestOutputHelper _output;

    public StringPadStartUndefinedFillTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("omitted-fill", "w", "7", 4, 2, "   7")]
    [InlineData("undefined-literal", "w, undefined", "7", 4, 3, "   7")]
    [InlineData("undefined-forwarded", "w, fill", "42", 13, 3, "           42")]
    [InlineData("undefined-utf16-receiver", "w, fill", "😀", 3, 3, " 😀")]
    [InlineData("undefined-no-growth", "w, fill", "42", 2, 3, "42")]
    [InlineData("null-fill", "w, null", "7", 4, 3, "nul7")]
    [InlineData("undefined-string-fill", "w, \"undefined\"", "7", 4, 3, "und7")]
    [InlineData("empty-fill", "w, \"\"", "7", 4, 3, "7")]
    [InlineData("explicit-fill", "w, \"kt\"", "7", 4, 3, "ktk7")]
    public void StringPadStart_ExplicitUndefinedFill(string row, string arguments, string text, int width, int argc, string expected)
    {
        var source = $"function main(s: string, w: number, fill: string | undefined): string {{ return s.padStart({arguments}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal("String::padStart", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(argc, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var result = Assert.IsType<TsStringValue>(interpreter.Execute(bytecode, "main",
            [TsValue.FromString(text), TsValue.FromInt32(width), TsValue.Void]));
        var actual = result.Value;
        _output.WriteLine($"padStart row={row}; parser/binder=0/0; target=String::padStart; argc={call.Operand1}; input=[{Hex(text)}]; width={width}; expected=[{Hex(expected)}]; actual=[{Hex(actual)}]");
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"String.padStart result mismatch: expected=[{Hex(expected)}]; actual=[{Hex(actual)}]");
    }

    private static string Hex(string value) => string.Join(" ", value.Select(unit => ((int)unit).ToString("X4")));
}
