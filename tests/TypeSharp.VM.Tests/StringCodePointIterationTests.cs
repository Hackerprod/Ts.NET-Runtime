using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringCodePointIterationTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Rows()
    {
        (string Id, string Input, string Expected)[] cases =
        [
            ("astral", "0061+D83D+DE00+007A", "0061,D83D+DE00,007A"),
            ("adjacent", "D801+DC00+D83D+DE00", "D801+DC00,D83D+DE00"),
            ("bmp", "0061+00E9+03A9", "0061,00E9,03A9"),
            ("high-gap", "D83D+0058", "D83D,0058"),
            ("low-only", "DE00", "DE00"),
            ("malformed-pair", "D83D+D83D+DE00+DE00", "D83D,D83D+DE00,DE00"),
            ("combining", "0065+0301", "0065,0301"),
            ("empty", "", ""),
        ];
        foreach (var route in new[] { "from", "array", "call", "loop", "mapper" })
            foreach (var (id, input, expected) in cases)
                yield return new object[] { route, id, input, expected };
        yield return new object[] { "unit-api", "unit-api", "0061+D83D+DE00+007A", "#4,D83D,DE00,#55357,#56832" };
    }

    private static string Decode(string hex) =>
        new(hex.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(unit => (char)ushort.Parse(unit, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray());

    private static string Encode(string value) =>
        string.Join("+", value.Select(unit => ((ushort)unit).ToString("X4", CultureInfo.InvariantCulture)));

    private static int Integer(TsValue value) => value switch
    {
        TsInt32Value n => n.Value,
        TsInt64Value n => checked((int)n.Value),
        TsUInt64Value n => checked((int)n.Value),
        TsFloat32Value n when float.IsFinite(n.Value) && MathF.Truncate(n.Value) == n.Value => checked((int)n.Value),
        TsFloat64Value n when double.IsFinite(n.Value) && Math.Truncate(n.Value) == n.Value => checked((int)n.Value),
        TsDecimalValue n when decimal.Truncate(n.Value) == n.Value => checked((int)n.Value),
        _ => throw new InvalidOperationException($"Expected exact integer, got {value.GetType().Name}")
    };

    private static TsValue Slot(TsArray array, int index)
    {
        Assert.False(array.IsHole(index));
        return array.Get(index);
    }

    private static string Script(string route) => route switch
    {
        "from" => "function main(s: string): any { return Array.from(s); }",
        "array" => "function main(s: string): any { return [...s]; }",
        "call" => "function collect(...values: any): any { return values; } function main(s: string): any { return collect(...s); }",
        "loop" => "function main(s: string): any { let values: any[] = []; for (let value of s) { values.push(value); } return values; }",
        "mapper" => "function entry(value: string, index: number): any { let pair: any[] = [value, index]; return pair; } function main(s: string): any { return Array.from(s, entry); }",
        "unit-api" => "function main(s: string): any { let parts: any[] = [s.length, s[1], s[2], s.charCodeAt(1), s.charCodeAt(2)]; return parts; }",
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };

    private static string Marker(Instruction i)
    {
        var op = i.Opcode.ToString();
        return i.Opcode switch
        {
            Opcode.LoadArg or Opcode.StoreLocal or Opcode.LoadLocal or Opcode.NewArray or Opcode.LoadConst_I32 =>
                $"{op}:{i.Operand0}",
            Opcode.Call => $"{op}:{i.OperandObject}/{i.Operand1}",
            Opcode.LoadFunc or Opcode.LoadField => $"{op}:{i.OperandObject}",
            _ => op
        };
    }

    private static void VerifyRoute(string route, string[][] blocks, string[][] helper)
    {
        string[] Exact(params string[] markers) => markers;
        var ops = blocks.SelectMany(block => block).ToArray();
        static void LocalPair(string store, string load)
        {
            Assert.StartsWith("StoreLocal:", store);
            Assert.Equal(store.Replace("StoreLocal:", "LoadLocal:", StringComparison.Ordinal), load);
        }
        switch (route)
        {
            case "from":
                Assert.Equal(Exact("LoadArg:0", "Call:Array::from/1", "Return"), Assert.Single(blocks));
                break;
            case "array":
                Assert.Equal(Exact("NewArray:0", "Dup", "LoadArg:0", "ArrayAppendSpread", "Return"), Assert.Single(blocks));
                break;
            case "call":
                Assert.Equal(Exact("LoadFunc:collect", "NewArray:0", "Dup", "LoadArg:0", "ArrayAppendSpread", "CallDynamicArray", "Return"), Assert.Single(blocks));
                Assert.Equal(Exact("LoadArg:0", "Return"), Assert.Single(helper));
                break;
            case "mapper":
                Assert.Equal(Exact("LoadArg:0", "LoadFunc:entry", "Call:Array::from/2", "Return"), Assert.Single(blocks));
                var entry = Assert.Single(helper);
                Assert.Equal(Exact("LoadArg:0", "LoadArg:1", "NewArray:2"), entry[..3]);
                Assert.Equal(6, entry.Length);
                LocalPair(entry[3], entry[4]);
                Assert.Equal("Return", entry[5]);
                break;
            case "loop":
            {
                var start = Assert.Single(blocks, block => block.Contains("IterableValues"));
                var i = Array.IndexOf(start, "IterableValues");
                Assert.Equal("LoadArg:0", start[i - 1]);
                Assert.StartsWith("StoreLocal:", start[i + 1]);
                string arr = start[i + 1].Replace("StoreLocal:", "LoadLocal:", StringComparison.Ordinal);
                Assert.Single(ops, op => op.StartsWith("Call:", StringComparison.Ordinal));
                string[] loop = Assert.Single(blocks, block => block.Contains("Call:Array::push/2"));
                var e = Array.IndexOf(loop, "LoadElement");
                var p = Array.IndexOf(loop, "Call:Array::push/2");
                Assert.Equal(arr, loop[e - 2]);
                Assert.StartsWith("LoadLocal:", loop[e - 1]);
                Assert.StartsWith("StoreLocal:", loop[e + 1]);
                Assert.Equal(loop[e + 1].Replace("StoreLocal:", "LoadLocal:", StringComparison.Ordinal), loop[p - 1]);
                Assert.StartsWith("LoadLocal:", loop[p - 2]);
                Assert.Contains(blocks, block => block.Contains("LoadField:length") &&
                    block[Array.IndexOf(block, "LoadField:length") - 1] == arr);
                break;
            }
            case "unit-api":
            {
                var unit = Assert.Single(blocks);
                Assert.Equal(Exact("LoadArg:0", "LoadField:length", "LoadArg:0", "LoadConst_I32:1", "LoadElement",
                    "LoadArg:0", "LoadConst_I32:2", "LoadElement", "LoadArg:0", "LoadConst_I32:1",
                    "Call:String::charCodeAt/2", "LoadArg:0", "LoadConst_I32:2", "Call:String::charCodeAt/2",
                    "NewArray:5"), unit[..15]);
                Assert.Equal(18, unit.Length);
                LocalPair(unit[15], unit[16]);
                Assert.Equal("Return", unit[17]);
                break;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void StringIteration_YieldsCodePoints(string route, string row, string input, string expected)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(Script(route)).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        int parserCount = parser.Diagnostics.Count();
        int binderCount = binder.Diagnostics.All.Count();
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        string[][] blocks = main.Blocks.Select(block => block.Instructions.Select(Marker).ToArray()).ToArray();
        Assert.Equal("s", Assert.Single(main.Parameters).Name);
        var helperFunction = route is "call" or "mapper"
            ? Assert.Single(ir.Functions, function => function.Name == (route == "call" ? "collect" : "entry"))
            : null;
        bool? rest = helperFunction is not null && route == "call"
            ? Assert.Single(helperFunction.Parameters).IsRest
            : null;
        if (route == "call")
            Assert.True(rest == true);
        if (route == "mapper")
            Assert.Equal(new[] { "value", "index" }, helperFunction!.Parameters.Select(parameter => parameter.Name));
        string[][] helper = helperFunction?.Blocks
            .Select(block => block.Instructions.Select(Marker).ToArray()).ToArray() ?? [];
        output.WriteLine("C95 IR " + JsonSerializer.Serialize(new { route, row, parserCount, binderCount, blocks, helper, rest }));
        VerifyRoute(route, blocks, helper);

        var argument = TsValue.FromString(Decode(input));
        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = vm.Execute(bytecode, "main", new TsValue[] { argument }); });
        Assert.Null(escaped);
        var outer = Assert.IsType<TsArrayValue>(result).Value;
        var actual = new List<string>();
        var itemTypes = new List<string>();
        var indexes = new List<int>();
        for (int i = 0; i < outer.Count; i++)
        {
            var item = Slot(outer, i);
            itemTypes.Add(item.GetType().Name);
            if (route == "mapper")
            {
                var pair = Assert.IsType<TsArrayValue>(item).Value;
                Assert.Equal(2, pair.Count);
                actual.Add(Encode(Assert.IsType<TsStringValue>(Slot(pair, 0)).Value));
                var index = Slot(pair, 1);
                indexes.Add(Integer(index));
                itemTypes[^1] += $":TsStringValue:{index.GetType().Name}";
                Assert.Equal(i, indexes[^1]);
            }
            else if (route == "unit-api" && i is 0 or 3 or 4)
                actual.Add("#" + Integer(item).ToString(CultureInfo.InvariantCulture));
            else
                actual.Add(Encode(Assert.IsType<TsStringValue>(item).Value));
        }
        string[] want = expected.Split(',', StringSplitOptions.RemoveEmptyEntries);
        output.WriteLine("C95 iteration row " + JsonSerializer.Serialize(new
        {
            route, row, parserCount, binderCount, blocks, helper, rest,
            input = Encode(Assert.IsType<TsStringValue>(argument).Value),
            returnType = result!.GetType().Name, itemTypes, indexes,
            expected = want, actual, expectedCount = want.Length, actualCount = outer.Count,
            escaped = escaped?.GetType().FullName
        }));
        Assert.True(want.SequenceEqual(actual, StringComparer.Ordinal),
            $"C95_ITERATION_MISMATCH {route}/{row}: expected [{string.Join(",", want)}]; actual [{string.Join(",", actual)}]");
    }
}
