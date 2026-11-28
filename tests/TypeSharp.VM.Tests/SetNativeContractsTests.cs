using System.Buffers.Binary;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Binder = TypeSharp.Semantics.Binder.Binder;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.VM.Tests;

public sealed class SetNativeContractsTests
{
    private const string ReferenceHelper = "Set::<invoke-member-reference>";

    private const string Computed = """
        function main(): any {const events:string[]=[];const s=new Set<number>([5]);function receiver(): Set<number> {events.push('receiver');return s;}function key(): string {events.push('key');return 'has';}function arg(): number {events.push('arg');return 5;}try{return [receiver()[key()](arg()),events];}catch(e){return [(e as any).name,events];}}
        """;
    private const string OptionalReceiver = """
        function arg(log:number[]):number{log.push(5);return 5;}function check(x:Set<number>|null|undefined,log:number[]):any{return x?.has(arg(log));}function main(): any {const events:number[]=[];return [check(null,events),check(undefined,events),events];}
        """;
    private const string OptionalMethod = """
        function main(): any {const events:number[]=[];const x:Set<number> = new Set<number>([5]);function arg():number{events.push(5);return 5;}return [x.has?.(arg()),events];}
        """;
    private const string Spread = """
        function main(): any {const x:Set<number> = new Set<number>();const a:any[]=[];x.add(...a);return [x.size,x.has(...a),x.delete(...a),x.size];}
        """;
    private const string MemberValue = """
        function main(): any {let seen=0;const s=new Set<number>();function recv():Set<number>{seen=seen+1;return s;}const f:any=recv().has;return [f===s.has,seen];}
        """;
    private const string Detached = """
        function main(): any { let x=new Set<any>(); x.add(9);let h:any=x.has;try{return h(x,9);}catch(e){return (e as any).name;} }
        """;
    private const string SizeWrite = """
        function main(): any {const a:any=new Set<number>([7]);const events:string[]=[];function recv():any {events.push('receiver');return a;}function key():string{events.push('key');return 'size';}function rhs():number{events.push('rhs');return 42;}try{recv()[key()]=rhs();}catch(e){events.push((e as any).name);}return [events,a.size];}
        """;
    private const string Callback = """
        function main(): any {let outer:any=new Set<any>([5]);let inner:any=new Set<any>([undefined]);let trace:any[]=[];let escaped:any;outer.forEach(function(v:any,k:any,source:any,...tail:any[]):void{trace.push([(this as any).id,v,k,source===outer,tail.length]);escaped=()=> (this as any).id;inner.forEach((x:any):void=>{trace.push((this as any).id);},{id:'ignored'});inner.forEach(function(x:any=(this as any).fallback,...rest:any[]):void{trace.push([(this as any).id,x,rest.length,rest[0],rest[1]===inner]);},{id:'inner',fallback:6});},{id:'outer'});trace.push(escaped() as any);return trace;}
        """;
    private const string InvalidCallback = """
        function main(): any {const empty:any=new Set<number>();const full:any=new Set<number>([7]);const r:any[]=[];const bad:any[]=['notCallable',{},3,null,undefined];for(let i=0;i<bad.length;i++){const cb:any=bad[i];try{empty.forEach(cb);}catch(e){r.push((e as any).name);}try{full.forEach(cb);}catch(e){r.push((e as any).name);}}full.forEach((v:any)=>{r.push(v);});return r;}
        """;
    private const string BytesView = """
        function main(): any {const a=new Uint8Array([7,4,3,4]);const view=a.subarray(1,4);a[1]=2;const s=new Set<any>(view);view[0]=5;return [s.values(),view[0]];}
        """;
    private const string Generator = """
        function main(): any {function *g(){yield 7;yield 3;return 9;}const i=g();const a=i.next();const s=new Set<any>(i);const b=i.next();return [a.value,s.values(),b.value,b.done];}
        """;
    private const string Wide = """
        function main(): any { let x=new Set<any>(); x.add(2147483648);x.add(2147483648n);return [x.size,x.delete(2147483648n),x.has(2147483648),x.delete(2147483648),x.size]; }
        """;

    [Fact]
    public void ComputedAndOptionalMethodRetainTheActualReceiverAndEvaluateArgumentsOnce()
    {
        Check(Computed, """[true,["receiver","key","arg"]]""", "main", Opcodes.Call, ReferenceHelper, 3);
        Check(OptionalMethod, "[true,[5]]", "main", Opcodes.Call, ReferenceHelper, 3);
    }

    [Fact]
    public void TypedReceiverOptionalSkipsArgumentWorkAndSpreadUsesOmittedValue()
    {
        Check(OptionalReceiver, """["<undefined>","<undefined>",[]]""", "check", Opcodes.Call, "Set::has", 2);
        Check(Spread, "[1,true,true,0]", "main", Opcodes.Call, ReferenceHelper, 3);
    }

