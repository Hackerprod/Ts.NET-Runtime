using System.Buffers.Binary;
using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringPadNegativeDecimalTextWidthTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("padStart")]
    [InlineData("padEnd")]
    public void NegativeIntegerTextWithoutFill_ReturnsReceiver(string method) =>
        Check(method, "integer", "xy", TsValue.FromString("-1"), null, "xy");

    [Theory]
    [InlineData("padStart")]
    [InlineData("padEnd")]
    public void NegativeFractionalTextWithPattern_ReturnsReceiver(string method) =>
        Check(method, "fraction", "xy", TsValue.FromString(" -2.75 "), TsValue.FromString("ab"), "xy");

    [Theory]
    [InlineData("padStart")]
    [InlineData("padEnd")]
    public void WidthAndFillControls(string method)
    {
        bool start = method == "padStart";
        Check(method, "native-negative", "xy", TsValue.FromInt32(-1), TsValue.FromString("ab"), "xy");
        Check(method, "native-negative-infinity", "xy", TsValue.FromFloat64(double.NegativeInfinity), TsValue.FromString("ab"), "xy");
        Check(method, "no-width", "xy", null, null, "xy");
        Check(method, "undefined-width", "xy", TsValue.Void, null, "xy");
        Check(method, "nonnumeric-text", "xy", TsValue.FromString("oops"), TsValue.FromString("ab"), "xy");
        Check(method, "native-nan", "xy", TsValue.FromFloat64(double.NaN), TsValue.FromString("ab"), "xy");
        Check(method, "sub-unit-negative-text", "xy", TsValue.FromString("-0.75"), null, "xy");
        Check(method, "signed-zero-text", "xy", TsValue.FromString("-0"), TsValue.FromString("ab"), "xy");
        Check(method, "positive-text-pattern", "xy", TsValue.FromString("5.9"), TsValue.FromString("ab"), start ? "abaxy" : "xyaba");
        Check(method, "positive-text-omitted-fill", "xy", TsValue.FromString("5.9"), null, start ? "   xy" : "xy   ");
        Check(method, "positive-text-undefined-fill", "😀", TsValue.FromString("3.9"), TsValue.Void, start ? " 😀" : "😀 ");
        Check(method, "positive-text-empty-fill", "xy", TsValue.FromString("5.9"), TsValue.FromString(""), "xy");
    }

    private void Check(string method, string row, string text, TsValue? width, TsValue? fill, string expected)
    {
        string arguments = width is null ? "" : fill is null ? "width" : "width, fill";
        int argc = width is null ? 1 : fill is null ? 2 : 3;
        string target = "String::" + method;
        var source = $"function main(s: string, width: any, fill: any): string {{ return s.{method}({arguments}); }}";
        var tokens = new Lexer(source).Tokenize();
        Assert.Equal(0, tokens.Count(token => token.Kind == TokenKind.Error));
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var tree = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, fn => fn.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), op => op.Opcode == Opcode.Call);
        Assert.Equal(target, Assert.IsType<string>(call.OperandObject));
        Assert.Equal(argc, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var compiled = Assert.Single(bytecode.Functions, fn => fn.Name == "main");
        var instructions = compiled.Instructions;
        int directCalls = 0;
        for (int offset = 0; offset < instructions.Length;)
        {
            byte op = instructions[offset];
            Assert.True(OpcodeFormats.TryGet(op, out var format));
            int size = 1 + format.OperandBytes;
            Assert.True(size <= instructions.Length - offset);
            if (op == Opcodes.Call)
            {
                Assert.Equal(9, size);
                int index = BinaryPrimitives.ReadInt32LittleEndian(instructions.AsSpan(offset + 1, 4));
                Assert.Equal(target, compiled.StringConstants[index]);
                Assert.Equal(argc, BinaryPrimitives.ReadInt32LittleEndian(instructions.AsSpan(offset + 5, 4)));
                directCalls++;
            }
            offset += size;
        }
        Assert.Equal(1, directCalls);

        TsValue[] values = [TsValue.FromString(text), width ?? TsValue.Void, fill ?? TsValue.Void];
        output.WriteLine($"ROUTE {method}/{row} lexer/parser/binder=0/0/0 target={target} argc={argc} " +
            $"receiver={Units(text)} width={Describe(width)} fill={Describe(fill)} expected={Units(expected)}");
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = Assert.IsType<TsStringValue>(interpreter.Execute(bytecode, "main", values)).Value;
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"Expected UTF-16 [{Units(expected)}], got [{Units(actual)}]");
        output.WriteLine($"RETURN {method}/{row} TsStringValue={Units(actual)}");
    }

    private static string Units(string value) => string.Join(",", value.Select(unit => ((int)unit).ToString("X4")));
    private static string Describe(TsValue? value) => value switch
    {
        null => "omitted",
        TsStringValue text => "TsStringValue:" + Units(text.Value),
        TsInt32Value number => "TsInt32Value:" + number.Value,
        TsFloat64Value number => "TsFloat64Value:" + number.Value,
        _ => value.GetType().Name
    };
}
