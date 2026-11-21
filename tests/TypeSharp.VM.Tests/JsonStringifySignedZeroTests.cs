using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class JsonStringifySignedZeroTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("f32", "negative-zero", "root", "0")]
    [InlineData("f32", "negative-zero", "array", "[1,0,2]")]
    [InlineData("f32", "negative-zero", "object", "{\"reading\":0}")]
    [InlineData("f32", "positive-zero", "root", "0")]
    [InlineData("f32", "negative-finite", "root", "-3.5")]
    [InlineData("f64", "negative-zero", "root", "0")]
    [InlineData("f64", "negative-zero", "array", "[1,0,2]")]
    [InlineData("f64", "negative-zero", "object", "{\"reading\":0}")]
    [InlineData("f64", "positive-zero", "root", "0")]
    [InlineData("f64", "negative-finite", "root", "-3.5")]
    public void JsonStringify_SerializesSignedZeroAsZero(string width, string input, string shape, string expected)
    {
        var expression = shape switch
        {
            "root" => "n",
            "array" => "[1, n, 2]",
            "object" => "{ reading: n }",
            _ => throw new InvalidOperationException(shape)
        };
        var source = $"function main(n: any): string {{ return JSON.stringify({expression}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("JSON::stringify", target);
        Assert.Equal(1, call.Operand1);
        Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Return);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode is
            Opcode.ReturnVoid or Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);

        var bytecode = BytecodeCompiler.Compile(ir);
        var (value, expectedBits, expectedType) = (width, input) switch
        {
            ("f32", "negative-zero") => (TsValue.FromFloat32(BitConverter.Int32BitsToSingle(int.MinValue)), 0x80000000UL, TsValueType.Float32),
            ("f32", "positive-zero") => (TsValue.FromFloat32(0f), 0UL, TsValueType.Float32),
            ("f32", "negative-finite") => (TsValue.FromFloat32(-3.5f), 0xc0600000UL, TsValueType.Float32),
            ("f64", "negative-zero") => (TsValue.FromFloat64(BitConverter.Int64BitsToDouble(long.MinValue)), 0x8000000000000000UL, TsValueType.Float64),
            ("f64", "positive-zero") => (TsValue.FromFloat64(0d), 0UL, TsValueType.Float64),
            ("f64", "negative-finite") => (TsValue.FromFloat64(-3.5d), 0xc00c000000000000UL, TsValueType.Float64),
            _ => throw new InvalidOperationException($"{width}/{input}")
        };
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var before = AssertInputBits(value, expectedType, expectedBits);
        var result = vm.Execute(bytecode, "main", [value]);
        var after = AssertInputBits(value, expectedType, expectedBits);
        var serialized = Assert.IsType<TsStringValue>(result);
        output.WriteLine($"JSON.stringify width={width} input={input} shape={shape}; type={expectedType}; before=0x{before:x} after=0x{after:x}; parser/binder=0/0; {call.Opcode} target={target} argc={call.Operand1}; Return; result={serialized.GetType().Name}; expected={expected}; actual={serialized.Value}");
        Assert.True(string.Equals(expected, serialized.Value, StringComparison.Ordinal),
            $"JSON.stringify text mismatch: expected={expected}; actual={serialized.Value}");
    }

    private static ulong AssertInputBits(TsValue value, TsValueType expectedType, ulong expectedBits)
    {
        Assert.Equal(expectedType, value.ValueType);
        var bits = expectedType == TsValueType.Float32
            ? unchecked((uint)BitConverter.SingleToInt32Bits(Assert.IsType<TsFloat32Value>(value).Value))
            : unchecked((ulong)BitConverter.DoubleToInt64Bits(Assert.IsType<TsFloat64Value>(value).Value));
        Assert.Equal(expectedBits, bits);
        return bits;
    }
}
