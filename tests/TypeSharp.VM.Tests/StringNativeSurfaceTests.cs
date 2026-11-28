using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Binder = TypeSharp.Semantics.Binder.Binder;
using Parser = TypeSharp.Syntax.Parser.Parser;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.VM.Tests;

public sealed class StringNativeSurfaceTests
{
    [Fact]
    public void QualifiedCallRequiresItsActualReceiver()
    {
        const string source = "function main(s: string): any { try { return s.repeat(1); } catch (e) { return e; } }";
        var code = Compile(source);
        Assert.Equal("ab", Assert.IsType<TsStringValue>(new Vm().Execute(code, "main", [TsValue.FromString("ab")])).Value);
        CheckTypeError(new Vm().Execute(code, "main", [TsValue.FromInt32(7)]));
        CheckTypeError(new Vm().Execute(code, "main", []));
    }

    [Fact]
    public void ComputedMethodCarriesReceiverButDetachedMethodDoesNot()
    {
        const string bound = """function main(): any { let s:any="abcd"; return s["slice"](1); }""";
        Assert.Equal("bcd", Assert.IsType<TsStringValue>(new Vm().Execute(Compile(bound), "main")).Value);
        const string detached = """
            function main():any {
                let s:string="abcd";
                let f:any=s.slice;
                try { return f("xyz",1); } catch(e) { return e; }
            }
            """;
        CheckTypeError(new Vm().Execute(Compile(detached), "main"));
    }

    [Fact]
    public void NumericAndTextualNegativeZeroAreDifferentPropertyKeys()
    {
        const string source = """function main(): any { let s:string="abc"; return [s[-0.0],s["-0"]]; }""";
        var actual = Assert.IsType<TsArrayValue>(new Vm().Execute(Compile(source), "main")).Value;
        Assert.Equal(2, actual.Count);
        Assert.Equal("a", Assert.IsType<TsStringValue>(actual.Get(0)).Value);
        Assert.IsType<TsVoid>(actual.Get(1));
    }

    private static void CheckTypeError(TsValue? actual)
    {
        var error = Assert.IsType<TsObjectValue>(actual).Value;
        Assert.Equal("TypeError", error.TypeName);
        Assert.Equal("TypeError", Assert.IsType<TsStringValue>(error.GetField("name")).Value);
    }

    private static BytecodeModule Compile(string source)
    {
        var tokens = new Lexer(source).Tokenize();
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
        var parser = new Parser(tokens);
        var binder = new Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var module = BytecodeCompiler.Compile(new IRGenerator().Generate(bound));
        BytecodeVerifier.Verify(module);
        return module;
    }
}
