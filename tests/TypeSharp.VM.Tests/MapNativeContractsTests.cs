using System.Numerics;
using TypeSharp.IR;
using Binder = TypeSharp.Semantics.Binder.Binder;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Interpreter;
using TypeSharp.VM.Memory;
using Vm = TypeSharp.VM.Interpreter.Interpreter;
using Xunit;

namespace TypeSharp.VM.Tests;

public sealed class MapNativeContractsTests
{
    [Fact]
    public void ConstructorBindsMixedPairsAndCopiesWithoutSharingStorage()
    {
        const string typed = """function main(): any { let x = new Map<string,number>([["a",1],["b",2],["a",3]]); return [x.size,x.get("a"),Array.from(x.keys())]; }""";
        const string copied = """function main(): any { let y=new Map<string, number>();y.set("a",1);let x=new Map<string, number>(y);return x.get("a"); }""";
        var mixed = Array(Run(typed));
        Number(2, mixed.Get(0));
        Number(3, mixed.Get(1));
        var keys = Array(mixed.Get(2));
        Text("a", keys.Get(0));
        Text("b", keys.Get(1));
        Number(1, Run(copied)!);
        const string independent = """function main(): any { let y = new Map<any,any>(); y.set("a",1); y.set("b",2); let x = new Map<any,any>(y as any); x.set("a",9); return [y.get("a"),x.get("a"),Array.from(x.keys())]; }""";
        var copy = Array(Run(independent));
        Number(1, copy.Get(0));
        Number(9, copy.Get(1));
        var copyKeys = Array(copy.Get(2));
        Text("a", copyKeys.Get(0));
        Text("b", copyKeys.Get(1));
    }

    [Fact]
    public void HostNumberKeysKeepExactWidthsAndSeparateTrueBigInt()
    {
        var map = new TsMap();
        var signed = TsValue.FromInt64(9007199254740992L);
        var unsigned = TsValue.FromUInt64(9007199254740992UL);
        var exactDouble = TsValue.FromFloat64(9007199254740992d);
        var neighbor = TsValue.FromInt64(9007199254740993L);
        var bigint = TsValue.FromBigInt(new BigInteger(9007199254740992L));
        map.Set(signed, TsValue.FromString("first"));
        map.Set(unsigned, TsValue.FromString("updated"));
        map.Set(neighbor, TsValue.FromString("neighbor"));
        map.Set(bigint, TsValue.FromString("bigint"));
        Assert.Equal(3, map.Count);
        Assert.Same(signed, map.GetEntryAt(0).Key);
        Text("updated", map.Get(exactDouble));
        Text("neighbor", map.Get(neighbor));
        Text("bigint", map.Get(bigint));
        var set = new TsSet();
        set.Add(signed);
        Assert.False(set.Contains(exactDouble)); // Set keeps its existing domain.
        Assert.True(set.Contains(bigint));
    }

    [Fact]
    public void InvalidLegacyEntryClosesBeforeAnotherYieldInInterpreterAndBuiltin()
    {
        const string source = """function main(g:any):any { let name:any; try { new Map<any,any>(g); } catch(e) {name=(e as any).name;} let n=g.next(); return [name,n.done,n.value]; }""";
        var items = new TsArray();
        items.Add(TsValue.FromInt32(1));
        var later = new TsArray();
        later.Add(TsValue.FromString("later"));
        later.Add(TsValue.FromString("v"));
        items.Add(new TsArrayValue(later));
        var generator = Assert.IsType<TsGeneratorValue>(TsValue.FromGenerator(items));
        var value = Array(Run(source, generator));
        Text("TypeError", value.Get(0));
        Assert.True(Assert.IsType<TsBoolValue>(value.Get(1)).Value);
        Assert.IsType<TsVoid>(value.Get(2));
        Assert.True(generator.Done);
        Assert.True(Builtins.TryGet("Generator::next", out var next));
        var terminal = Assert.IsType<TsObjectValue>(next([generator]));
        Assert.True(Assert.IsType<TsBoolValue>(terminal.Value.GetField("done")).Value);
        Assert.IsType<TsVoid>(terminal.Value.GetField("value"));
    }

