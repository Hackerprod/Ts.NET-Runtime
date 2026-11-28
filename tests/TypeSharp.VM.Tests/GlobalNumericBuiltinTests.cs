using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Binder = TypeSharp.Semantics.Binder.Binder;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.VM.Tests;

public sealed class GlobalNumericBuiltinTests
{
    [Fact]
    public void MissingArgumentsKeepNativeCallRoutes()
    {
        CheckNumber(Evaluate("return parseInt();", "parseInt", 0), double.NaN);
        CheckNumber(Evaluate("return parseFloat();", "parseFloat", 0), double.NaN);
        Assert.True(Assert.IsType<TsBoolValue>(Evaluate("return isNaN();", "isNaN", 0)).Value);
        Assert.False(Assert.IsType<TsBoolValue>(Evaluate("return isFinite();", "isFinite", 0)).Value);
    }

    [Fact]
    public void DefaultRadixAppliesOnlyToApprovedCarriers()
    {
        CheckNumber(Evaluate("return parseInt('0x12', undefined, 42);", "parseInt", 3), 18d);
        CheckNumber(Evaluate("return parseInt('0x12', null);", "parseInt", 2), 18d);
        CheckNumber(Evaluate("return parseInt('0x12', false);", "parseInt", 2), 18d);
        CheckNumber(Evaluate("return parseInt('42', true);", "parseInt", 2), double.NaN);
        CheckNumber(Evaluate("return parseInt('0x12', input);", "parseInt", 2, [TsValue.FromDecimal(0.00m)]), 18d);
        CheckNumber(Evaluate("return parseInt('0x12', input);", "parseInt", 2, [TsValue.FromInt64(0L)]), double.NaN);
        CheckNumber(Evaluate("return parseInt('123', '2');", "parseInt", 2), 123d);
        CheckNumber(Evaluate("return parseInt('123', 2.5);", "parseInt", 2), 123d);
    }

    [Fact]
    public void ParseFloatDistinguishesTextAndNumericNegativeZeroAndWidensSingle()
    {
        CheckNumber(Evaluate("return parseFloat('-0px');", "parseFloat", 1), BitConverter.Int64BitsToDouble(long.MinValue));
        var input = BitConverter.Int32BitsToSingle(int.MinValue);
        CheckNumber(Evaluate("return parseFloat(input);", "parseFloat", 1, [TsValue.FromFloat32(input)]), 0d);
        var tenth = BitConverter.Int32BitsToSingle(0x3dcccccd);
        CheckNumber(Evaluate("return parseFloat(input);", "parseFloat", 1, [TsValue.FromFloat32(tenth)]), (double)tenth);
    }

    [Fact]
    public void PredicatesUseWholePrimitiveTextAndPreserveNumberSiblings()
    {
        Assert.False(Assert.IsType<TsBoolValue>(Evaluate("return isNaN('\uFEFF0b11');", "isNaN", 1)).Value);
        Assert.True(Assert.IsType<TsBoolValue>(Evaluate("return isNaN('infinity');", "isNaN", 1)).Value);
        Assert.True(Assert.IsType<TsBoolValue>(Evaluate("return isFinite('0o12');", "isFinite", 1)).Value);
        Assert.False(Assert.IsType<TsBoolValue>(Evaluate("return isFinite('+0x12');", "isFinite", 1)).Value);
        Assert.False(Assert.IsType<TsBoolValue>(Evaluate("return Number.isFinite('0o12');", "Number::isFinite", 1)).Value);
    }

    [Fact]
    public void BigIntPredicatesThrowCatchableScriptTypeErrors()
    {
        Assert.Equal("TypeError", Assert.IsType<TsStringValue>(Evaluate(
            "try { return isNaN(7n); } catch(e: any) { return e.name; }", "isNaN", 1)).Value);
        Assert.Equal("TypeError", Assert.IsType<TsStringValue>(Evaluate(
            "try { return isFinite(7n); } catch(e: any) { return e.name; }", "isFinite", 1)).Value);
        var escaped = Assert.Throws<InvalidOperationException>(() => Evaluate("return isFinite(7n);", "isFinite", 1));
        Assert.Equal("Cannot convert a BigInt value to a number", escaped.Message);
        Assert.Null(escaped.InnerException);
    }

    private static TsValue? Evaluate(string body, string target, int arity, TsValue[]? args = null)
    {
        string parameter = args is null ? "" : "input: any";
        var tokens = new Lexer($"function main({parameter}): any {{ {body} }}").Tokenize();
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
        var parser = new Parser(tokens);
        var binder = new Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var entry = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(entry.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Opcode == Opcode.Call);
        Assert.Equal(target, Assert.IsType<string>(call.OperandObject));
        Assert.Equal(arity, call.Operand1);
        var module = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(module);
        return new Vm().Execute(module, "main", args);
    }

    private static void CheckNumber(TsValue? value, double expected)
    {
        var n = Assert.IsType<TsFloat64Value>(value).Value;
        Assert.True(double.IsNaN(expected) ? double.IsNaN(n) :
            BitConverter.DoubleToInt64Bits(n) == BitConverter.DoubleToInt64Bits(expected));
    }
}
