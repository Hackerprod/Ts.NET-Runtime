using System.Globalization;
using System.Numerics;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.VM.Tests;

public sealed class DateConstructorNumericTimeClipRangeTests
{
    private const double Limit = 8_640_000_000_000_000d;
    private const string Numeric = "function main(n: number): number { return new Date(n).getTime(); }";
    private const string Any = "function main(n: any): number { return new Date(n).getTime(); }";

    internal sealed record Row(string Id, string Source, TsValue[] Arguments, int Arity, string Target,
        double Stock, double Expected, bool Clock = false);

    internal static IEnumerable<Row> Cases()
    {
        foreach (var (id, value, expected) in new (string, double, double)[]
        {
            ("positive-over", Limit + 1, double.NaN),
            ("negative-over", -Limit - 1, double.NaN),
            ("positive-huge", 1e100, double.NaN),
            ("negative-huge", -1e100, double.NaN),
            ("positive-endpoint", Limit, Limit),
            ("negative-endpoint", -Limit, -Limit),
            ("positive-adjacent-inside", Limit - 1, Limit - 1),
            ("negative-adjacent-inside", -Limit + 1, -Limit + 1),
            ("positive-fraction", 42.75, 42),
            ("negative-fraction", -42.75, -42),
            ("epoch", 0, 0),
            ("negative-zero", -0d, -0d),
            ("positive-infinity", double.PositiveInfinity, double.NaN),
            ("negative-infinity", double.NegativeInfinity, double.NaN),
            ("nan", double.NaN, double.NaN)
        })
            yield return new Row(id, Numeric, [TsValue.FromFloat64(value)], 1, "Date::getTime",
                double.IsFinite(value) && double.IsNaN(expected) ? value : expected, expected);

        yield return new Row("float32-outside", Numeric, [TsValue.FromFloat32(1e16f)], 1,
            "Date::getTime", (double)1e16f, double.NaN);
        yield return new Row("decimal-outside", Numeric, [TsValue.FromDecimal(8_640_000_000_000_001m)],
            1, "Date::getTime", Limit + 1, double.NaN);
        yield return new Row("int32", Numeric, [TsValue.FromInt32(-123)], 1, "Date::getTime", -123, -123);

        var legacyInt = -9_000_000_000_000_001L;
        var legacyUInt = 9_000_000_000_000_001UL;
        var legacyBig = new BigInteger(9_000_000_000_000_002L);
        foreach (var (id, input, expected) in new (string, TsValue, double)[]
        {
            ("legacy-int64", TsValue.FromInt64(legacyInt), (double)legacyInt),
            ("legacy-uint64", TsValue.FromUInt64(legacyUInt), (double)legacyUInt),
            ("legacy-bigint", TsValue.FromBigInt(legacyBig), (double)legacyBig),
            ("legacy-null", TsValue.Null, 0),
            ("legacy-undefined", TsValue.Void, 0),
            ("legacy-true", TsValue.FromBool(true), 0),
            ("legacy-false", TsValue.FromBool(false), 0),
            ("legacy-object", TsValue.FromObject(new TsObject("Object")), 0),
            ("legacy-array", new TsArrayValue(new TsArray(0)), 0),
            ("text", TsValue.FromString("1970-01-01T00:00:02.000Z"), 2000),
            ("malformed-text", TsValue.FromString("not-a-date"), double.NaN),
            ("copy-native-valid", NativeDate(1234), 1234),
            ("copy-native-invalid", NativeDate(double.NaN), double.NaN),
            ("copy-native-legacy-outside", NativeDate(Limit + 1), Limit + 1)
        })
            yield return new Row(id, Any, [input], 1, "Date::getTime", expected, expected);

        yield return new Row("component-utc", "function main(): number { return new Date(1970, 0, 1, 0, 0, 2, 0).getTime(); }",
            [], 7, "Date::getTime", 2000, 2000);
        yield return new Row("component-short-year", "function main(): number { return new Date(73, 0).getTime(); }",
            [], 2, "Date::getTime", 94694400000d, 94694400000d);
        yield return new Row("utc-accessor", "function main(): number { return new Date(0).getUTCFullYear(); }",
            [], 1, "Date::getUTCFullYear", 1970, 1970);
        yield return new Row("clock", "function main(): number { return new Date().getTime(); }",
            [], 0, "Date::getTime", 0, 0, Clock: true);
    }

    private static TsValue NativeDate(double milliseconds)
    {
        var obj = new TsObject("Date");
        obj.SetField("__timestampMs", TsValue.FromFloat64(milliseconds));
        return TsValue.FromObject(obj);
    }

    internal static bool Same(double actual, double expected) => double.IsNaN(expected)
        ? double.IsNaN(actual)
        : BitConverter.DoubleToInt64Bits(actual) == BitConverter.DoubleToInt64Bits(expected);

    internal static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    internal static (double Value, string Evidence) Observe(Row row)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(row.Source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        var diagnostics = $"parser={parser.Diagnostics.Count} binder={binder.Diagnostics.All.Count}";
        if (parser.Diagnostics.Count != 0 || binder.Diagnostics.All.Count != 0)
            throw new InvalidOperationException($"{row.Id} {diagnostics}: {string.Join("; ", binder.Diagnostics.All)}");
        var ir = new IRGenerator().Generate(bound);
        var instructions = ir.Functions.Single(f => f.Name == "main").Blocks.SelectMany(b => b.Instructions).ToArray();
        var constructor = instructions.Single(i => i.Opcode == Opcode.NewObject);
        var call = instructions.Single(i => i.Opcode is Opcode.Call or Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        if (constructor.OperandObject is not string type || type != "Date" || constructor.Operand1 != row.Arity
            || call.Opcode != Opcode.Call || call.OperandObject is not string target || target != row.Target || call.Operand1 != 1)
            throw new InvalidOperationException($"{row.Id} unexpected constructor/native call");
        var bytecode = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(bytecode);
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(bytecode, "main", row.Arguments);
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (result is not TsFloat64Value milliseconds || result.ValueType != TsValueType.Float64)
            throw new InvalidOperationException($"{row.Id} unexpected result wrapper {result?.GetType()}");
        var value = milliseconds.Value;
        if (row.Clock && (!double.IsFinite(value) || value < before || value > after))
            throw new InvalidOperationException($"{row.Id} timestamp {Number(value)} is outside [{before}, {after}]");
        return (value, $"{diagnostics} verified ctor={type}/{constructor.Operand1} call={target}/{call.Operand1} tag={result.ValueType}");
    }

    [Fact]
    public void ConstructFromNumber_ClipsOnlyOutOfRangeTimestamps()
    {
        var mismatches = new List<string>();
        foreach (var row in Cases())
        {
            var (value, _) = Observe(row);
            if (!row.Clock && !Same(value, row.Expected))
                mismatches.Add($"{row.Id} expected={Number(row.Expected)} actual={Number(value)}");
        }
        Assert.True(mismatches.Count == 0, "C309_DATE_TIMECLIP_RANGE " + string.Join("; ", mismatches));
    }
}
