using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class NativeKeyedCollectionNullishTests(ITestOutputHelper output)
{
    private const string MapSource = """
        function make(): Map<any, int32> { return new Map<any, int32>(); }
        function put(m: Map<any, int32>, k: any, v: int32): Map<any, int32> { return m.set(k, v); }
        function lookup(m: Map<any, int32>, k: any): any { return m.get(k); }
        function has(m: Map<any, int32>, k: any): boolean { return m.has(k); }
        function remove(m: Map<any, int32>, k: any): boolean { return m.delete(k); }
        """;
    private const string SetSource = """
        function make(): Set<any> { return new Set<any>(); }
        function put(s: Set<any>, k: any): Set<any> { return s.add(k); }
        function has(s: Set<any>, k: any): boolean { return s.has(k); }
        function remove(s: Set<any>, k: any): boolean { return s.delete(k); }
        """;

    [Theory]
    [InlineData("map-null-undefined")]
    [InlineData("set-null-undefined")]
    [InlineData("map-same-kind")]
    [InlineData("set-same-kind")]
    [InlineData("map-samevaluezero")]
    [InlineData("set-samevaluezero")]
    public void NativeKeyedCollections_KeepExactKeyEquality(string row)
    {
        bool map = row.StartsWith("map-", StringComparison.Ordinal);
        bool distinct = row.EndsWith("-null-undefined", StringComparison.Ordinal);
        string type = map ? "Map" : "Set";
        string source = map ? MapSource : SetSource;
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        (string name, string target, int argc)[] routes = map
            ? [("make", "Map", 0), ("put", "Map::set", 3), ("lookup", "Map::get", 2),
                ("has", "Map::has", 2), ("remove", "Map::delete", 2)]
            : [("make", "Set", 0), ("put", "Set::add", 2), ("has", "Set::has", 2),
                ("remove", "Set::delete", 2)];
        Assert.Equal(routes.Length, ir.Functions.Count());
        var irRoutes = routes.Select(route =>
        {
            var fn = Assert.Single(ir.Functions, f => f.Name == route.name);
            var ops = fn.Blocks.SelectMany(b => b.Instructions).ToArray();
            Assert.DoesNotContain(ops, op => op.Opcode is Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
            var dispatched = Assert.Single(ops, op => op.Opcode is Opcode.NewObject or Opcode.Call);
            Assert.Equal(route.name == "make" ? Opcode.NewObject : Opcode.Call, dispatched.Opcode);
            Assert.Equal(route.target, Assert.IsType<string>(dispatched.OperandObject));
            Assert.Equal(route.argc, dispatched.Operand1);
            var block = Assert.Single(fn.Blocks, b => b.Instructions.Contains(dispatched));
            int end = block.Instructions.IndexOf(dispatched);
            int[] args = block.Instructions.Take(end).TakeLast(route.argc).Select(op =>
            {
                Assert.Equal(Opcode.LoadArg, op.Opcode);
                return op.Operand0;
            }).ToArray();
            Assert.Equal(Enumerable.Range(0, route.argc), args);
            return new { route.name, opcode = dispatched.Opcode.ToString(), route.target, route.argc, args };
        }).ToArray();

        (string name, TsValue a, TsValue b)[] keys = distinct
            ? [("null-undefined", TsValue.Null, TsValue.Void)]
            : row.EndsWith("-same-kind", StringComparison.Ordinal)
                ? [("null-null", new TsNull(), new TsNull()), ("undefined-undefined", new TsVoid(), new TsVoid())]
                : [("nan-nan", new TsFloat64Value(double.NaN), new TsFloat64Value(double.NaN)),
                    ("negative-positive-zero", new TsFloat64Value(BitConverter.Int64BitsToDouble(long.MinValue)),
                        new TsFloat64Value(0.0))];

        var code = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        object Backing(TsValue value) => map ? Assert.IsType<TsMapValue>(value).Value : Assert.IsType<TsSetValue>(value).Value;
        int Count(object backing) => map ? Assert.IsType<TsMap>(backing).Count : Assert.IsType<TsSet>(backing).Count;
        var backings = new List<object>();
        var pairs = keys.Select(pair =>
        {
            Assert.NotSame(pair.a, pair.b);
            var (aType, aTag, aSentinel) = Describe(pair.a);
            var (bType, bTag, bSentinel) = Describe(pair.b);
            switch (pair.name)
            {
                case "null-undefined": Assert.Equal(("Null", "Void"), (aTag, bTag)); break;
                case "null-null": Assert.Equal(("Null", "Null"), (aTag, bTag)); break;
                case "undefined-undefined": Assert.Equal(("Void", "Void"), (aTag, bTag)); break;
                case "nan-nan": Assert.Equal(("NaN", "NaN"), (aSentinel, bSentinel)); break;
                case "negative-positive-zero":
                    Assert.Equal(("8000000000000000", "0000000000000000"), (aSentinel, bSentinel));
                    break;
            }
            var receiver = Assert.IsAssignableFrom<TsValue>(vm.Execute(code, "make"));
            Assert.Equal(map ? TsValueType.Map : TsValueType.Set, receiver.ValueType);
            var backing = Backing(receiver);
            var freshCount = Count(backing);
            Assert.Equal(0, freshCount);
            Assert.DoesNotContain(backings, prior => ReferenceEquals(prior, backing));
            backings.Add(backing);

            var putWrappers = new List<bool>();
            var stableBackings = new List<bool>();
            TsValue Invoke(string operation, TsValue key, params TsValue[] payload)
            {
                TsValue[] args = [receiver, key, .. payload];
                var result = vm.Execute(code, operation, args);
                Assert.Same(receiver, args[0]);
                Assert.Same(key, args[1]);
                bool sameBacking = ReferenceEquals(backing, Backing(args[0]));
                Assert.True(sameBacking);
                stableBackings.Add(sameBacking);
                return Assert.IsAssignableFrom<TsValue>(result);
            }
            void Put(TsValue key, int number)
            {
                var result = map ? Invoke("put", key, new TsInt32Value(number)) : Invoke("put", key);
                bool sameWrapper = ReferenceEquals(receiver, result);
                Assert.True(sameWrapper);
                putWrappers.Add(sameWrapper);
            }
            bool Has(TsValue key) => Predicate(Invoke("has", key));
            bool Delete(TsValue key) => Predicate(Invoke("remove", key));
            object[] Get(TsValue key)
            {
                var value = Invoke("lookup", key);
                if (value is TsVoid)
                {
                    Assert.Equal(TsValueType.Void, value.ValueType);
                    return ["Void"];
                }
                var integer = Assert.IsType<TsInt32Value>(value);
                Assert.Equal(TsValueType.Int32, integer.ValueType);
                return ["Int32", integer.Value];
            }

            Put(pair.a, 10);
            int firstCount = Count(backing);
            bool firstHas = Has(pair.b);
            object[]? firstGet = map ? Get(pair.b) : null;
            Put(pair.b, 20);
            int secondCount = Count(backing);
            object[] actual = map
                ? [firstCount, firstHas, firstGet!, secondCount, Get(pair.a), Get(pair.b), Delete(pair.b),
                    Count(backing), Has(pair.a), Get(pair.a), Has(pair.b), Delete(pair.b)]
                : [firstCount, firstHas, secondCount, Has(pair.a), Has(pair.b), Delete(pair.b),
                    Count(backing), Has(pair.a), Has(pair.b), Delete(pair.b)];
            object[] expected = map
                ? distinct
                    ? [1, false, Void(), 2, Int(10), Int(20), true, 1, true, Int(10), false, false]
                    : [1, true, Int(10), 1, Int(20), Int(20), true, 0, false, Void(), false, false]
                : distinct
                    ? [1, false, 2, true, true, true, 1, true, false, false]
                    : [1, true, 1, true, true, true, 0, false, false, false];
            return new
            {
                pair.name, aType, aTag, aSentinel, bType, bTag, bSentinel,
                sameKeyWrapper = ReferenceEquals(pair.a, pair.b),
                receiverType = receiver.GetType().Name, receiverTag = receiver.ValueType.ToString(),
                backingType = backing.GetType().Name, freshCount, putWrappers, stableBackings, actual, expected
            };
        }).ToArray();
        output.WriteLine("C104 native keyed row " + JsonSerializer.Serialize(new
        {
            row, source, parserCount = parser.Diagnostics.Count(), binderCount = binder.Diagnostics.All.Count(),
            irRoutes, completed = true, escaped = (string?)null, pairs
        }));
        string actual = JsonSerializer.Serialize(pairs.Select(pair => pair.actual));
        string expected = JsonSerializer.Serialize(pairs.Select(pair => pair.expected));
        Assert.True(actual == expected, $"C104_NATIVE_KEY_EQUALITY_MISMATCH {row}: actual {actual}; expected {expected}");
    }

    private static bool Predicate(TsValue value)
    {
        var boolean = Assert.IsType<TsBoolValue>(value);
        Assert.Equal(TsValueType.Bool, boolean.ValueType);
        return boolean.Value;
    }

    private static (string type, string tag, string? sentinel) Describe(TsValue key)
    {
        string? sentinel;
        switch (key)
        {
            case TsNull: Assert.Equal(TsValueType.Null, key.ValueType); sentinel = null; break;
            case TsVoid: Assert.Equal(TsValueType.Void, key.ValueType); sentinel = null; break;
            default:
                var number = Assert.IsType<TsFloat64Value>(key);
                Assert.Equal(TsValueType.Float64, number.ValueType);
                sentinel = double.IsNaN(number.Value) ? "NaN" : BitConverter.DoubleToInt64Bits(number.Value).ToString("x16");
                break;
        }
        return (key.GetType().Name, key.ValueType.ToString(), sentinel);
    }

    private static object[] Void() => ["Void"];
    private static object[] Int(int value) => ["Int32", value];
}
