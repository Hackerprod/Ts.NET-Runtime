using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.VM.Tests;

public sealed class JsonStringifyWireTextTests
{
    [Fact]
    public void QuotedValuesAndKeysPreserveActualUtf16CodeUnits()
    {
        var item = new TsObject("Object");
        item.SetField("é\u0022\ud800", TsValue.FromString("\ud800x\udc00\ud83d\ude00\udc00"));
        item.SetField("\ud83d\ude00", TsValue.FromString("\\u0041/<>&+'\u2028\u2029\ufeff\b\t\n\f\r\0\u001f"));
        string expected = "{\"é\\\"\\ud800\":\"\\ud800x\\udc00\ud83d\ude00\\udc00\","
            + "\"\ud83d\ude00\":\"\\\\u0041/<>&+'\u2028\u2029\ufeff\\b\\t\\n\\f\\r\\u0000\\u001f\"}";

        Assert.Equal(expected.Select(c => (ushort)c).ToArray(),
            Run("return JSON.stringify(value);", TsValue.FromObject(item)).Select(c => (ushort)c).ToArray());
    }

    [Theory]
    [InlineData("return JSON.stringify(value);")]
    [InlineData("const j = JSON; return j.stringify(value);")]
    [InlineData("const f = JSON.stringify; return f(value);")]
    public void FiniteFloatsUseTheSameCanonicalTextOnEveryNativeRoute(string body)
    {
        var values = new TsArray();
        foreach (var v in new[] {
            TsValue.FromFloat64(1e-7), TsValue.FromFloat64(1e-6), TsValue.FromFloat64(1e20),
            TsValue.FromFloat64(1e21), TsValue.FromFloat64(double.Epsilon),
            TsValue.FromFloat64(BitConverter.Int64BitsToDouble(0x3ff0000800000000L)),
            TsValue.FromFloat32(0.1f), TsValue.FromFloat64(-0.0),
            TsValue.FromInt64(long.MinValue), TsValue.FromUInt64(ulong.MaxValue), TsValue.FromDecimal(12.3400m)
        })
            values.Add(v);

        Assert.Equal("[1e-7,0.000001,100000000000000000000,1e+21,5e-324,1.0000076293945312,"
            + "0.10000000149011612,0,-9223372036854775808,18446744073709551615,12.3400]",
            Run(body, new TsArrayValue(values)));
    }

    private static string Run(string body, TsValue value)
    {
        string source = $"function main(value: any): string {{ {body} }}";
        var lexer = new TypeSharp.Syntax.Lexer(source);
        var tokens = lexer.Tokenize();
        Assert.True(tokens.All(t => t.Kind != TypeSharp.Syntax.TokenKind.Error));
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, f => f.Name == "main");
        var instructions = main.Blocks.SelectMany(b => b.Instructions).ToArray();
        if (body.StartsWith("const f", StringComparison.Ordinal))
        {
            Assert.Single(instructions, i => i.Opcode == Opcode.LoadFunc && Equals(i.OperandObject, "JSON::stringify"));
            var call = Assert.Single(instructions, i => i.Opcode == Opcode.CallDynamic);
            Assert.Equal(1, call.Operand0);
        }
        else
        {
            var call = Assert.Single(instructions, i => i.Opcode == Opcode.Call && Equals(i.OperandObject, "JSON::stringify"));
            Assert.Equal(1, call.Operand1);
        }

        var bytecode = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(bytecode);
        return Assert.IsType<TsStringValue>(new TypeSharp.VM.Interpreter.Interpreter().Execute(bytecode, "main", [value])).Value;
    }
}