    [Fact]
    public void MethodExtractionEvaluatesTheReceiverButNeverUsesAPositionalReceiver()
    {
        Check(MemberValue, "[true,1]", "main", Opcodes.LoadField, "has");
        Check(Detached, "\"TypeError\"", "main", Opcodes.LoadField, "has");
    }

    [Fact]
    public void SizeWriteThrowsAfterReceiverKeyAndValueEvaluation()
    {
        Check(SizeWrite, """[["receiver","key","rhs","TypeError"],1]""", "main", Opcodes.StoreElement);
    }

    [Fact]
    public void ForEachUsesIndependentReceiverWithDefaultRestAndLexicalArrows()
    {
        Check(Callback, """
            [["outer",5,5,true,0],"outer",["inner",6,2,"<undefined>",true],"outer"]
            """, "main", Opcodes.CallVirt, "forEach", 3);
        var invalid = Compile(InvalidCallback);
        var result = Assert.IsType<TsArrayValue>(new Vm().Execute(invalid, "main")).Value;
        Assert.Equal(11, result.Count);
        for (int index = 0; index < 10; index++)
            Assert.Equal("TypeError", Assert.IsType<TsStringValue>(result.Get(index)).Value);
        Assert.Equal(7, Assert.IsType<TsInt32Value>(result.Get(10)).Value);
    }

    [Fact]
    public void ConstructorReadsCurrentByteViewAndOnlyRemainingGeneratorYields()
    {
        Check(BytesView, "[[2,3,4],5]", "main", Opcodes.NewObject, "Set", 1);
        Check(Generator, """[7,[3],"<undefined>",true]""", "main", Opcodes.NewObject, "Set", 1);
    }

    [Fact]
    public void ExactMembershipSeparatesBothNumericDomainsWithoutChangingHostSetProfile()
    {
        Check(Wide, "[2,true,true,true,0]", "main", Opcodes.NewObject, "Set", 0);
        var module = Compile("function make():any{return new Set<any>();}");
        var native = Assert.IsType<TsSetValue>(new Vm().Execute(module, "make"));
        var host = new TsSet();
        var legacyAllocation = new TsHeap().AllocateSet();
        var exact = new TsInt64Value(9_007_199_254_740_992);
        var matchingDouble = new TsFloat64Value(exact.Value);
        var trueBig = TsValue.FromBigInt(exact.Value);
        foreach (var set in new[] { native.Value, host, legacyAllocation }) Assert.True(set.Add(exact));
        Assert.True(native.Value.Contains(matchingDouble));
        Assert.False(native.Value.Contains(trueBig));
        Assert.True(native.Value.Add(trueBig));
        Assert.Same(exact, native.Value.GetAt(0));
        Assert.Same(trueBig, native.Value.GetAt(1));
        foreach (var set in new[] { host, legacyAllocation })
        {
            Assert.False(set.Contains(matchingDouble));
            Assert.True(set.Contains(trueBig));
        }
    }

    private static BytecodeModule Compile(string source)
    {
        var tokens = new Lexer(source).Tokenize();
        Assert.Equal(0, tokens.Count(token => token.Kind == TokenKind.Error));
        var parser = new Parser(tokens);
        var ast = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(ast);
        Assert.Empty(binder.Diagnostics.All);
        var module = BytecodeCompiler.Compile(new IRGenerator().Generate(bound));
        BytecodeVerifier.Verify(module);
        return module;
    }

    private static void Check(string source, string json, string functionName, byte opcode, string? name = null, int? arity = null)
    {
        var module = Compile(source);
        var function = module.Functions.Single(f => f.Name == functionName);
        var code = function.Instructions;
        var matched = false;
        for (int offset = 0; offset < code.Length; offset += OpcodeFormats.InstructionSize(code[offset]))
        {
            if (code[offset] != opcode) continue;
            if (name is not null && function.StringConstants[BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(offset + 1))] != name)
                continue;
            if (arity is int count && BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(offset + 5)) != count)
                continue;
            matched = true;
        }
        Assert.True(matched, $"Missing decoded route in {functionName}: {opcode:X2}/{name}/{arity}");
        Assert.Equal(json, JsonSerializer.Serialize(Project(new Vm().Execute(module, "main")), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private static object? Project(TsValue? value) => value switch
    {
        TsInt32Value i => i.Value,
        TsFloat64Value f => f.Value,
        TsStringValue s => s.Value,
        TsBoolValue b => b.Value,
        TsArrayValue a => Enumerable.Range(0, a.Value.Count).Select(i => Project(a.Value.Get(i))).ToArray(),
        TsVoid => "<undefined>",
        TsNull => null,
        _ => throw new InvalidOperationException($"Unexpected result: {value?.GetType().Name}"),
    };
}
