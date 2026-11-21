using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayJoinFlatNestedElementsTests(ITestOutputHelper output)
{
    private const string Source = "function main(a: any[], sep: any): string { return a.join(sep); }";
    private const string Fallback = "TypeSharp.VM.Memory.TsArray|end";

    [Theory]
    [InlineData("integer-pairs", "1,2|3,4")]
    [InlineData("boolean-fields", "true,false|end")]
    [InlineData("literal-strings", "x,y,,z|end")]
    [InlineData("null-hole-undefined", "1,,,,2|end")]
    [InlineData("empty-inner", "|end")]
    [InlineData("empty-outer-separator", "-2,0end")]
    [InlineData("flat-control", "7||x")]
    [InlineData("deeper-control", Fallback)]
    [InlineData("unsupported-control", Fallback)]
    [InlineData("cycle-control", Fallback)]
    public void ArrayJoin_FormatsOnlyFlatSupportedNestedFields(string id, string expected)
    {
        // Build a finite explicitly declared identity graph, even for self-references.
        var outer = new TsArrayValue(new TsArray());
        var a = new TsArrayValue(new TsArray());
        var b = new TsArrayValue(new TsArray());
        var arrays = new Dictionary<string, TsArrayValue> { ["outer"] = outer };
        var layouts = new Dictionary<string, object?[]>();
        var end = "end";
        if (id != "flat-control")
            arrays.Add("a", a);
        if (id is "integer-pairs" or "deeper-control")
            arrays.Add("b", b);
        switch (id)
        {
            case "integer-pairs":
                layouts["outer"] = [a, b]; layouts["a"] = [1, 2]; layouts["b"] = [3, 4]; break;
            case "boolean-fields":
                layouts["outer"] = [a, end]; layouts["a"] = [true, false]; break;
            case "literal-strings":
                layouts["outer"] = [a, end]; layouts["a"] = ["x,y", "", "z"]; break;
            case "null-hole-undefined":
                layouts["outer"] = [a, end]; layouts["a"] = [1, null, HoleSlot.Value, TsValue.Void, 2]; break;
            case "empty-inner":
                layouts["outer"] = [a, end]; layouts["a"] = []; break;
            case "empty-outer-separator":
                layouts["outer"] = [a, end]; layouts["a"] = [-2, 0]; break;
            case "flat-control":
                layouts["outer"] = [7, null, "x"]; break;
            case "deeper-control":
                layouts["outer"] = [a, end]; layouts["a"] = [1, b]; layouts["b"] = [2]; break;
            case "unsupported-control":
                layouts["outer"] = [a, end]; layouts["a"] = [1, 2L]; break;
            case "cycle-control":
                layouts["outer"] = [a, end]; layouts["a"] = [a]; break;
            default: throw new ArgumentOutOfRangeException(nameof(id));
        }
        var expectedRefs = new Dictionary<string, TsValue[]>();
        var expectedBackings = arrays.ToDictionary(x => x.Key, x => x.Value.Value);
        Assert.Equal(arrays.Count, expectedBackings.Values.Distinct(ReferenceEqualityComparer.Instance).Count());
        foreach (var (label, data) in layouts)
        {
            var entries = new TsValue[data.Length];
            var backing = expectedBackings[label];
            for (int i = 0; i < data.Length; i++)
            {
                entries[i] = data[i] switch
                {
                    int n => TsValue.FromInt32(n),
                    long n => TsValue.FromInt64(n),
                    bool flag => TsValue.FromBool(flag),
                    string text => TsValue.FromString(text),
                    null => TsValue.Null,
                    HoleSlot.Value => TsValue.Void,
                    TsArrayValue av => av,
                    TsVoid => TsValue.Void,
                    _ => throw new InvalidOperationException("Unspecified native slot")
                };
                if (data[i] is HoleSlot) backing.SetHole(i);
                else backing.Add(entries[i]);
            }
            expectedRefs.Add(label, entries);
        }
        var sepValue = id == "empty-outer-separator" ? "" : "|";
        var separator = new TsStringValue(sepValue);
        TsValue[] args = [outer, separator];

        string Snapshot()
        {
            Assert.Equal(2, args.Length);
            Assert.Same(outer, Assert.IsType<TsArrayValue>(args[0]));
            Assert.Same(separator, Assert.IsType<TsStringValue>(args[1]));
            Assert.Equal(sepValue, separator.Value);
            return JsonSerializer.Serialize(arrays.Select(pair =>
            {
                var (label, av) = pair;
                Assert.Same(expectedBackings[label], Assert.IsType<TsArray>(av.Value));
                Assert.Same(av.Value, av.RawValue);
                var data = layouts[label];
                Assert.Equal(data.Length, av.Value.Count);
                var slots = new List<object>();
                for (int i = 0; i < data.Length; i++)
                {
                    bool hole = av.Value.IsHole(i);
                    Assert.Equal(data[i] is HoleSlot, hole);
                    var val = av.Value.Get(i);
                    Assert.Same(expectedRefs[label][i], val);
                    object? field;
                    switch (data[i])
                    {
                        case int n: Assert.Equal(n, Assert.IsType<TsInt32Value>(val).Value); field = n; break;
                        case long n: Assert.Equal(n, Assert.IsType<TsInt64Value>(val).Value); field = n; break;
                        case bool flag: Assert.Equal(flag, Assert.IsType<TsBoolValue>(val).Value); field = flag; break;
                        case string text: Assert.Equal(text, Assert.IsType<TsStringValue>(val).Value); field = text; break;
                        case null: Assert.Same(TsValue.Null, Assert.IsType<TsNull>(val)); field = null; break;
                        case HoleSlot or TsVoid: Assert.Same(TsValue.Void, Assert.IsType<TsVoid>(val)); field = null; break;
                        case TsArrayValue child:
                            Assert.Same(child, Assert.IsType<TsArrayValue>(val));
                            field = Assert.Single(arrays, x => ReferenceEquals(x.Value, child)).Key;
                            Assert.Same(expectedBackings[(string)field], child.Value);
                            break;
                        default: throw new InvalidOperationException("Unspecified native slot");
                    }
                    slots.Add(new { i, hole, type = val.GetType().Name, field, sameReference = true });
                }
                return new { label, count = av.Value.Count, sameBacking = true, slots };
            }).ToArray());
        }

        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(Source).Tokenize());
        var tree = parser.Parse();
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions);
        Assert.Equal("main", main.Name);
        Assert.Equal(new[] { "a", "sep" }, main.Parameters.Select(p => p.Name).ToArray());
        var ops = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.DoesNotContain(ops, op => op.Opcode is Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        var call = Assert.Single(ops, op => op.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::join", target);
        Assert.Equal(2, call.Operand1);
        var block = Assert.Single(main.Blocks, b => b.Instructions.Contains(call));
        int index = block.Instructions.IndexOf(call);
        Assert.True(index >= 2);
        var loadA = block.Instructions[index - 2];
        var loadSep = block.Instructions[index - 1];
        Assert.Equal(Opcode.LoadArg, loadA.Opcode);
        Assert.Equal(0, loadA.Operand0);
        Assert.Equal(Opcode.LoadArg, loadSep.Opcode);
        Assert.Equal(1, loadSep.Operand0);

        string before = Snapshot();
        var code = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(code, "main", args); });
        Assert.Null(escaped);
        string after = Snapshot();
        Assert.Equal(before, after);
        var actual = Assert.IsType<TsStringValue>(result).Value;
        output.WriteLine("C101-alt Array.join row " + JsonSerializer.Serialize(new
        {
            id, source = Source, parserCount = parser.Diagnostics.Count(), binderCount = binder.Diagnostics.All.Count(),
            target, opcode = call.Opcode.ToString(), argc = call.Operand1, argOps = new[] { loadA.Operand0, loadSep.Operand0 },
            argTypes = args.Select(v => v.GetType().Name), separator = separator.Value, before, after,
            unchanged = true, exception = escaped?.GetType().FullName, actualType = result!.GetType().Name, actual, expected
        }));
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"C101_ALT_JOIN_RESULT_MISMATCH {id}: expected={JsonSerializer.Serialize(expected)}; actual={JsonSerializer.Serialize(actual)}");
    }

    private enum HoleSlot { Value }
}
