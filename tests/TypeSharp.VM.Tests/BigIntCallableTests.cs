using System.Globalization;
using System.Numerics;
using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Binder = TypeSharp.Semantics.Binder.Binder;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.VM.Tests;

public sealed class BigIntCallableTests
{
    [Fact]
    public void ConvertsExactNumericCarriersWithoutLosingPrecisionOrIdentity()
    {
        var program = Compile("return BigInt(input);");
        var identity = TsValue.FromBigInt(BigInteger.One << 90);
        (TsValue Input, BigInteger Expected)[] rows =
        [
            (TsValue.FromInt32(-23), -23),
            (TsValue.FromInt64(long.MinValue), long.MinValue),
            (TsValue.FromUInt64(ulong.MaxValue), ulong.MaxValue),
            (TsValue.FromFloat32(-32768f), -32768),
            (TsValue.FromFloat64(9007199254740993d), 9007199254740992L),
            (TsValue.FromFloat64(-0d), BigInteger.Zero),
            (TsValue.FromDecimal(9007199254740993m), 9007199254740993L),
            (TsValue.FromDecimal(-9007199254740993m), -9007199254740993L),
            (TsValue.FromDecimal(decimal.MaxValue), new BigInteger(decimal.MaxValue))
        ];
        foreach (var (input, expected) in rows)
            Assert.Equal(expected, Assert.IsType<TsBigIntValue>(Run(program, input)).Value);
        Assert.Same(identity, Run(program, identity));
    }

    [Fact]
    public void ParsesWholeEcmaTextAndCatchesScriptErrorsByName()
    {
        var guarded = Compile("try { return BigInt(input); } catch (e: any) { return e.name; }");
        (string Text, string Value)[] valid =
        [
            ("\t\uFEFF\u2028-00017\u00A0", "-17"), (" \u2029", "0"),
            ("0xFF", "255"), ("0O17", "15"), ("0b1011", "11"), ("18446744073709551617", "18446744073709551617")
        ];
        foreach (var (text, expected) in valid)
            Assert.Equal(BigInteger.Parse(expected, CultureInfo.InvariantCulture),
                Assert.IsType<TsBigIntValue>(Run(guarded, TsValue.FromString(text))).Value);
        foreach (var text in new[] { "12\0", "1\0" + "2", "-0x1", "0o", "0b2", "1e2", "1_2", "\u008512", "١٢" })
            Assert.Equal("SyntaxError", Assert.IsType<TsStringValue>(Run(guarded, TsValue.FromString(text))).Value);
        foreach (var value in new[] { TsValue.FromFloat32(float.NaN), TsValue.FromFloat64(double.NegativeInfinity),
                     TsValue.FromFloat64(-2.5d), TsValue.FromDecimal(1e-28m) })
            Assert.Equal("RangeError", Assert.IsType<TsStringValue>(Run(guarded, value)).Value);
        Assert.Equal("TypeError", Assert.IsType<TsStringValue>(Run(guarded, TsValue.Null)).Value);
    }

    [Fact]
    public void OptionalSpreadDispatchesToNativeAndPublicVmEscapeHasExactWrapper()
    {
        const string caught = "const f: any = BigInt; try { return f?.(...input); } catch(e: any) { return e.name; }";
        var program = Compile(caught);
        var array = new TsArray();
        Assert.Equal("TypeError", Assert.IsType<TsStringValue>(Run(program, new TsArrayValue(array))).Value);
        array.Add(TsValue.FromString("0xFF"));
        Assert.Equal(new BigInteger(255), Assert.IsType<TsBigIntValue>(Run(program, new TsArrayValue(array))).Value);

        var fault = Assert.Throws<InvalidOperationException>(() => Run(Compile("return BigInt(input);"), TsValue.FromString("0b2")));
        Assert.Equal("Cannot convert string to BigInt", fault.Message);
        Assert.Null(fault.InnerException);
    }

    private static BytecodeModule Compile(string body)
    {
        var tokens = new Lexer($"function main(input: any): any {{ {body} }}").Tokenize();
        var parser = new Parser(tokens);
        var binder = new Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var module = BytecodeCompiler.Compile(new IRGenerator().Generate(bound));
        BytecodeVerifier.Verify(module);
        return module;
    }

    private static TsValue? Run(BytecodeModule module, TsValue input) => new Vm().Execute(module, "main", [input]);
}
