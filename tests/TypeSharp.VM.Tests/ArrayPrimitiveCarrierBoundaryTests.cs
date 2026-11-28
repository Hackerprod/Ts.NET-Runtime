using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.VM.Tests;

public sealed class ArrayPrimitiveCarrierBoundaryTests
{
    private const string DepthError = "Array.flat depth must be a finite number or undefined";

    [Theory]
    [InlineData("clr-infinity")]
    [InlineData("decimal-overflow-positive")]
    [InlineData("decimal-overflow-negative")]
    [InlineData("f32-infinity")]
    [InlineData("native-decimal")]
    [InlineData("native-long")]
    [InlineData("rich-depth")]
    public void Flat_RetainsLegacyCarrierBoundary(string id)
    {
        TsValue arg = id switch
        {
            "clr-infinity" => TsValue.FromString("infinity"),
            "decimal-overflow-positive" => TsValue.FromString("1e309"),
            "decimal-overflow-negative" => TsValue.FromString("-1e309"),
            "f32-infinity" => TsValue.FromFloat32(float.PositiveInfinity),
            "native-decimal" => TsValue.FromDecimal(0.9999999999999999999999999999m),
            "native-long" => TsValue.FromInt64(1),
            "rich-depth" => new TsObjectValue(new TsObject("Object")),
            _ => throw new ArgumentOutOfRangeException(nameof(id))
        };
        var leaf = TsValue.FromInt32(7);
        var inner = ArrayOf(leaf);
        var original = ArrayOf(inner);
        original.Value.SetHole(1);
        var invoke = Prepare("flat");
        if (id is "native-decimal" or "native-long")
        {
            var result = Assert.IsType<TsArrayValue>(invoke(original, arg));
            Assert.NotSame(original, result);
            Assert.NotSame(original.Value, result.Value);
            Assert.Equal(1, result.Value.Count);
            Assert.False(result.Value.IsHole(0));
            Assert.Same(id == "native-decimal" ? (TsValue)inner : leaf, result.Value.Get(0));
        }
        else
        {
            var ex = Assert.Throws<InvalidOperationException>(() => { _ = invoke(original, arg); });
            Assert.Equal(DepthError, ex.Message);
            Assert.Contains("TypeSharp.VM.Interpreter.Builtins.", ex.StackTrace!);
        }
        Assert.Equal(2, original.Value.Count);
        Assert.Same(inner, original.Value.Get(0));
        Assert.True(original.Value.IsHole(1));
        Assert.Same(leaf, inner.Value.Get(0));
    }

    [Theory]
    [InlineData("f32")]
    [InlineData("decimal")]
    [InlineData("long")]
    [InlineData("ulong")]
    [InlineData("rich-field")]
    public void Join_RejectsNativeOnlyDescendantForEntireField(string id)
    {
        TsValue leaf = id switch
        {
            "f32" => TsValue.FromFloat32(0.1f),
            "decimal" => TsValue.FromDecimal(3.7500m),
            "long" => TsValue.FromInt64(7),
            "ulong" => TsValue.FromUInt64(7),
            "rich-field" => new TsObjectValue(new TsObject("Object")),
            _ => throw new ArgumentOutOfRangeException(nameof(id))
        };
        var inner = ArrayOf(TsValue.FromInt32(1), leaf);
        var end = TsValue.FromString("end");
        var original = ArrayOf(inner, end);
        var invoke = Prepare("join");
        var result = Assert.IsType<TsStringValue>(invoke(original, TsValue.FromString("|")));
        Assert.Equal("TypeSharp.VM.Memory.TsArray|end", result.Value);
        Assert.Equal(2, original.Value.Count);
        Assert.Equal(2, inner.Value.Count);
        Assert.False(inner.Value.IsHole(1));
        Assert.Same(inner, original.Value.Get(0));
        Assert.Same(end, original.Value.Get(1));
        Assert.Same(leaf, inner.Value.Get(1));
    }

