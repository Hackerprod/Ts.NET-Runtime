using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Binder = TypeSharp.Semantics.Binder.Binder;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.VM.Tests;

public sealed class BooleanCallableTests
{
    [Theory]
    [InlineData("function main(): any { return Boolean(new Uint8Array(0)); }", true)]
    [InlineData("function main(): any { let n = 1; return Boolean(() => n); }", true)]
    [InlineData("class C {} function main(): any { return Boolean(C); }", true)]
    [InlineData("function* gen(): any { yield 0; } function main(): any { return Boolean(gen()); }", true)]
    [InlineData("async function task(): Promise<number> { return 0; } function main(): any { return Boolean(task()); }", true)]
    [InlineData("function main(): any { return Boolean?.(/a/); }", true)]
    [InlineData("function main(): any { let f: any = Boolean; let a: any[] = [/a/, 0]; return f?.(...a); }", true)]
    [InlineData("function main(): any { let f: any = Boolean; let a: any[] = [0, /a/]; return f?.(...a); }", false)]
    [InlineData("function main(): any { return Boolean(); }", false)]
    [InlineData("function main(): any { return Boolean(0, /a/); }", false)]
    public void ReturnsPrimitiveTruthinessAfterVerifiedBytecodeRoundTrip(string source, bool expected)
    {
        var tokens = new Lexer(source).Tokenize();
        var parser = new Parser(tokens);
        var binder = new Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var original = BytecodeCompiler.Compile(new IRGenerator().Generate(bound));
        BytecodeVerifier.Verify(original);
        using var wire = new MemoryStream();
        BytecodeSerializer.Serialize(wire, original);
        wire.Position = 0;
        var restored = BytecodeSerializer.Deserialize(wire);
        Assert.Equal(wire.Length, wire.Position);
        BytecodeVerifier.Verify(restored);
        Assert.Equal(expected, Assert.IsType<TsBoolValue>(new Vm().Execute(restored, "main")).Value);
    }
}