    [Fact]
    public void TypedSizeKeyIsStorageButDynamicSizeIsReadonly()
    {
        const string source = """function main():any {let m=new Map<string,any>();m.set("size","entry");let d:any=m;let k="size";let out:any[]=[];out.push(m.size,d[k],m[k]);out.push(delete d.size,m.size,m.get(k));out.push(delete d[k],m.size,m.get(k));try {d[k]=22;}catch(e){out.push((e as any).name);}out.push(delete m[k],m.size);return out;}""";
        var a = Array(Run(source));
        Assert.Equal(12, a.Count);
        Number(1, a.Get(0));
        Number(1, a.Get(1));
        Text("entry", a.Get(2));
        Assert.True(Assert.IsType<TsBoolValue>(a.Get(3)).Value);
        Number(1, a.Get(4));
        Text("entry", a.Get(5));
        Assert.True(Assert.IsType<TsBoolValue>(a.Get(6)).Value);
        Number(1, a.Get(7));
        Text("entry", a.Get(8));
        Text("TypeError", a.Get(9));
        Assert.True(Assert.IsType<TsBoolValue>(a.Get(10)).Value);
        Number(0, a.Get(11));
    }

    [Fact]
    public void ForEachPassesOnlyThreeArgumentsAndUsesTheIndependentReceiver()
    {
        const string ordinary = """function main():any {let m:any=new Map<any,any>();m.set("k",5);let seen:any[]=[];let get:any; m.forEach(function(v:any,k:any,o:any,r:any=()=>{return (this as any).tag;}){seen.push(v);seen.push(k);seen.push(o===m);get=r;},{tag:"map"});seen.push(get());return seen;}""";
        const string native = """function main():any {let source=new Map<any,any>();let dest=new Map<any,any>();source.set("k",5);source.forEach(dest.set,dest);return [dest.get(5),dest.size];}""";
        var callback = Array(Run(ordinary));
        Number(5, callback.Get(0));
        Text("k", callback.Get(1));
        Assert.True(Assert.IsType<TsBoolValue>(callback.Get(2)).Value);
        Text("map", callback.Get(3));
        const string arity = """function main():any {let m:any=new Map<any,any>();m.set("k",5);let seen:any[]=[];m.forEach(function(v:any,k:any,o:any,fourth:any){seen.push(fourth);});m.forEach(function(...xs:any[]){seen.push(xs.length);seen.push(xs[0]);seen.push(xs[1]);seen.push(xs[2]===m);});return seen;}""";
        var args = Array(Run(arity));
        Assert.IsType<TsVoid>(args.Get(0));
        Number(3, args.Get(1));
        Number(5, args.Get(2));
        Text("k", args.Get(3));
        Assert.True(Assert.IsType<TsBoolValue>(args.Get(4)).Value);
        var moved = Array(Run(native));
        Text("k", moved.Get(0));
        Number(1, moved.Get(1));
    }

    private static TsValue? Run(string source, params TsValue[] args)
    {
        var tokens = new Lexer(source).Tokenize();
        Assert.True(tokens.All(token => token.Kind != TokenKind.Error));
        var parser = new Parser(tokens);
        var tree = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(binder.Diagnostics.All);
        var bytecode = BytecodeCompiler.Compile(new IRGenerator().Generate(bound));
        BytecodeVerifier.Verify(bytecode);
        return new Vm().Execute(bytecode, "main", args);
    }

    private static TsArray Array(TsValue? value) => Assert.IsType<TsArrayValue>(value).Value;
    private static void Text(string expected, TsValue actual) =>
        Assert.Equal(expected, Assert.IsType<TsStringValue>(actual).Value);
    private static void Number(double expected, TsValue actual) =>
        Assert.Equal(expected, actual is TsInt32Value integer ? integer.Value : Assert.IsType<TsFloat64Value>(actual).Value);
}