    [Theory]
    [InlineData("splice-octal-delete-zero-start", "7UN/1H")]
    [InlineData("splice-binary-start-zero-delete", "1H7UN/[]")]
    public void Splice_SeparatesOrdinaryRadixStartAndDelete(string id, string expected)
    {
        var leaf = new TsInt32Value(1);
        var insert = new TsInt32Value(7);
        var receiver = ArrayOf(leaf);
        receiver.Value.SetHole(1);
        receiver.Value.Add(TsValue.Void);
        receiver.Value.Add(TsValue.Null);
        var backing = receiver.Value;
        Assert.Equal("1HUN", Slots(backing));

        TsValue start;
        TsValue delete;
        switch (id)
        {
            case "splice-octal-delete-zero-start":
                start = TsValue.FromInt32(0);
                delete = TsValue.FromString("0o2");
                break;
            case "splice-binary-start-zero-delete":
                start = TsValue.FromString("0b10");
                delete = TsValue.FromInt32(0);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(id));
        }
        var run = PrepareNative("splice", "a: any[], start: any, count: any, value: any", "start, count, value", 4);
        var removed = Assert.IsType<TsArrayValue>(run([receiver, start, delete, insert]));
        Assert.NotSame(receiver, removed);
        Assert.NotSame(backing, removed.Value);
        Assert.Same(backing, receiver.Value);

        var own = Own(backing).Concat(Own(removed.Value)).ToArray();
        Assert.Equal(4, own.Length);
        Assert.Same(leaf, Assert.Single(own, item => item is TsInt32Value n && n.Value == 1));
        Assert.Same(insert, Assert.Single(own, item => item is TsInt32Value n && n.Value == 7));
        Assert.Same(TsValue.Void, Assert.Single(own, item => item is TsVoid));
        Assert.Same(TsValue.Null, Assert.Single(own, item => item is TsNull));
        var actual = Slots(backing) + "/" + Slots(removed.Value);
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), "C334 native splice observed: " + actual);

        static IEnumerable<TsValue> Own(TsArray a) => Enumerable.Range(0, a.Count)
            .Where(i => !a.IsHole(i)).Select(a.Get);
        static string Slots(TsArray a)
        {
            if (a.Count == 0) return "[]";
            return string.Concat(Enumerable.Range(0, a.Count).Select(i => a.IsHole(i) ? "H" : a.Get(i) switch
            {
                TsVoid => "U",
                TsNull => "N",
                TsInt32Value n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException("Wrong native splice slot type")
            }));
        }
    }

    private static Func<TsValue, TsValue, TsValue?> Prepare(string method)
    {
        var run = PrepareNative(method, "a: any[], arg: any", "arg", 2);
        return (a, b) => run([a, b]);
    }

    private static Func<TsValue[], TsValue?> PrepareNative(string method, string parameters, string args, int arity)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(
            $"function main({parameters}) {{ return a.{method}({args}); }}").Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var ir = new IRGenerator().Generate(binder.Bind(parser.Parse()));
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var main = Assert.Single(ir.Functions, f => f.Name == "main");
        var block = Assert.Single(main.Blocks, b => b.Instructions.Any(i => i.Opcode == Opcode.Call));
        var native = Assert.Single(block.Instructions, i => i.Opcode == Opcode.Call);
        Assert.Equal("Array::" + method, Assert.IsType<string>(native.OperandObject));
        Assert.Equal(arity, native.Operand1);
        var index = block.Instructions.IndexOf(native);
        Assert.True(index >= arity);
        for (int i = 0; i < arity; i++)
        {
            var argument = block.Instructions[index - arity + i];
            Assert.Equal(Opcode.LoadArg, argument.Opcode);
            Assert.Equal(i, argument.Operand0);
        }
        var code = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(code);
        return values => new TypeSharp.VM.Interpreter.Interpreter().Execute(code, "main", values);
    }

    private static TsArrayValue ArrayOf(params TsValue[] values)
    {
        var array = new TsArray();
        foreach (var v in values) array.Add(v);
        return new TsArrayValue(array);
    }
}
