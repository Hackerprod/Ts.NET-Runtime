using System.Buffers.Binary;
using System.Reflection;
using TypeSharp.IR;
using Binder = TypeSharp.Semantics.Binder.Binder;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.VM.Tests;

public sealed class GeneratorCompletionTests
{
    private static BytecodeModule Compile(string source)
    {
        var parser = new Parser(new Lexer(source).Tokenize());
        var ast = parser.Parse();
        var binder = new Binder();
        var bound = binder.Bind(ast);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        return BytecodeCompiler.Compile(new IRGenerator().Generate(bound));
    }

    private static TsValue Run(string source, params TsValue[] args) => new Vm().Execute(Compile(source), "main", args)!;
    private static bool Bool(TsValue v) => Assert.IsType<TsBoolValue>(v).Value;

    [Fact]
    public void YieldingNestedCatchPreservesPendingReturn()
    {
        var result = Assert.IsType<TsObjectValue>(Run("""
            function* gen(box:any,token:any):any {
                try { yield 1; } finally {
                    try { yield 2; } catch(e) { if(e===token) { box.n=3; } }
                    box.n=box.n*10+4;
                }
            }
            function main():any {
                let t:any={n:1};let b:any={n:0};let g:any=gen(b,t);g.next();
                let a:any=g.return(7);let z:any=g.throw(t);
                return {first:a.value,firstDone:a.done,last:z.value,lastDone:z.done,trace:b.n};
            }
            """)).Value;
        Assert.Equal(2, Assert.IsType<TsInt32Value>(result.GetField("first")).Value);
        Assert.False(Bool(result.GetField("firstDone")));
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result.GetField("last")).Value);
        Assert.True(Bool(result.GetField("lastDone")));
        Assert.Equal(34L, Assert.IsType<TsInt64Value>(result.GetField("trace")).Value);
    }

    [Theory]
    [InlineData("break")]
    [InlineData("continue")]
    public void CatchOnlyAbruptLoopTransferLeavesTheOldHandler(string transfer)
    {
        var result = Assert.IsType<TsObjectValue>(Run($$"""
            function* step(b:any,one:any,two:any):any {
                outer:for(let i:number=0;i<1;i=i+1) {
                    try { yield 1; {{transfer}} outer; } catch(e) { b.n=9; }
                    b.n=b.n*10+3;
                }
                throw two;
            }
            function main():any {
                let b:any={n:0};let one:any={n:1};let two:any={n:2};
                try{let g:any=step(b,one,two);g.next();g.next();}
                catch(e){return {same:e===two,trace:b.n};}
                return false;
            }
            """)).Value;
        Assert.True(Bool(result.GetField("same")));
        Assert.Equal(0, Assert.IsType<TsInt32Value>(result.GetField("trace")).Value);
    }

    [Theory]
    [InlineData("next")]
    [InlineData("return")]
    [InlineData("throw")]
    public void BusyGuardIsCatchableAndKeepsTheActiveFrame(string method)
    {
        var result = Assert.IsType<TsObjectValue>(Run($$"""
            function* gen(box:any):any {try {box.g.{{method}}();} catch(e) {let err:any=e;yield err.name;} yield 2;}
            function main():any {
                let b:any={g:null};let g:any=gen(b);b.g=g;
                let a:any=g.next();let z:any=g.next();return {first:a.value,last:z.value};
            }
            """)).Value;
        Assert.Equal("TypeError", Assert.IsType<TsStringValue>(result.GetField("first")).Value);
        Assert.Equal(2, Assert.IsType<TsInt32Value>(result.GetField("last")).Value);
    }

    [Fact]
    public void NamedAndComputedBridgesAndDetachedTokenIdentity()
    {
        var result = Assert.IsType<TsObjectValue>(Run("""
            function* gen():any {yield 4;yield 5;}
            function main():any {
                let g:any=gen();let a:any=g.next;let b:any=g.next;
                let args:any=[9];let r:any=g.next?.(...args);let key:any='next';let z:any=g[key](9);
                let label:any='none';try {a(g);} catch(e) {let err:any=e;label=err.name;}
                return {same:a===b,distinct:a!==g.return,first:r.value,second:z.value,error:label};
            }
            """)).Value;
        Assert.True(Bool(result.GetField("same")));
        Assert.True(Bool(result.GetField("distinct")));
        Assert.Equal(4, Assert.IsType<TsInt32Value>(result.GetField("first")).Value);
        Assert.Equal(5, Assert.IsType<TsInt32Value>(result.GetField("second")).Value);
        Assert.Equal("TypeError", Assert.IsType<TsStringValue>(result.GetField("error")).Value);
    }

    [Fact]
    public void ThrownMemberPayloadStaysRaw()
    {
        var result = Run("""
            function* gen():any {try {yield 1;} catch(e) {yield e;}}
            function main():any {
                let g:any=gen();let token:any={n:7};let args:any=[token];g.next();
                let r:any=g.throw(...args);return r.value===token && !r.done;
            }
            """);
        Assert.True(Bool(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BufferedThrowClosesWithoutAdvancingAndKeepsPayload(bool suspended)
    {
        var items = new TsArray();
        items.Add(TsValue.FromInt32(1));
        items.Add(TsValue.Void);
        var g = new TsGeneratorValue(items);
        if (suspended) Assert.True(g.TryNextLegacy(out _, out _));
        var field = typeof(TsGeneratorValue).GetField("_legacyIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var before = field.GetValue(g);
        var token = new TsObjectValue(new TsObject("Object"));
        var result = Run("""
            function main(g:any,token:any):any {try {g.throw(token);} catch(e) {return e===token;} return false;}
            """, g, token);
        Assert.True(Bool(result));
        Assert.Equal(before, field.GetValue(g));
        Assert.True(g.Done);
        Assert.False(g.Running);
    }

    [Fact]
    public void InvalidMapEntryClosesOnceWithoutDrainingYieldingCleanup()
    {
        var result = Assert.IsType<TsObjectValue>(Run("""
            function* gen(box:any):any {try {yield 1;} finally {box.n=1;yield 2;box.n=12;}}
            function main():any {
                let b:any={n:0};let g:any=gen(b);
                try {new Map(g);} catch(e) {
                    let err:any=e;let before:any=b.n;let a:any=g.next();let z:any=g.next();
                    return {name:err.name,message:err.message,before:before,after:b.n,done:a.done,last:z.done};
                }
                return false;
            }
            """)).Value;
        Assert.Equal("TypeError", Assert.IsType<TsStringValue>(result.GetField("name")).Value);
        Assert.Equal("Map constructor entry must be an object", Assert.IsType<TsStringValue>(result.GetField("message")).Value);
        Assert.Equal(1, Assert.IsType<TsInt32Value>(result.GetField("before")).Value);
        Assert.Equal(12, Assert.IsType<TsInt32Value>(result.GetField("after")).Value);
        Assert.True(Bool(result.GetField("done")));
        Assert.True(Bool(result.GetField("last")));
    }

    [Fact]
    public void MixedEnvelopePreservesOrdinaryRecordAndReopensWithoutSources()
    {
        const string ordinary = "function ordinary():any {try {return 4;} finally {let n:any=2;}}";
        var original = Compile(ordinary);
        var mixed = Compile(ordinary + """
            function* gen():any {try{yield 1;}finally{yield 2;}}
            function main():any {let g:any=gen();g.next();return g.return(9).value;}
            """);
        var stockOrdinary = original.Functions.Single();
        var mixedOrdinary = mixed.Functions.Single(f => f.Name == "ordinary");
        using var left = new MemoryStream();
        using var right = new MemoryStream();
        BytecodeSerializer.Serialize(left, new BytecodeModule("records", [stockOrdinary]));
        BytecodeSerializer.Serialize(right, new BytecodeModule("records", [mixedOrdinary]));
        Assert.True(left.ToArray().SequenceEqual(right.ToArray()));
        using var originalStream = new MemoryStream();
        BytecodeSerializer.Serialize(originalStream, original);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(originalStream.ToArray().AsSpan(4, 4)));
        using var mixedStream = new MemoryStream();
        BytecodeSerializer.Serialize(mixedStream, mixed);
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(mixedStream.ToArray().AsSpan(4, 4)));
        mixedStream.Position = 0;
        var loaded = BytecodeSerializer.Deserialize(mixedStream);
        Assert.Equal(mixedStream.Length, mixedStream.Position);
        Assert.Equal(4, Assert.IsType<TsInt32Value>(new Vm().Execute(loaded, "ordinary")).Value);
        Assert.Equal(2, Assert.IsType<TsInt32Value>(new Vm().Execute(loaded, "main")).Value);
    }
}
