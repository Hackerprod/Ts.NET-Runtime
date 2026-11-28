using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.VM.Bytecode;
using Vm = TypeSharp.VM.Interpreter.Interpreter;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.VM.Tests;

public sealed class MathNamespacePrimitiveTests
{
    [Theory]
    [InlineData("abs-negative-zero", "export function main(): any {return Math.abs(-0);}", "0000000000000000")]
    [InlineData("round-neg-half", "export function main(): any {return Math.round(-0.5);}", "8000000000000000")]
    [InlineData("sign-text-zero", "export function main(): any {return Math.sign(\" -0 \");}", "8000000000000000")]
    [InlineData("fround-tie", "export function main(): any {return Math.fround(1.000000059604644775390625);}", "3ff0000000000000")]
    [InlineData("log-extra", "export function main(): any {return Math.log(\"\", 1n);}", "fff0000000000000")]
    [InlineData("pow-zero-slot", "export function main(): any {return Math.pow();}", "NaN")]
    [InlineData("pow-one-slot", "export function main(): any {return Math.pow(2);}", "NaN")]
    [InlineData("pow-negative-zero", "export function main(): any {return Math.pow(-0, 3, 1n);}", "8000000000000000")]
    [InlineData("atan2-quadrant", "export function main(): any {return Math.atan2(-0, -0, 1n);}", "c00921fb54442d18")]
    [InlineData("atan2-bigint-first-omitted-second", "export function main(): any {try {return Math.atan2(1n);}catch(e){return (e as any).name;}}", "string:\"TypeError\"")]
    [InlineData("atan2-bigint-second", "export function main(): any {try {return Math.atan2(undefined, 1n);}catch(e){return (e as any).name;}}", "string:\"TypeError\"")]
    [InlineData("imul-one-slot", "export function main(): any {return Math.imul(2);}", "0000000000000000")]
    [InlineData("imul-wraparound", "export function main(): any {return Math.imul(4294967295, 5, 1n);}", "c014000000000000")]
    [InlineData("clz32-wraparound", "export function main(): any {return Math.clz32(\"0x100000001\", 1n);}", "403f000000000000")]
    [InlineData("min-signed-zero", "export function main(): any {return Math.min(0, \"-0\");}", "8000000000000000")]
    [InlineData("max-signed-zero", "export function main(): any {return Math.max(\"-0\", 0);}", "0000000000000000")]
    [InlineData("max-nan", "export function main(): any {return Math.max(Infinity, \"x\");}", "NaN")]
    [InlineData("min-bigint-nan-first", "export function main(): any {try {return Math.min(NaN, 1n);}catch(e){return (e as any).name;}}", "string:\"TypeError\"")]
    [InlineData("max-bigint-inf-first", "export function main(): any {try {return Math.max(Infinity, 1n);}catch(e){return (e as any).name;}}", "string:\"TypeError\"")]
    [InlineData("hypot-bigint-inf-first", "export function main(): any {try {return Math.hypot(Infinity, 1n);}catch(e){return (e as any).name;}}", "string:\"TypeError\"")]
    [InlineData("hypot-scaled-large", "export function main(): any {return Math.hypot(\"3e200\", 0);}", "698f5aa543c31387")]
    [InlineData("hypot-scaled-subnormal", "export function main(): any {return Math.hypot(\"1e-323\", 0);}", "0000000000000002")]
    [InlineData("hypot-inf-after-nan", "export function main(): any {return Math.hypot(\"x\", Infinity);}", "7ff0000000000000")]
    [InlineData("alias-radix", "export function main(): any {const fn = Math.abs;return fn(\"0x4\");}", "4010000000000000")]
    [InlineData("alias-bigint", "export function main(): any {const fn = Math.sign;try {return fn(1n);}catch(e){return (e as any).name;}}", "string:\"TypeError\"")]
    [InlineData("ignored-extra-evaluated", "let hits:number=0; function extra(): any { hits=hits+1; return 1n; } export function main(): any {return Math.abs(-7, extra()) + hits;}", "4020000000000000")]
    [InlineData("random-ignored-bigint", "export function main():any{const sample = Math.random(1n); return typeof sample === \"number\" && sample >= 0 && sample < 1;}", "boolean:true")]
    public void RegisteredMathUsesActualPrimitiveArguments(string scenario, string source, string expected)
    {
        var tokens = new Lexer(source).Tokenize();
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
        var parser = new Parser(tokens);
        var ast = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(ast);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var module = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(module);

        var result = new Vm().Execute(module, "main");
        if (expected == "string:\"TypeError\"")
            Assert.Equal("TypeError", Assert.IsType<TsStringValue>(result).Value);
        else if (expected == "boolean:true")
            Assert.True(Assert.IsType<TsBoolValue>(result).Value, scenario);
        else
        {
            double value = Assert.IsType<TsFloat64Value>(result).Value;
            if (expected == "NaN") Assert.True(double.IsNaN(value), scenario);
            else Assert.Equal(expected, $"{BitConverter.DoubleToInt64Bits(value):x16}");
        }
    }
}
