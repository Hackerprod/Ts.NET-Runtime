using System.Reflection;
using TypeSharp.Interop.Marshalling;
using TypeSharp.IR;
using Binder = TypeSharp.Semantics.Binder.Binder;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Vm = TypeSharp.VM.Interpreter.Interpreter;

namespace TypeSharp.VM.Tests;

public sealed class Uint8ArrayClassContractsTests
{
    [Fact]
    public void NestedViewsOverlapInBothDirectionsAndHaveIndependentCopies()
    {
        var (value, ir) = Execute("""
            function main() {
                const root = new Uint8Array([11,12,13,14,15,16]);
                const a = root.subarray(1,5);
                const b = root.subarray(2,5);
                b.set(a.subarray(0,3));
                a.subarray(0,3).set(b);
                const reversed = b.reverse();
                const copy = new Uint8Array(b);
                const slice = b.slice(0);
                const other = b.toReversed();
                copy[0] = 90;
                return [root,a,b,reversed,copy,slice,other];
            }
            """);
        Call(ir, "subarray", 3, 4);
        Call(ir, "set", 2, 2);
        Call(ir, "reverse", 1);
        Call(ir, "slice", 2);
        Call(ir, "toReversed", 1);
        var results = Items(value);
        Assert.Equal("11,12,14,14,13,16", results.Get(0).ToString());
        Assert.Equal("12,14,14,13", results.Get(1).ToString());
        Assert.Equal("14,14,13", results.Get(2).ToString());
        Assert.Same(results.Get(2), results.Get(3));
        Assert.Equal("90,14,13", results.Get(4).ToString());
        Assert.Equal("14,14,13", results.Get(5).ToString());
        Assert.Equal("13,14,14", results.Get(6).ToString());
        for (int i = 4; i <= 6; i++) Assert.NotSame(results.Get(2), results.Get(i));
    }

    [Fact]
    public void PositionsDistinguishNullUndefinedAndNativePrimitiveConversions()
    {
        var (value, ir) = Execute("""
            function main() {
                const b = new Uint8Array([10,20,30,40]);
                const cycle: any = [0];
                cycle[0] = cycle;
                return [b.slice("0x2"),b.slice([2n]),b.slice(new Date(2) as any),
                        b.slice(1,null),b.slice(1,undefined),b.slice(cycle)];
            }
            """);
        Call(ir, "slice", 2, 4);
        Call(ir, "slice", 3, 2);
        var results = Items(value);
        foreach (var i in new[] { 0, 1, 2 })
            Assert.Equal("30,40", results.Get(i).ToString());
        Assert.Equal("", results.Get(3).ToString());
        Assert.Equal("20,30,40", results.Get(4).ToString());
        Assert.Equal("10,20,30,40", results.Get(5).ToString());
    }

    [Fact]
    public void SetPreflightsRangeButCommitsBytesBeforeALateTypeError()
    {
        var (value, ir) = Execute("""
            function main() {
                const b = new Uint8Array([10,20,30,40]);
                let late="ok", early="ok", length="ok";
                try { b.set([91 as any,2n],1); } catch(e) { late=(e as any).name; }
                try { b.set([88 as any,2n],3); } catch(e) { early=(e as any).name; }
                try { b.set({length:2n},Number("Infinity")); } catch(e) { length=(e as any).name; }
                const result=b.set({length:"0x2", "0":"0b101"},2);
                return [late as any,early,length,result,b];
            }
            """);
        Call(ir, "set", 3, 4);
        var results = Items(value);
        Assert.Equal("TypeError", Assert.IsType<TsStringValue>(results.Get(0)).Value);
        Assert.Equal("RangeError", Assert.IsType<TsStringValue>(results.Get(1)).Value);
        Assert.Equal("TypeError", Assert.IsType<TsStringValue>(results.Get(2)).Value);
        Assert.Same(TsValue.Void, results.Get(3));
        Assert.Equal("10,91,5,0", results.Get(4).ToString());
    }

    [Fact]
    public void ConstructorChecksLengthsAndUsesOneByteConversionForRepresentedSources()
    {
        var (value, ir) = Execute("""
            function main() {
                const s = new Set([257,8,257]);
                const m = new Map<string,number>();
                m.set("a",93);
                let range = "ok";
                try { new Uint8Array(Number("9007199254740992")); } catch(e) { range=(e as any).name; }
                return [new Uint8Array(s) as any,new Uint8Array(m),new Uint8Array("0b10"),
                        new Uint8Array([Number("4294967297"),Number("-4294967297"),"0x20" as any,[2n] as any]),range];
            }
            """);
        Assert.Equal(5, ir.Count(i => i.Opcode == Opcode.NewObject && i.OperandObject is "Uint8Array"));
        var results = Items(value);
        Assert.Equal("1,8", results.Get(0).ToString());
        Assert.Equal("0", results.Get(1).ToString());
        Assert.Equal("0,0", results.Get(2).ToString());
        Assert.Equal("1,255,32,2", results.Get(3).ToString());
        Assert.Equal("RangeError", Assert.IsType<TsStringValue>(results.Get(4)).Value);
    }

