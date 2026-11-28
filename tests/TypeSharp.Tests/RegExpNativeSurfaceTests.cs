using TypeSharp.Hosting;
using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Binder = TypeSharp.Semantics.Binder.Binder;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.Tests;

public sealed class RegExpNativeSurfaceTests
{
    [Fact]
    public void CursorUsesUtf16OffsetsAndRemainsPerNominalValue()
    {
        var values = Array(Run("""
            function make():any { return /a/g; }
            function main():any {
                let a:any=make(); let b:any=make();
                let initial=a.lastIndex;
                let first=a.test('😀a');
                let end=a['lastIndex'];
                let miss=a.test('😀a');
                let reset=a.lastIndex;
                let sticky:any=/a/y;
                sticky.lastIndex=1;
                let anchored=sticky.test('ba');
                let zero:any=/b*/g;
                zero.lastIndex=1;
                return [initial,first,end,miss,reset,b.lastIndex,anchored,sticky.lastIndex,zero.test('ac'),zero.lastIndex];
            }
            """));
        Assert.Equal(0d, Number(values.Get(0)));
        Assert.True(Bool(values.Get(1)));
        Assert.Equal(3d, Number(values.Get(2)));
        Assert.False(Bool(values.Get(3)));
        Assert.Equal(0d, Number(values.Get(4)));
        Assert.Equal(0d, Number(values.Get(5)));
        Assert.True(Bool(values.Get(6)));
        Assert.Equal(2d, Number(values.Get(7)));
        Assert.True(Bool(values.Get(8)));
        Assert.Equal(1d, Number(values.Get(9)));
    }

    [Fact]
    public void ComputedOptionalCallKeepsReceiverAndDetachedValueChecksBrand()
    {
        var values = Array(Run("""
            let hits:string='';
            function key():string { hits=hits+'k'; return 'test'; }
            function arg():string { hits=hits+'a'; return 'a'; }
            function main():any {
                let r:any=/a/g;
                let ok=r?.[key()](arg());
                let f=r.test;
                let err:any='none';
                try { f(r,'a'); } catch(e) { let caught:any=e; err=caught.name; }
                return [ok,hits,r.lastIndex,err,/^undefined$/.test()];
            }
            """));
        Assert.True(Bool(values.Get(0)));
        Assert.Equal("ka", Text(values.Get(1)));
        Assert.Equal(1d, Number(values.Get(2)));
        Assert.Equal("TypeError", Text(values.Get(3)));
        Assert.True(Bool(values.Get(4)));
    }

    [Fact]
    public void CachedManagedCallDoesNotConsumeNativeRegExp()
    {
        var values = Array(Run("""
            class Probe { test(x:any):boolean { return false; } }
            function same(r:any):any { return r.test('a'); }
            function main():any {
                let m:any=new Probe(); let a:any=/a/; let o:any={test:(x:any)=>false};
                return [same(m),same(a),same(o),same(a),same(m)];
            }
            """));
        Assert.Equal(new[]{false,true,false,true,false},
            Enumerable.Range(0,values.Count).Select(i=>Bool(values.Get(i))).ToArray());
    }

    [Fact]
    public void LiteralSpacesTemplateAndDivisionKeepTheirSeparateRoutes()
    {
        var values = Array(Run("""
            function main():any {
                return [String(/ a /i), /^[a b]$/.test(' '), `v:${/^a  b$/i.test('A  B')}`, 12 / 3];
            }
            """));
        Assert.Equal("/ a /i",Text(values.Get(0)));
        Assert.True(Bool(values.Get(1)));
        Assert.Equal("v:true",Text(values.Get(2)));
        Assert.Equal(4d,Number(values.Get(3)));
    }

    [Fact]
    public async Task PublicHostingReturnsCanonicalReferenceAndLeavesClrRegexStateless()
    {
        var folder = Path.Combine(Path.GetTempPath(), "typesharp-regex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder,"app.ts");
            await File.WriteAllTextAsync(source, """
                interface RegExp { lastIndex:number; test(input:any):boolean; }
                export function typed():any {
                    let r:RegExp=/a/g; let f=r.test; return [r,f];
                }
                export function reference(r:any):any { return r.test; }
                export function inject(r:any):any { r.lastIndex=1; return [r.test('ba'),r.lastIndex]; }
                """);
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceFile(source).BuildAsync();
            var typed = Array(runtime.Invoke("typed"));
            var rx = Assert.IsType<TsRegexValue>(typed.Get(0));
            var reference = Assert.IsType<TsFunctionValue>(typed.Get(1));
            Assert.Same(reference,runtime.Invoke("reference",[rx]));
            var raw = rx.Regex;
            var result = Array(runtime.Invoke("inject",[rx]));
            Assert.True(Bool(result.Get(0)));
            Assert.Equal(2d,Number(result.Get(1)));
            Assert.Same(raw,rx.RawValue);
            Assert.Equal("/a/g",rx.ToString());
            Assert.True(rx.Test("a"));
            Assert.True(rx.Test("a"));
        }
        finally { Directory.Delete(folder,true); }
    }

    private static TsValue? Run(string source)
    {
        var toks=new Lexer(source).Tokenize();
        Assert.DoesNotContain(toks,t=>t.Kind==TokenKind.Error);
        var parser=new Parser(toks);
        var binder=new Binder();
        var tree=binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var module=BytecodeCompiler.Compile(new IRGenerator().Generate(tree));
        BytecodeVerifier.Verify(module);
        return new Vm().Execute(module,"main");
    }

    private static TsArray Array(TsValue? value) => Assert.IsType<TsArrayValue>(value).Value;
    private static bool Bool(TsValue value) => Assert.IsType<TsBoolValue>(value).Value;
    private static string Text(TsValue value) => Assert.IsType<TsStringValue>(value).Value;
    private static double Number(TsValue value) => value switch
    {
        TsInt32Value v => v.Value,
        TsInt64Value v => v.Value,
        _ => Assert.IsType<TsFloat64Value>(value).Value,
    };
}