    [Fact]
    public void IndexedKeysClassifyCanonicalWritesAndDerivedReadOnlyFields()
    {
        var (value, ir) = Execute("""
            function main() {
                const root=new Uint8Array([10,20,30,40]);
                const view=root.subarray(1,3);
                let invalid="ok", ordinary="ok", shadow="ok";
                try { view["-0" as any]=4n as any; } catch(e) { invalid=(e as any).name; }
                try { view["02" as any]=4n as any; } catch(e) { ordinary=(e as any).name; }
                try { view[[1 as any,{toString:null}] as any]; } catch(e) { shadow=(e as any).name; }
                view[1n as any]="0x70" as any;
                return [invalid as any,ordinary,shadow,view[[["length"]] as any],view[["byteLength"] as any],
                        view[Number("4294967296")],view[1],root];
            }
            """);
        Assert.Equal(3, ir.Count(i => i.Opcode == Opcode.StoreElement));
        Assert.Equal(5, ir.Count(i => i.Opcode == Opcode.LoadElement));
        var results = Items(value);
        Assert.Equal("TypeError", Assert.IsType<TsStringValue>(results.Get(0)).Value);
        Assert.Equal("ok", Assert.IsType<TsStringValue>(results.Get(1)).Value);
        Assert.Equal("TypeError", Assert.IsType<TsStringValue>(results.Get(2)).Value);
        Assert.Equal(2d, Assert.IsType<TsFloat64Value>(results.Get(3)).Value);
        Assert.Equal(2d, Assert.IsType<TsFloat64Value>(results.Get(4)).Value);
        Assert.Same(TsValue.Void, results.Get(5));
        Assert.Equal(112d, Assert.IsType<TsFloat64Value>(results.Get(6)).Value);
        Assert.Equal("10,20,112,40", results.Get(7).ToString());
    }

    [Fact]
    public void WrapperIdentityAndManagedRootViewContractsSurviveMutation()
    {
        byte[] backing = [10, 20, 30, 40];
        var root = new TsUint8ArrayValue(backing);
        var sameBacking = Assert.IsType<TsUint8ArrayValue>(TsValue.FromUint8Array(backing));
        var (value, _) = Execute("""
            function main(a: Uint8Array, b: Uint8Array) {
                const partial = a.subarray(1,3);
                const other = a.subarray(1,3);
                return [(a === b) as any,a == b,a === a,partial === other,
                        partial,a.subarray(),a.subarray(1,1),new Uint8Array() === new Uint8Array()];
            }
            """, root, sameBacking);
        var items = Items(value);
        Assert.False(Assert.IsType<TsBoolValue>(items.Get(0)).Value);
        Assert.False(Assert.IsType<TsBoolValue>(items.Get(1)).Value);
        Assert.True(Assert.IsType<TsBoolValue>(items.Get(2)).Value);
        Assert.False(Assert.IsType<TsBoolValue>(items.Get(3)).Value);
        Assert.False(Assert.IsType<TsBoolValue>(items.Get(7)).Value);
        Assert.Same(backing, root.Value);
        Assert.Same(backing, root.RawValue);
        Assert.Equal(0, new TsUint8ArrayValue(null!).Length);
        Assert.Equal((byte)0, root.Get(-1));
        root.Set(backing.Length, TsValue.FromBigInt(2)); // Invalid host indices do not coerce.
        Assert.Equal("10,20,30,40", root.Slice(-10, 99).ToString());
        var partial = Assert.IsType<TsUint8ArrayValue>(items.Get(4));
        var full = Assert.IsType<TsUint8ArrayValue>(items.Get(5));
        var empty = Assert.IsType<TsUint8ArrayValue>(items.Get(6));
        Assert.Equal(new byte[] { 20, 30 }, partial.Value);
        Assert.Equal(backing, full.Value);
        Assert.Empty(empty.Value);
        foreach (var view in new[] { partial, full, empty })
        {
            Assert.NotSame(backing, view.Value);
            Assert.NotSame(view.Value, view.RawValue);
            Assert.NotSame(view.Value, view.Value);
            Assert.NotSame(view.RawValue, view.RawValue);
        }
        var clone = Assert.IsType<TsUint8ArrayValue>(Marshaller.FromManaged(backing));
        Assert.NotSame(backing, clone.Value);
        Assert.Same(backing, Marshaller.ToManaged(root, typeof(object)));
        var visible = Assert.IsType<byte[]>(Marshaller.ToManaged(partial, typeof(byte[])));
        Assert.Equal(new byte[] { 20, 30 }, visible);
        Assert.NotSame(visible, Marshaller.ToManaged(partial, typeof(byte[])));
        var objectWindow = Assert.IsType<byte[]>(Marshaller.ToManaged(partial, typeof(object)));
        Assert.Equal(visible, objectWindow);
        var snapshot = partial.Value;
        var comparerType = typeof(TsValue).Assembly.GetType("TypeSharp.VM.Memory.TsValueMapKeyComparer", throwOnError: true)!;
        var comparer = (IEqualityComparer<TsValue>)comparerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        int hash = comparer.GetHashCode(partial);
        visible[0] = objectWindow[0] = snapshot[0] = 99;
        Assert.Equal((byte)20, partial.Get(0));
        backing[1] = 81;
        Assert.Equal((byte)81, partial.Get(0));
        partial.Set(1, TsValue.FromString("0x5A"));
        Assert.Equal(new byte[] { 10, 81, 90, 40 }, backing);
        Assert.Equal("81,90", partial.ToString());
        Assert.Equal(hash, comparer.GetHashCode(partial));
    }

    private static (TsValue? Value, Instruction[] Ir) Execute(string source, params TsValue[] args)
    {
        var tokens = new Lexer(source).Tokenize();
        var parser = new Parser(tokens);
        var binder = new Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Error);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, f => f.Name == "main");
        Assert.Equal(args.Length, main.Parameters.Count);
        var code = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(code);
        return (new Vm().Execute(code, "main", args), main.Blocks.SelectMany(b => b.Instructions).ToArray());
    }

    private static TsArray Items(TsValue? value) => Assert.IsType<TsArrayValue>(value).Value;

    private static void Call(Instruction[] ir, string method, int arity, int count = 1) =>
        Assert.Equal(count, ir.Count(i => i.Opcode == Opcode.Call && i.OperandObject is string target
            && target == "Uint8Array::" + method && i.Operand1 == arity));
}
