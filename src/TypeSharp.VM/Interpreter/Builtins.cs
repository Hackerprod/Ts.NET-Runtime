using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeSharp.VM.Memory;

namespace TypeSharp.VM.Interpreter;

// Native implementations of the JS-style globals the binder exposes
// (Math.*, Number.*, console.*) and of array/string instance members.
// Instance members receive the receiver as args[0].
public static class Builtins
{
    private static readonly Dictionary<string, Func<TsValue[], TsValue>> Table = Build();

    public static IReadOnlyCollection<string> Names => Table.Keys;

    public static bool TryGet(string name, out Func<TsValue[], TsValue> builtin) =>
        Table.TryGetValue(name, out builtin!);

    private static Dictionary<string, Func<TsValue[], TsValue>> Build()
    {
        var t = new Dictionary<string, Func<TsValue[], TsValue>>(StringComparer.Ordinal)
        {
            // ── Math ──
            ["Math::abs"] = args => Num(Math.Abs(args[0] is TsNull ? 0d : D(args[0]))),
            ["Math::floor"] = args => Num(Math.Floor(args[0] is TsNull ? 0d : D(args[0]))),
            ["Math::ceil"] = args => Num(Math.Ceiling(args[0] is TsNull ? 0d : D(args[0]))),
            ["Math::round"] = args => Num(RoundJsNumber(args[0] is TsNull ? 0d : D(args[0]))),
            ["Math::trunc"] = args => args[0] is TsNull ? Num(0d) : Num(Math.Truncate(D(args[0]))),
            ["Math::sqrt"] = args => Num(Math.Sqrt(args[0] is TsNull ? 0d : D(args[0]))),
            ["Math::cbrt"] = args => Num(Math.Cbrt(args[0] is TsNull ? 0d : D(args[0]))),
            ["Math::pow"] = args =>
            {
                var @base = D(args[0]);
                var exponent = D(args[1]);
                // ECMAScript differs from Math.Pow for these five input combinations.
                if ((@base == 1.0 && double.IsNaN(exponent)) ||
                    ((@base == 1.0 || @base == -1.0) && double.IsInfinity(exponent)))
                    return Num(double.NaN);

                return Num(Math.Pow(@base, exponent));
            },
            ["Math::log"] = args => Num(Math.Log(
                args.Length == 1 && args[0] is TsStringValue text && TrimEcmaWhitespace(text.Value).Length == 0
                    ? 0.0
                    : args.Length == 1 && args[0] is TsNull ? 0d : D(args[0]))),
            ["Math::log2"] = args => Num(Math.Log2(args[0] is TsNull ? 0d : D(args[0]))),
            ["Math::log10"] = args => Num(Math.Log10(args[0] is TsNull ? 0d : D(args[0]))),
            ["Math::exp"] = args => Num(Math.Exp(args[0] is TsNull ? 0d : D(args[0]))),
            ["Math::sign"] = args =>
            {
                var n = args[0] is TsNull ? 0d : D(args[0]);
                return Num(double.IsNaN(n) || n == 0.0 ? n : n < 0.0 ? -1.0 : 1.0);
            },
            ["Math::sin"] = args =>
            {
                if (args.Length == 0) return Num(double.NaN);
                var n = D(args[0]);
                return Num(n == 0.0 ? n : System.Math.Sin(n));
            },
            ["Math::cos"] = args => Num(args.Length == 0 ? double.NaN : System.Math.Cos(args[0] is TsNull ? 0d : D(args[0]))),
            ["Math::fround"] = args => Num(args.Length == 0 ? double.NaN : (double)(float)D(args[0])),
            ["Math::clz32"] = args => Num(CountLeadingZeroBits32(D(args[0]))),
            ["Math::imul"] = args => Num(unchecked((int)(ToUInt32(D(args[0])) * ToUInt32(D(args[1]))))),
            ["Math::acos"] = args => Num(args.Length == 0 ? double.NaN : System.Math.Acos(D(args[0]))),
            ["Math::acosh"] = args => Num(args.Length == 0 ? double.NaN : System.Math.Acosh(D(args[0]))),
            ["Math::asin"] = args =>
            {
                if (args.Length == 0) return Num(double.NaN);
                var n = args[0] is TsNull ? 0d : D(args[0]);
                return Num(n == 0.0 ? n : System.Math.Asin(n));
            },
            ["Math::asinh"] = args =>
            {
                if (args.Length == 0) return Num(double.NaN);
                var n = D(args[0]);
                return Num(n == 0.0 ? n : System.Math.Asinh(n));
            },
            ["Math::cosh"] = args => Num(args.Length == 0 ? double.NaN : System.Math.Cosh(D(args[0]))),
            ["Math::sinh"] = args =>
            {
                if (args.Length == 0) return Num(double.NaN);
                var n = D(args[0]);
                return Num(n == 0.0 ? n : System.Math.Sinh(n));
            },
            ["Math::tan"] = args =>
            {
                if (args.Length == 0) return Num(double.NaN);
                var n = args[0] is TsNull ? 0d : D(args[0]);
                return Num(n == 0.0 ? n : System.Math.Tan(n));
            },
            ["Math::atan"] = args =>
            {
                if (args.Length == 0) return Num(double.NaN);
                var n = args[0] is TsNull ? 0d : D(args[0]);
                return Num(n == 0.0 ? n : System.Math.Atan(n));
            },
            ["Math::atan2"] = args =>
            {
                if (args.Length < 2) return Num(double.NaN);
                double y = D(args[0]), x = D(args[1]);
                if (double.IsNaN(y) || double.IsNaN(x)) return Num(double.NaN);
                if (double.IsInfinity(y))
                {
                    double angle = double.IsPositiveInfinity(x) ? System.Math.PI / 4.0
                        : double.IsNegativeInfinity(x) ? 3.0 * System.Math.PI / 4.0
                        : System.Math.PI / 2.0;
                    return Num(System.Math.CopySign(angle, y));
                }
                if (y == 0.0)
                    return Num(BitConverter.DoubleToInt64Bits(x) < 0 ? System.Math.CopySign(System.Math.PI, y) : y);
                if (double.IsPositiveInfinity(x)) return Num(System.Math.CopySign(0.0, y));
                if (double.IsNegativeInfinity(x)) return Num(System.Math.CopySign(System.Math.PI, y));
                if (x == 0.0) return Num(System.Math.CopySign(System.Math.PI / 2.0, y));
                return Num(System.Math.Atan2(y, x));
            },
            ["Math::atanh"] = args =>
            {
                if (args.Length == 0) return Num(double.NaN);
                var n = D(args[0]);
                return Num(n == 0.0 ? n : System.Math.Atanh(n));
            },
            ["Math::tanh"] = args =>
            {
                if (args.Length == 0) return Num(double.NaN);
                var n = D(args[0]);
                return Num(n == 0.0 ? n : System.Math.Tanh(n));
            },
            ["Math::random"] = _ => Num(Random.Shared.NextDouble()),
            ["Math::min"] = Min,
            ["Math::max"] = Max,
            ["Math::hypot"] = Hypot,

            // ── Number ──
            ["Number::isInteger"] = args => Bool(args[0] switch
            {
                TsInt32Value => true,
                TsFloat32Value f => float.IsFinite(f.Value) && f.Value == MathF.Truncate(f.Value),
                TsFloat64Value d => double.IsFinite(d.Value) && d.Value == Math.Truncate(d.Value),
                _ => false
            }),
            ["Number::isFinite"] = args => Bool(args[0] is TsInt32Value or TsDecimalValue ||
                (args[0] is TsFloat64Value fd && double.IsFinite(fd.Value)) ||
                (args[0] is TsFloat32Value ff && float.IsFinite(ff.Value))),
            ["Number::isNaN"] = args => Bool(
                (args[0] is TsFloat64Value fd && double.IsNaN(fd.Value)) ||
                (args[0] is TsFloat32Value ff && float.IsNaN(ff.Value))),
            ["Number::isSafeInteger"] = args =>
            {
                static bool IsSafeBinary64(double value) =>
                    value >= -9007199254740991d && value <= 9007199254740991d && value == Math.Truncate(value);

                return Bool(args[0] switch
                {
                    TsInt32Value => true,
                    TsFloat32Value f => IsSafeBinary64((double)f.Value),
                    TsFloat64Value d => IsSafeBinary64(d.Value),
                    TsDecimalValue m => m.Value >= -9007199254740991m && m.Value <= 9007199254740991m
                        && m.Value == decimal.Truncate(m.Value),
                    _ => false
                });
            },
            ["Number::parseFloat"] = args => Num(ParseFloatPrefix(S(args[0]))),
            ["Number::parseInt"] = ParseIntBuiltin,
            ["Number"] = args => Num(
                args.Length == 0 || args[0] is TsNull ||
                (args[0] is TsStringValue text && TrimEcmaWhitespace(text.Value).Length == 0)
                    ? 0d
                    : D(args[0])),

            // ── console ──
            ["console::log"] = args => { Console.WriteLine(string.Join(" ", args.Select(a => a.ToString()))); return TsValue.Void; },
            ["console::error"] = args => { Console.Error.WriteLine(string.Join(" ", args.Select(a => a.ToString()))); return TsValue.Void; },
            ["console::warn"] = args => { Console.Error.WriteLine(string.Join(" ", args.Select(a => a.ToString()))); return TsValue.Void; },

            ["JSON::stringify"] = args => args.Length == 0 || args[0] is TsVoid
                ? TsValue.Void
                : TsValue.FromString(JsonSerializer.Serialize(
                    ToJsonCompatible(args[0], new HashSet<object>(ReferenceEqualityComparer.Instance), 0),
                    new JsonSerializerOptions { MaxDepth = 1001 })),

            // ── Array construction + statics ──
            ["Array::ctor"] = args =>
            {
                // new Array(n) → n empty slots; new Array(a, b, …) → elements.
                if (args.Length == 1 && args[0] is TsInt32Value or TsInt64Value or TsFloat64Value)
                {
                    int size = I(args[0]);
                    var sized = new TsArray(Math.Max(size, 4));
                    for (int i = 0; i < size; i++) sized.SetHole(i);
                    return new TsArrayValue(sized);
                }
                var fromElements = new TsArray(args.Length);
                foreach (var arg in args) fromElements.Add(arg);
                return new TsArrayValue(fromElements);
            },
            ["Array::of"] = args =>
            {
                var arr = new TsArray(args.Length);
                foreach (var arg in args) arr.Add(arg);
                return new TsArrayValue(arr);
            },
            ["Array::isArray"] = args => Bool(args.Length > 0 && args[0] is TsArrayValue),
            ["Uint8Array::slice"] = args =>
            {
                var bytes = Bytes(args[0]);
                int start = args.Length > 1 ? Normalize(I(args[1]), bytes.Length) : 0;
                int end = args.Length > 2 && args[2] is not TsVoid ? Normalize(I(args[2]), bytes.Length) : bytes.Length;
                return bytes.Slice(start, end);
            },
            ["Uint8Array::subarray"] = args =>
            {
                var bytes = Bytes(args[0]);
                int start = args.Length > 1 ? Normalize(I(args[1]), bytes.Length) : 0;
                int end = args.Length > 2 && args[2] is not TsVoid ? Normalize(I(args[2]), bytes.Length) : bytes.Length;
                return bytes.Slice(start, end);
            },
            ["Uint8Array::reverse"] = args =>
            {
                System.Array.Reverse(Bytes(args[0]).Value);
                return args[0];
            },
            ["Uint8Array::toReversed"] = args =>
            {
                var clone = (byte[])Bytes(args[0]).Value.Clone();
                System.Array.Reverse(clone);
                return new TsUint8ArrayValue(clone);
            },
            ["Uint8Array::set"] = args =>
            {
                var target = Bytes(args[0]);
                if (args.Length < 2) return TsValue.Null;
                int offset = args.Length > 2 ? I(args[2]) : 0;
                if (args[1] is TsUint8ArrayValue sourceBytes)
                {
                    for (int i = 0; i < sourceBytes.Length && offset + i < target.Length; i++)
                        target.Value[offset + i] = sourceBytes.Get(i);
                    return TsValue.Void;
                }
                else if (args[1] is TsArrayValue sourceArray)
                {
                    for (int i = 0; i < sourceArray.Value.Count && offset + i < target.Length; i++)
                        target.Value[offset + i] = ToByte(sourceArray.Value.Get(i));
                    return TsValue.Void;
                }
                return TsValue.Null;
            },

            // ── Map instance members (receiver = args[0]) ──
            ["Map::set"] = args =>
            {
                var map = Map(args[0]);
                if (args.Length < 3)
                    throw new InvalidOperationException("Map.set requires key and value arguments");
                map.Set(args[1], args[2]);
                return args[0];
            },
            ["Map::get"] = args =>
            {
                var map = Map(args[0]);
                if (args.Length < 2)
                    throw new InvalidOperationException("Map.get requires a key argument");
                return map.Get(args[1]);
            },
            ["Map::has"] = args =>
            {
                var map = Map(args[0]);
                if (args.Length < 2)
                    throw new InvalidOperationException("Map.has requires a key argument");
                return Bool(map.Contains(args[1]));
            },
            ["Map::delete"] = args =>
            {
                var map = Map(args[0]);
                if (args.Length < 2)
                    throw new InvalidOperationException("Map.delete requires a key argument");
                return Bool(map.Remove(args[1]));
            },
            ["Map::clear"] = args =>
            {
                Map(args[0]).Clear();
                return TsValue.Void;
            },
            ["Map::values"] = args =>
            {
                var result = new TsArray();
                foreach (var entry in Map(args[0]).Entries)
                    result.Add(entry.Value);
                return new TsArrayValue(result);
            },
            ["Map::keys"] = args =>
            {
                var result = new TsArray();
                foreach (var entry in Map(args[0]).Entries)
                    result.Add(entry.Key);
                return new TsArrayValue(result);
            },
            ["Map::entries"] = args =>
            {
                var result = new TsArray();
                foreach (var entry in Map(args[0]).Entries)
                    result.Add(CreateEntryPair(entry.Key, entry.Value));
                return new TsArrayValue(result);
            },

            // Date instance members (receiver = args[0]).
            ["Date::getTime"] = args => DateTimestamp(args[0]),
            ["Date::valueOf"] = args => DateTimestamp(args[0]),
            ["Date::toISOString"] = args =>
            {
                var timestamp = D(DateTimestamp(args[0]));
                if (!double.IsFinite(timestamp))
                    throw new InvalidOperationException("Invalid Date");

                var milliseconds = (long)Math.Truncate(timestamp);
                if (milliseconds < DateMinUnixMilliseconds || milliseconds > DateMaxUnixMilliseconds)
                    throw new InvalidOperationException("Invalid Date");

                return TsValue.FromString(FormatDateUtcIso(milliseconds));
            },

            ["Date::getUTCFullYear"] = args =>
            {
                var timestamp = D(DateTimestamp(args[0]));
                if (!double.IsFinite(timestamp) ||
                    timestamp < DateMinUnixMilliseconds || timestamp > DateMaxUnixMilliseconds)
                    return TsValue.FromFloat64(double.NaN);

                return TsValue.FromFloat64(DateTimeOffset
                    .FromUnixTimeMilliseconds((long)timestamp)
                    .UtcDateTime.Year);
            },

            ["Date::getUTCMonth"] = args =>
            {
                var timestamp = D(DateTimestamp(args[0]));
                if (!double.IsFinite(timestamp) ||
                    timestamp < DateMinUnixMilliseconds || timestamp > DateMaxUnixMilliseconds)
                    return TsValue.FromFloat64(double.NaN);

                return TsValue.FromFloat64(DateTimeOffset
                    .FromUnixTimeMilliseconds((long)timestamp)
                    .UtcDateTime.Month - 1);
            },

            ["Date::getUTCDate"] = args =>
            {
                var timestamp = D(DateTimestamp(args[0]));
                if (!double.IsFinite(timestamp) ||
                    timestamp < DateMinUnixMilliseconds || timestamp > DateMaxUnixMilliseconds)
                    return TsValue.FromFloat64(double.NaN);

                return TsValue.FromFloat64(DateTimeOffset
                    .FromUnixTimeMilliseconds((long)timestamp)
                    .UtcDateTime.Day);
            },

            ["Date::getUTCDay"] = args =>
            {
                var timestamp = D(DateTimestamp(args[0]));
                if (!double.IsFinite(timestamp) ||
                    timestamp < DateMinUnixMilliseconds || timestamp > DateMaxUnixMilliseconds)
                    return TsValue.FromFloat64(double.NaN);

                return TsValue.FromFloat64((int)DateTimeOffset
                    .FromUnixTimeMilliseconds((long)timestamp)
                    .UtcDateTime.DayOfWeek);
            },

            ["Date::getUTCHours"] = args =>
            {
                var timestamp = D(DateTimestamp(args[0]));
                if (!double.IsFinite(timestamp) ||
                    timestamp < DateMinUnixMilliseconds || timestamp > DateMaxUnixMilliseconds)
                    return TsValue.FromFloat64(double.NaN);

                return TsValue.FromFloat64(DateTimeOffset
                    .FromUnixTimeMilliseconds((long)timestamp)
                    .UtcDateTime.Hour);
            },

            ["Date::getUTCMinutes"] = args =>
            {
                var timestamp = D(DateTimestamp(args[0]));
                if (!double.IsFinite(timestamp) ||
                    timestamp < DateMinUnixMilliseconds || timestamp > DateMaxUnixMilliseconds)
                    return TsValue.FromFloat64(double.NaN);

                return TsValue.FromFloat64(DateTimeOffset
                    .FromUnixTimeMilliseconds((long)timestamp)
                    .UtcDateTime.Minute);
            },

            ["Date::getUTCSeconds"] = args =>
            {
                var timestamp = D(DateTimestamp(args[0]));
                if (!double.IsFinite(timestamp) ||
                    timestamp < DateMinUnixMilliseconds || timestamp > DateMaxUnixMilliseconds)
                    return TsValue.FromFloat64(double.NaN);

                return TsValue.FromFloat64(DateTimeOffset
                    .FromUnixTimeMilliseconds((long)timestamp)
                    .UtcDateTime.Second);
            },

            ["Date::getUTCMilliseconds"] = args =>
            {
                var timestamp = D(DateTimestamp(args[0]));
                if (!double.IsFinite(timestamp) ||
                    timestamp < DateMinUnixMilliseconds || timestamp > DateMaxUnixMilliseconds)
                    return TsValue.FromFloat64(double.NaN);

                return TsValue.FromFloat64(DateTimeOffset
                    .FromUnixTimeMilliseconds((long)timestamp)
                    .UtcDateTime.Millisecond);
            },

            // Set instance members (receiver = args[0]).
            ["Set::add"] = args =>
            {
                var set = Set(args[0]);
                if (args.Length < 2)
                    throw new InvalidOperationException("Set.add requires a value argument");
                set.Add(args[1]);
                return args[0];
            },
            ["Set::has"] = args =>
            {
                var set = Set(args[0]);
                if (args.Length < 2)
                    throw new InvalidOperationException("Set.has requires a value argument");
                return Bool(set.Contains(args[1]));
            },
            ["Set::delete"] = args =>
            {
                var set = Set(args[0]);
                if (args.Length < 2)
                    throw new InvalidOperationException("Set.delete requires a value argument");
                return Bool(set.Remove(args[1]));
            },
            ["Set::clear"] = args =>
            {
                Set(args[0]).Clear();
                return TsValue.Void;
            },
            ["Set::values"] = args =>
            {
                var result = new TsArray();
                foreach (var value in Set(args[0]).Entries)
                    result.Add(value);
                return new TsArrayValue(result);
            },
            ["Set::keys"] = args =>
            {
                var result = new TsArray();
                foreach (var value in Set(args[0]).Entries)
                    result.Add(value);
                return new TsArrayValue(result);
            },
            ["Set::entries"] = args =>
            {
                var result = new TsArray();
                foreach (var value in Set(args[0]).Entries)
                    result.Add(CreateEntryPair(value, value));
                return new TsArrayValue(result);
            },

            // ── Global functions ──
            ["parseInt"] = ParseIntBuiltin,
            ["parseFloat"] = args => Num(ParseFloatPrefix(S(args[0]))),
            ["isNaN"] = args => Bool(args[0] is not TsNull
                && !(args[0] is TsStringValue text && TrimEcmaWhitespace(text.Value).Length == 0)
                && double.IsNaN(D(args[0]))),
            ["isFinite"] = args => Bool(args[0] is TsNull || (args[0] is TsStringValue text && TrimEcmaWhitespace(text.Value).Length == 0) || double.IsFinite(D(args[0]))),
            ["String"] = args => TsValue.FromString(args.Length > 0 ? S(args[0]) : ""),
            ["Boolean"] = args => Bool(args.Length > 0 && args[0] switch
            {
                TsBoolValue b => b.Value,
                TsNull => false,
                TsStringValue s => s.Value.Length > 0,
                TsArrayValue => true,
                TsObjectValue => true,
                TsMapValue or TsSetValue => true,
                _ => D(args[0]) != 0 && !double.IsNaN(D(args[0]))
            }),
            ["BigInt"] = args => args.Length > 0 ? ToBigInt(args[0]) : TsValue.FromBigInt(System.Numerics.BigInteger.Zero),
            ["RegExp::test"] = args =>
            {
                if (args.Length < 2 || args[0] is not TsRegexValue regex)
                    throw new InvalidOperationException("RegExp.test requires a RegExp receiver and input");
                return Bool(regex.Test(S(args[1])));
            },
            ["Generator::next"] = args =>
            {
                if (args.Length < 1 || args[0] is not TsGeneratorValue generator)
                    throw new InvalidOperationException("Generator.next requires a generator receiver");
                if (!generator.TryNextLegacy(out var value, out var done))
                    throw new InvalidOperationException("Generator.next must be executed by the interpreter");
                var result = new TsObject("Object");
                result.SetField("value", value);
                result.SetField("done", Bool(done));
                return new TsObjectValue(result);
            },

            // ── Array instance members (receiver = args[0]) ──
            ["Array::push"] = args =>
            {
                var arr = Arr(args[0]);
                for (int i = 1; i < args.Length; i++) arr.Add(args[i]);
                return TsValue.FromInt32(arr.Count);
            },
            ["Array::pop"] = args =>
            {
                var arr = Arr(args[0]);
                if (arr.Count == 0) return TsValue.Void;
                var last = arr.Get(arr.Count - 1);
                arr.RemoveLast();
                return last;
            },
            ["Array::shift"] = args =>
            {
                var arr = Arr(args[0]);
                if (arr.Count == 0) return TsValue.Void;
                var first = arr.Get(0);
                arr.RemoveAt(0);
                return first;
            },
            ["Array::unshift"] = args =>
            {
                var arr = Arr(args[0]);
                for (int i = args.Length - 1; i >= 1; i--) arr.Insert(0, args[i]);
                return TsValue.FromInt32(arr.Count);
            },
            ["Array::splice"] = args =>
            {
                var arr = Arr(args[0]);
                int length = arr.Count;
                int start = 0;
                if (args.Length > 1)
                {
                    start = args[1] is TsStringValue startText &&
                        TryGetBoundedHexPosition(startText.Value, length, out int hexStart)
                        ? hexStart : Normalize(I(args[1]), length);
                }

                int remaining = length - start;
                int deleteCount = args.Length > 1 ? remaining : 0;
                if (args.Length > 2)
                {
                    deleteCount = args[2] is TsStringValue countText &&
                        TryGetBoundedHexPosition(countText.Value, remaining, out int hexCount)
                        ? hexCount : Math.Clamp(I(args[2]), 0, remaining);
                }

                var removed = new TsArray(deleteCount);
                for (int i = 0; i < deleteCount; i++)
                {
                    if (arr.IsHole(start))
                        removed.SetHole(i);
                    else
                        removed.Add(arr.Get(start));
                    arr.RemoveAt(start);
                }

                for (int i = args.Length - 1; i >= 3; i--)
                    arr.Insert(start, args[i]);

                return new TsArrayValue(removed);
            },
            ["Array::reverse"] = args => { Arr(args[0]).Reverse(); return args[0]; },
            ["Array::toReversed"] = args =>
            {
                var source = Arr(args[0]);
                int count = source.Count;
                var copy = new TsArray(count);
                for (int i = count - 1; i >= 0; i--)
                    copy.Add(source.Get(i));
                return new TsArrayValue(copy);
            },
            ["Array::includes"] = args =>
            {
                var arr = Arr(args[0]);
                int length = arr.Count;
                if (length == 0) return Bool(false);

                int start = args.Length > 2 ? FillIndex(args[2], length) : 0;
                return Bool(IndexOf(arr, args.Length > 1 ? args[1] : TsValue.Void, sameValueZero: true, start: start) >= 0);
            },
            ["Array::indexOf"] = args =>
            {
                var arr = Arr(args[0]);
                int length = arr.Count;
                if (length == 0) return TsValue.FromInt32(-1);

                int start = args.Length > 2 ? FillIndex(args[2], length) : 0;
                return TsValue.FromInt32(IndexOf(arr, args.Length > 1 ? args[1] : TsValue.Void, start: start));
            },
            ["Array::lastIndexOf"] = args =>
            {
                var arr = Arr(args[0]);
                int length = arr.Count;
                if (length == 0) return TsValue.FromInt32(-1);

                int start = length - 1;
                if (args.Length > 2)
                {
                    double position = args[2] is TsStringValue text
                        && TryGetBoundedHexPosition(text.Value, length, out int hexPosition)
                        ? hexPosition
                        : D(args[2]);
                    double index = double.IsNaN(position) ? 0 : Math.Truncate(position);
                    if (index < -length) return TsValue.FromInt32(-1);
                    start = index < 0 ? (int)(length + index) : (int)Math.Min(index, start);
                }

                for (int i = start; i >= 0; i--)
                    if (!arr.IsHole(i) && Interpreter.StrictEquals(arr.Get(i), args.Length > 1 ? args[1] : TsValue.Void)) return TsValue.FromInt32(i);
                return TsValue.FromInt32(-1);
            },
            ["Array::join"] = args =>
            {
                var arr = Arr(args[0]);
                string sep = args.Length > 1 && args[1] is not TsVoid ? S(args[1]) : ",";
                return TsValue.FromString(string.Join(sep, Enumerable.Range(0, arr.Count).Select(i =>
                {
                    var element = arr.Get(i);
                    if (element is TsArrayValue nested && TryFormatFlatInner(nested.Value, out string inner))
                        return inner;
                    return element is TsNull or TsVoid ? string.Empty
                        : element is TsBoolValue ? S(element)
                        : (element is TsFloat32Value f32 && f32.Value == 0f)
                            || (element is TsFloat64Value f64 && f64.Value == 0d) ? "0"
                        : element.ToString();
                })));

                static bool TryFormatFlatInner(TsArray array, out string result)
                {
                    int count = array.Count;
                    for (int i = 0; i < count; i++)
                    {
                        if (array.Get(i) is not (TsInt32Value or TsStringValue or TsBoolValue or TsNull or TsVoid))
                        {
                            result = string.Empty;
                            return false;
                        }
                    }

                    var fields = new string[count];
                    for (int i = 0; i < count; i++)
                    {
                        fields[i] = array.Get(i) switch
                        {
                            TsInt32Value n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            TsStringValue text => text.Value,
                            TsBoolValue flag => flag.Value ? "true" : "false",
                            TsNull or TsVoid => string.Empty,
                            _ => throw new InvalidOperationException("Ineligible flat array field")
                        };
                    }

                    result = string.Join(",", fields);
                    return true;
                }
            },
            ["Array::slice"] = args =>
            {
                var arr = Arr(args[0]);
                int start = args.Length > 1 ? Normalize(I(args[1]), arr.Count) : 0;
                int end = args.Length > 2 && args[2] is not TsVoid ? Normalize(I(args[2]), arr.Count) : arr.Count;
                var result = new TsArray(Math.Max(end - start, 0));
                for (int i = start; i < end; i++)
                {
                    if (arr.IsHole(i)) result.SetHole(i - start);
                    else result.Add(arr.Get(i));
                }
                return new TsArrayValue(result);
            },
            ["Array::flat"] = args =>
            {
                var array = Arr(args[0]);
                double depth = args.Length == 1 ? 1 : args[1] switch
                {
                    TsVoid => 1,
                    TsNull => 0,
                    TsBoolValue b => b.Value ? 1d : 0d,
                    TsInt32Value i => i.Value,
                    TsInt64Value l => l.Value,
                    TsUInt64Value u => u.Value,
                    var n when (n is TsFloat32Value f32 && float.IsNaN(f32.Value)) ||
                               (n is TsFloat64Value f64 && double.IsNaN(f64.Value)) => 0,
                    TsFloat32Value f when float.IsFinite(f.Value) => Math.Truncate(f.Value),
                    TsFloat64Value d when double.IsFinite(d.Value) => Math.Truncate(d.Value),
                    TsStringValue s => FlatStringDepth(s),
                    TsDecimalValue m => (double)decimal.Truncate(m.Value),
                    _ => throw new InvalidOperationException("Array.flat depth must be a finite number or undefined")
                };
                depth = Math.Max(0, depth);

                var result = new TsArray();
                var frames = new Stack<(TsArray Array, int Length, int NextIndex, long Level)>();
                frames.Push((array, array.Count, 0, 0));
                while (frames.TryPop(out var frame))
                {
                    while (frame.NextIndex < frame.Length)
                    {
                        int index = frame.NextIndex++;
                        if (frame.Array.IsHole(index)) continue;
                        var value = frame.Array.Get(index);
                        if (frame.Level < depth && value is TsArrayValue nested)
                        {
                            // Save the parent at its next index, then visit the child in order.
                            frames.Push(frame);
                            var child = nested.Value;
                            frame = (child, child.Count, 0, frame.Level + 1);
                        }
                        else
                            result.Add(value);
                    }
                }
                return new TsArrayValue(result);
            },
            ["Array::concat"] = args =>
            {
                var result = new TsArray();
                foreach (var arg in args)
                {
                    if (arg is TsArrayValue av)
                    {
                        for (int i = 0; i < av.Value.Count; i++)
                        {
                            if (av.Value.IsHole(i)) result.SetHole(result.Count);
                            else result.Add(av.Value.Get(i));
                        }
                    }
                    else
                        result.Add(arg);
                }
                return new TsArrayValue(result);
            },
            ["Array::fill"] = args =>
            {
                var arr = Arr(args[0]);
                int length = arr.Count;
                static int Bound(TsValue bound, int count) =>
                    bound is TsStringValue text && TryGetBoundedHexPosition(text.Value, count, out int hexPosition)
                        ? hexPosition : FillIndex(bound, count);
                int start = args.Length > 2 ? Bound(args[2], length) : 0;
                int end = args.Length > 3 && args[3] is not TsVoid ? Bound(args[3], length) : length;
                var value = args.Length > 1 ? args[1] : TsValue.Void;
                for (int i = start; i < end; i++) arr.Set(i, value);
                return args[0];
            },

            // ── String instance members (receiver = args[0]) ──
            ["String::charAt"] = args =>
            {
                string s = S(args[0]);
                int i = args.Length > 1 ? I(args[1]) : 0;
                return TsValue.FromString(i >= 0 && i < s.Length ? s[i].ToString() : "");
            },
            ["String::charCodeAt"] = args =>
            {
                string s = S(args[0]);
                int i = args.Length > 1 ? I(args[1]) : 0;
                return i >= 0 && i < s.Length ? TsValue.FromInt32(s[i]) : TsValue.FromFloat64(double.NaN);
            },
            ["String::codePointAt"] = args =>
            {
                string s = S(args[0]);
                int i = args.Length > 1 ? I(args[1]) : 0;
                if (i < 0 || i >= s.Length)
                    return TsValue.Void;

                char unit = s[i];
                if (char.IsHighSurrogate(unit) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    return TsValue.FromInt32(char.ConvertToUtf32(unit, s[i + 1]));
                return TsValue.FromInt32(unit);
            },
            ["String::at"] = args =>
            {
                string s = S(args[0]);
                double index = args.Length <= 1 ? 0
                    : args[1] is TsStringValue text && TryGetBoundedHexPosition(text.Value, s.Length, out int hexPosition)
                        ? hexPosition : D(args[1]);
                if (double.IsNaN(index))
                    index = 0;
                if (!double.IsFinite(index))
                    return TsValue.Void;

                index = Math.Truncate(index);
                if (index < 0)
                    index += s.Length;

                return index >= 0 && index < s.Length
                    ? TsValue.FromString(s[(int)index].ToString())
                    : TsValue.Void;
            },
            ["String::toUpperCase"] = args => TsValue.FromString(StringUppercaseExpansions.Uppercase(S(args[0]))),
            ["String::toLowerCase"] = args => TsValue.FromString(Lowercase(S(args[0]))),
            ["String::trim"] = args => TsValue.FromString(TrimEcmaWhitespace(S(args[0]))),
            ["String::trimStart"] = args => TsValue.FromString(TrimStartEcmaWhitespace(S(args[0]))),
            ["String::trimEnd"] = args => TsValue.FromString(TrimEndEcmaWhitespace(S(args[0]))),
            ["String::substring"] = args =>
            {
                string s = S(args[0]);
                static int Bound(TsValue bound, int length) =>
                    bound is TsStringValue text && TryGetBoundedHexPosition(text.Value, length, out int hexPosition)
                        ? hexPosition : I(bound);
                int start = Math.Clamp(args.Length > 1 ? Bound(args[1], s.Length) : 0, 0, s.Length);
                int end = Math.Clamp(args.Length > 2 && args[2] is not TsVoid ? Bound(args[2], s.Length) : s.Length, 0, s.Length);
                if (start > end) (start, end) = (end, start);
                return TsValue.FromString(s[start..end]);
            },
            ["String::slice"] = args =>
            {
                string s = S(args[0]);
                static int Bound(TsValue bound, int length) =>
                    bound is TsStringValue text && TryGetBoundedHexPosition(text.Value, length, out int hexPosition)
                        ? hexPosition : Normalize(I(bound), length);
                int start = args.Length > 1 ? Bound(args[1], s.Length) : 0;
                int end = args.Length > 2 && args[2] is not TsVoid ? Bound(args[2], s.Length) : s.Length;
                return TsValue.FromString(start < end ? s[start..end] : "");
            },
            ["String::repeat"] = args =>
            {
                var text = S(args[0]);
                if (args.Length > 1 && IsNegativeRepeatCount(args[1]))
                {
                    var error = new TsObject("RangeError");
                    error.SetField("name", TsValue.FromString("RangeError"));
                    error.SetField("message", TsValue.FromString("String.repeat count must not be negative"));
                    throw new TsThrownException(TsValue.FromObject(error));
                }
                return TsValue.FromString(string.Concat(Enumerable.Repeat(text, Math.Max(args.Length > 1 ? I(args[1]) : 0, 0))));
            },
            ["String::concat"] = args => TsValue.FromString(string.Concat(args.Select(a => a switch
            {
                TsStringValue sv => sv.Value,
                TsBoolValue or TsVoid => S(a),
                TsFloat32Value f32 when f32.Value == 0f => "0",
                TsFloat64Value f64 when f64.Value == 0d => "0",
                _ => a.ToString()
            }))),
            ["String::includes"] = args =>
            {
                var text = S(args[0]);
                var needle = S(args.Length > 1 ? args[1] : TsValue.Void);
                double position = args.Length <= 2 ? 0.0
                    : args[2] is TsStringValue positionText
                        && TryGetBoundedHexPosition(positionText.Value, text.Length, out int hexPosition)
                        ? hexPosition
                        : D(args[2]);
                var start = double.IsNaN(position) || position <= 0
                    ? 0
                    : position >= text.Length ? text.Length : (int)Math.Truncate(position);
                return Bool(needle.Length == 0 || text.IndexOf(needle, start, StringComparison.Ordinal) >= 0);
            },
            ["String::startsWith"] = args =>
            {
                var text = S(args[0]);
                var needle = S(args.Length > 1 ? args[1] : TsValue.Void);
                double position = args.Length <= 2 ? 0.0
                    : args[2] is TsStringValue positionText
                        && TryGetBoundedHexPosition(positionText.Value, text.Length, out int hexPosition)
                        ? hexPosition
                        : D(args[2]);
                int start = double.IsNaN(position) || position <= 0 ? 0
                    : position >= text.Length ? text.Length : (int)Math.Truncate(position);
                return Bool(text.AsSpan(start).StartsWith(needle.AsSpan(), StringComparison.Ordinal));
            },
            ["String::endsWith"] = args =>
            {
                var text = S(args[0]);
                var needle = S(args.Length > 1 ? args[1] : TsValue.Void);
                int end = text.Length;
                if (args.Length > 2 && args[2] is not TsVoid)
                {
                    double position = args[2] is TsStringValue positionText
                        && TryGetBoundedHexPosition(positionText.Value, text.Length, out int hexPosition)
                        ? hexPosition
                        : StringEndsWithPosition(args[2]);
                    end = double.IsNaN(position) || position <= 0
                        ? 0
                        : position >= text.Length ? text.Length : (int)Math.Truncate(position);
                }
                return Bool(text.AsSpan(0, end).EndsWith(needle.AsSpan(), StringComparison.Ordinal));
            },
            ["String::indexOf"] = args =>
            {
                var text = S(args[0]);
                var needle = S(args.Length > 1 ? args[1] : TsValue.Void);
                double position = args.Length <= 2 ? 0.0 : args[2] is TsStringValue positionText && TryGetBoundedHexPosition(positionText.Value, text.Length, out int hexPosition) ? hexPosition : D(args[2]);
                var start = double.IsNaN(position) || position <= 0
                    ? 0
                    : position >= text.Length ? text.Length : (int)Math.Truncate(position);
                return TsValue.FromInt32(needle.Length == 0 ? start : text.IndexOf(needle, start, StringComparison.Ordinal));
            },
            ["String::lastIndexOf"] = args =>
            {
                var text = S(args[0]);
                var needle = S(args.Length > 1 ? args[1] : TsValue.Void);
                var maxStart = text.Length - needle.Length;
                if (maxStart < 0) return TsValue.FromInt32(-1);

                var position = args.Length < 3 ? double.NaN : args[2] is TsNull ? 0.0 : args[2] is TsStringValue positionText && TryGetBoundedHexPosition(positionText.Value, text.Length, out int hexPosition) ? hexPosition : D(args[2]);
                var start = double.IsNaN(position) || position >= maxStart
                    ? maxStart
                    : position <= 0 ? 0 : (int)Math.Truncate(position);
                if (needle.Length == 0) return TsValue.FromInt32(start);

                // Position limits the start of the match, not its final character.
                return TsValue.FromInt32(text.AsSpan(0, start + needle.Length).LastIndexOf(needle.AsSpan()));
            },
            ["String::replace"] = args =>
            {
                string s = S(args[0]);
                string find = S(args[1]);
                int idx = s.IndexOf(find, StringComparison.Ordinal);
                if (idx < 0) return TsValue.FromString(s);

                string template = S(args.Length > 2 ? args[2] : TsValue.Void);
                int tail = idx + find.Length;
                var prefix = s.AsSpan(0, idx);
                var suffix = s.AsSpan(tail);
                StringBuilder? result = null;
                int literalStart = 0;
                for (int i = 0; i < template.Length - 1; i++)
                {
                    if (template[i] != '$') continue;
                    char token = template[i + 1];
                    if (token is not ('$' or '&' or '`' or '\'')) continue;

                    // Scan only the template; inserted source text is never rescanned.
                    result ??= new StringBuilder().Append(prefix);
                    result.Append(template.AsSpan(literalStart, i - literalStart));
                    switch (token)
                    {
                        case '$': result.Append('$'); break;
                        case '&': result.Append(s.AsSpan(idx, find.Length)); break;
                        case '`': result.Append(prefix); break;
                        case '\'': result.Append(suffix); break;
                    }
                    literalStart = i + 2;
                    i++;
                }

                return TsValue.FromString(result is null
                    ? string.Concat(prefix, template.AsSpan(), suffix)
                    : result.Append(template.AsSpan(literalStart)).Append(suffix).ToString());
            },
            ["String::replaceAll"] = args =>
            {
                string s = S(args[0]);
                string find = S(args[1]);
                if (args.Length == 2 && args[0] is TsStringValue &&
                    args[1] is TsStringValue && find.Length != 0)
                    return TsValue.FromString(s.Replace(find, S(TsValue.Void)));
                bool emptyPrimitiveWithUndefinedReplacement = find.Length == 0 &&
                    args[0] is TsStringValue && args[1] is TsStringValue &&
                    (args.Length == 2 || args[2] is TsVoid);
                string template = emptyPrimitiveWithUndefinedReplacement ? S(TsValue.Void) : S(args[2]);
                if (!emptyPrimitiveWithUndefinedReplacement && (args[0] is not TsStringValue ||
                    args[1] is not (TsStringValue or TsBoolValue or TsInt32Value or TsFloat64Value) || args[2] is not TsStringValue))
                    return TsValue.FromString(s.Replace(find, template));

                static bool IsToken(char c) => c is '$' or '&' or '`' or '\'';

                static void ExpandTemplate(string s, string template, int match, int length, int tail,
                    int firstToken, StringBuilder result)
                {
                    int literalStart = 0;
                    // Only scan the template. Both context tokens refer to the original source.
                    for (int i = firstToken; i < template.Length - 1; i++)
                    {
                        if (template[i] != '$') continue;
                        char token = template[i + 1];
                        if (!IsToken(token)) continue;

                        result.Append(template.AsSpan(literalStart, i - literalStart));
                        switch (token)
                        {
                            case '$': result.Append('$'); break;
                            case '&': result.Append(s.AsSpan(match, length)); break;
                            case '`': result.Append(s.AsSpan(0, match)); break;
                            case '\'': result.Append(s.AsSpan(tail)); break;
                        }
                        literalStart = i + 2;
                        i++;
                    }
                    result.Append(template.AsSpan(literalStart));
                }

                int firstToken = 0;
                while (firstToken < template.Length - 1 &&
                    (template[firstToken] != '$' || !IsToken(template[firstToken + 1])))
                    firstToken++;
                bool hasToken = firstToken < template.Length - 1;
                if (find.Length == 0)
                {
                    if (template.Length == 0) return TsValue.FromString(s);

                    var inserted = new StringBuilder();
                    // Insert at every UTF-16 code-unit boundary, including both ends.
                    for (int boundary = 0; ; boundary++)
                    {
                        if (hasToken)
                            ExpandTemplate(s, template, boundary, 0, boundary, firstToken, inserted);
                        else
                            inserted.Append(template);

                        if (boundary == s.Length) break;
                        inserted.Append(s[boundary]);
                    }
                    return TsValue.FromString(inserted.ToString());
                }

                if (!hasToken)
                    return TsValue.FromString(s.Replace(find, template));

                int match = s.IndexOf(find, StringComparison.Ordinal);
                if (match < 0) return TsValue.FromString(s);

                var result = new StringBuilder();
                int consumed = 0;
                do
                {
                    result.Append(s.AsSpan(consumed, match - consumed));
                    int tail = match + find.Length;
                    ExpandTemplate(s, template, match, find.Length, tail, firstToken, result);
                    consumed = tail;
                    match = s.IndexOf(find, consumed, StringComparison.Ordinal);
                } while (match >= 0);

                return TsValue.FromString(result.Append(s.AsSpan(consumed)).ToString());
            },
            ["String::padStart"] = args =>
            {
                string text = S(args[0]);
                if (args.Length == 1) return TsValue.FromString(text);
                double numericWidth = D(args[1]);
                if (args[1] is TsInt32Value or TsInt64Value or TsUInt64Value or TsFloat32Value or TsFloat64Value or TsDecimalValue
                    && numericWidth < 0) return TsValue.FromString(text);
                int width = (int)numericWidth;
                if (width < 0) return TsValue.FromString(text.PadLeft(width));
                if (width <= text.Length) return TsValue.FromString(text);

                string fill = args.Length > 2 && args[2] is not TsVoid ? S(args[2]) : " ";
                if (fill.Length == 0) return TsValue.FromString(text);
                if (fill.Length == 1) return TsValue.FromString(text.PadLeft(width, fill[0]));

                return TsValue.FromString(string.Create(width, (text, fill), static (destination, state) =>
                {
                    var prefix = destination[..(destination.Length - state.text.Length)];
                    var pattern = state.fill.AsSpan();
                    for (int offset = 0; offset < prefix.Length;)
                    {
                        int count = Math.Min(pattern.Length, prefix.Length - offset);
                        pattern[..count].CopyTo(prefix[offset..]);
                        offset += count;
                    }
                    state.text.AsSpan().CopyTo(destination[prefix.Length..]);
                }));
            },
            ["String::padEnd"] = args =>
            {
                string text = S(args[0]);
                if (args.Length == 1) return TsValue.FromString(text);
                double numericWidth = D(args[1]);
                int width = (int)numericWidth;
                string fill = args.Length > 2 && args[2] is not TsVoid ? S(args[2]) : " ";
                if (args[1] is TsInt32Value or TsInt64Value or TsUInt64Value or TsFloat32Value or TsFloat64Value or TsDecimalValue
                    && numericWidth < 0) return TsValue.FromString(text);
                if (width < 0) return TsValue.FromString(text.PadRight(width));
                if (width <= text.Length || fill.Length == 0) return TsValue.FromString(text);
                if (fill.Length == 1) return TsValue.FromString(text.PadRight(width, fill[0]));

                return TsValue.FromString(string.Create(width, (text, fill), static (destination, state) =>
                {
                    state.text.AsSpan().CopyTo(destination);
                    var suffix = destination[state.text.Length..];
                    var pattern = state.fill.AsSpan();
                    for (int offset = 0; offset < suffix.Length;)
                    {
                        int count = Math.Min(pattern.Length, suffix.Length - offset);
                        pattern[..count].CopyTo(suffix[offset..]);
                        offset += count;
                    }
                }));
            },
            ["String::split"] = args =>
            {
                string s = S(args[0]);
                if (args.Length == 1 || args[1] is TsVoid)
                {
                    if (args.Length > 2 &&
                        ((args[2] is TsInt32Value or TsInt64Value or TsUInt64Value or
                            TsFloat32Value or TsFloat64Value or TsDecimalValue or TsBoolValue) ||
                            (args[2] is TsStringValue undefinedTextLimit && IsStringSplitDecimalTextLimit(undefinedTextLimit.Value))) &&
                        StringSplitLimit(D(args[2])) == 0)
                        return new TsArrayValue(new TsArray(0));

                    var whole = new TsArray(1);
                    whole.Add(TsValue.FromString(s));
                    return new TsArrayValue(whole);
                }

                string sep = S(args[1]);
                if ((args[1] is TsStringValue or TsInt32Value or TsInt64Value or TsUInt64Value or
                        TsFloat32Value or TsFloat64Value or TsDecimalValue or TsBoolValue) && args.Length > 2 &&
                    ((args[2] is TsInt32Value or TsInt64Value or TsUInt64Value or
                        TsFloat32Value or TsFloat64Value or TsDecimalValue or TsBoolValue) ||
                        (args[1] is TsStringValue && args[2] is TsStringValue textLimit &&
                            IsStringSplitDecimalTextLimit(textLimit.Value))))
                {
                    uint limit = StringSplitLimit(D(args[2]));
                    if (limit == 0)
                        return new TsArrayValue(new TsArray(0));

                    long maxParts = sep.Length == 0 ? s.Length : (long)s.Length / sep.Length + 1;
                    if (limit < maxParts)
                    {
                        if (sep.Length == 0)
                        {
                            // Below s.Length, so the limit fits in an int.
                            int count = (int)limit;
                            var units = new TsArray(count);
                            for (int i = 0; i < count; i++)
                                units.Add(TsValue.FromString(s[i].ToString()));
                            return new TsArrayValue(units);
                        }

                        var limited = new TsArray((int)Math.Min(4u, limit));
                        int start = 0;
                        while (true)
                        {
                            int match = s.IndexOf(sep, start, StringComparison.Ordinal);
                            if (match < 0)
                            {
                                limited.Add(TsValue.FromString(s.Substring(start)));
                                break;
                            }

                            limited.Add(TsValue.FromString(s.Substring(start, match - start)));
                            if ((long)limited.Count == limit)
                                break;
                            start = match + sep.Length;
                        }
                        return new TsArrayValue(limited);
                    }
                }

                var parts = sep.Length == 0
                    ? s.Select(ch => ch.ToString()).ToArray()
                    : s.Split(sep);
                var result = new TsArray(parts.Length);
                foreach (var part in parts) result.Add(TsValue.FromString(part));
                return new TsArrayValue(result);
            },
        };
        return t;
    }

    // Accept only whole fixed-point ASCII decimal text for this split limit.
    private static bool IsStringSplitDecimalTextLimit(string text)
    {
        int index = 0;
        if (index < text.Length && text[index] is '+' or '-')
            index++;

        int start = index;
        while (index < text.Length && text[index] is >= '0' and <= '9')
            index++;
        if (index == start)
            return false;
        if (index == text.Length)
            return true;
        if (text[index++] != '.')
            return false;

        start = index;
        while (index < text.Length && text[index] is >= '0' and <= '9')
            index++;
        return index > start && index == text.Length;
    }

    private static uint StringSplitLimit(double value)
    {
        if (!double.IsFinite(value))
            return 0;

        const double modulus = 4294967296d;
        double remainder = Math.Truncate(value) % modulus;
        return (uint)(remainder < 0 ? remainder + modulus : remainder);
    }

    private static int CountLeadingZeroBits32(double value) =>
        System.Numerics.BitOperations.LeadingZeroCount(ToUInt32(value));

    private static uint ToUInt32(double value)
    {
        if (!double.IsFinite(value) || value == 0d)
            return 0u;

        const double modulus = 4294967296d;
        double remainder = Math.Truncate(value) % modulus;
        if (remainder < 0d)
            remainder += modulus;
        return (uint)remainder;
    }

    private static double RoundJsNumber(double value)
    {
        if (!double.IsFinite(value) || value == 0.0)
            return value;

        double truncated = Math.Truncate(value);
        if (truncated == value)
            return value;

        // Compare the fraction directly, without rounding value + 0.5.
        double fraction = value - truncated;
        if (fraction >= 0.5)
            return truncated + 1.0;
        if (fraction < -0.5)
            return truncated - 1.0;

        return truncated == 0.0 ? Math.CopySign(0.0, value) : truncated;
    }

    private static TsValue Min(TsValue[] args)
    {
        // Keep the legacy coercion path for the whole call unless every
        // argument is native numeric, even when an earlier argument is NaN.
        foreach (var arg in args)
        {
            if (arg is not (TsInt32Value or TsInt64Value or TsUInt64Value or
                TsFloat32Value or TsFloat64Value or TsDecimalValue))
                return Num(args.Select(D).Min());
        }

        double best = double.PositiveInfinity;
        foreach (var arg in args)
        {
            double value = D(arg);
            if (double.IsNaN(value))
                return Num(value);

            if (value < best || (value == 0.0 && best == 0.0 &&
                BitConverter.DoubleToInt64Bits(value) < 0 &&
                BitConverter.DoubleToInt64Bits(best) == 0))
                best = value;
        }

        return Num(best);
    }

    private static TsValue Max(TsValue[] args)
    {
        // Keep the legacy coercion path for the whole call unless every
        // argument is native numeric, even when an earlier argument is NaN.
        foreach (var arg in args)
        {
            if (arg is not (TsInt32Value or TsInt64Value or TsUInt64Value or
                TsFloat32Value or TsFloat64Value or TsDecimalValue))
                return Num(args.Select(D).Max());
        }

        double best = double.NegativeInfinity;
        foreach (var arg in args)
        {
            double value = D(arg);
            if (double.IsNaN(value))
                return Num(value);

            if (value > best || (value == 0.0 && best == 0.0 &&
                BitConverter.DoubleToInt64Bits(value) == 0 &&
                BitConverter.DoubleToInt64Bits(best) < 0))
                best = value;
        }

        return Num(best);
    }

    private static TsValue Hypot(TsValue[] args)
    {
        double max = 0.0;
        bool hasInfinity = false;
        bool hasNaN = false;
        foreach (var arg in args)
        {
            // Preserve the original coercion path for the entire call if any
            // argument is neither a native numeric value nor undefined.
            if (arg is not (TsInt32Value or TsInt64Value or TsUInt64Value or
                TsFloat32Value or TsFloat64Value or TsDecimalValue or TsVoid))
                return Num(Math.Sqrt(args.Select(D).Sum(v => v * v)));

            double magnitude = Math.Abs(D(arg));
            if (double.IsInfinity(magnitude))
                hasInfinity = true;
            else if (double.IsNaN(magnitude))
                hasNaN = true;
            else if (magnitude > max)
                max = magnitude;
        }

        if (hasInfinity)
            return Num(double.PositiveInfinity);
        if (hasNaN)
            return Num(double.NaN);
        if (max == 0.0)
            return Num(0.0);

        double sum = 0.0;
        double compensation = 0.0;
        foreach (var arg in args)
        {
            // Divide directly, including when the scale is subnormal.
            double scaled = Math.Abs(D(arg)) / max;
            // Kahan summation retains small contributions to the norm.
            double term = scaled * scaled - compensation;
            double next = sum + term;
            compensation = (next - sum) - term;
            sum = next;
        }

        return Num(max * Math.Sqrt(sum));
    }

    private static double D(TsValue v) => v switch
    {
        TsInt32Value i => i.Value,
        TsInt64Value l => l.Value,
        TsUInt64Value u => u.Value,
        TsBigIntValue b => (double)b.Value,
        TsFloat32Value f => f.Value,
        TsFloat64Value d => d.Value,
        TsDecimalValue m => (double)m.Value,
        TsBoolValue b => b.Value ? 1 : 0,
        TsStringValue s when double.TryParse(
            s.Value.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) => parsed,
        _ => double.NaN
    };

    private static double FlatStringDepth(TsStringValue value)
    {
        double depth = D(value);
        if (double.IsNaN(depth)) return 0;
        if (!double.IsFinite(depth))
            throw new InvalidOperationException("Array.flat depth must be a finite number or undefined");
        return Math.Truncate(depth);
    }

    private static bool IsNegativeRepeatCount(TsValue count) => count switch
    {
        TsInt32Value i => i.Value < 0,
        TsFloat32Value f => f.Value <= -1f,
        TsFloat64Value f => f.Value <= -1d,
        TsDecimalValue d => decimal.Truncate(d.Value) is >= int.MinValue and <= -1m,
        TsStringValue s => Math.Truncate(D(s)) is >= int.MinValue and <= -1d,
        _ => false
    };

    private static int I(TsValue v) => (int)D(v);

    private static TsValue ToBigInt(TsValue v) => v switch
    {
        TsBigIntValue => v,
        TsInt64Value l => TsValue.FromBigInt(new System.Numerics.BigInteger(l.Value)),
        TsUInt64Value u => TsValue.FromBigInt(new System.Numerics.BigInteger(u.Value)),
        TsInt32Value i => TsValue.FromBigInt(new System.Numerics.BigInteger(i.Value)),
        TsStringValue s when System.Numerics.BigInteger.TryParse(
            s.Value.Trim(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) => TsValue.FromBigInt(parsed),
        TsBoolValue b => TsValue.FromBigInt(b.Value ? System.Numerics.BigInteger.One : System.Numerics.BigInteger.Zero),
        _ => throw new InvalidOperationException("Cannot convert value to BigInt")
    };

    private static double StringEndsWithPosition(TsValue value)
    {
        if (value is TsStringValue text && text.Value.Contains('\u0085'))
        {
            var trimmed = TrimEcmaWhitespace(text.Value);
            // The shared conversion uses .NET Trim, which would discard boundary NEL.
            if (trimmed.Length > 0 && (trimmed[0] == '\u0085' || trimmed[^1] == '\u0085'))
                return double.NaN;
        }

        return D(value);
    }

    // ECMAScript WhiteSpace and LineTerminator; NEL is not included.
    private static bool IsEcmaTrimWhitespace(char ch) =>
        ch is '\t' or '\v' or '\f' or '\uFEFF' or '\r' or '\n' or '\u2028' or '\u2029' ||
        char.GetUnicodeCategory(ch) == UnicodeCategory.SpaceSeparator;

    private static bool TryGetBoundedHexPosition(string text, int length, out int position)
    {
        text = TrimEcmaWhitespace(text);
        position = 0;
        if (text.Length < 3 || text[0] != '0' || text[1] is not ('x' or 'X'))
            return false;

        for (int i = 2; i < text.Length; i++)
        {
            char c = text[i];
            int digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1
            };
            if (digit < 0)
                return false;

            // The search already clamps positions at or above length; still validate every digit.
            position = (int)Math.Min(length, (long)position * 16 + digit);
        }

        return true;
    }

    private static string Lowercase(string text)
    {
        const char capitalSigma = '\u03A3';
        char[]? rewritten = null;
        for (int i = text.IndexOf(capitalSigma); i >= 0 && i < text.Length;
             i = text.IndexOf(capitalSigma, i + 1))
        {
            // A bounded Final_Sigma proof, not full Unicode context detection.
            // Only acute is known ignorable here; unsupported units cannot prove a match.
            int left = i - 1;
            while (left >= 0 && text[left] == '\u0301') left--;
            if (left < 0 || text[left] is not (>= '\u0391' and <= '\u03A1' or
                >= '\u03A3' and <= '\u03A9' or >= '\u03B1' and <= '\u03C9'))
                continue;

            int right = i + 1;
            while (right < text.Length && text[right] == '\u0301') right++;
            if (right < text.Length && text[right] != ' ') continue;

            // Both scans use the original text, including adjacent capital sigmas.
            rewritten ??= text.ToCharArray();
            rewritten[i] = '\u03C2';
        }

        if (rewritten is not null) text = new string(rewritten);
        return text.Replace("\u0130", "\u0069\u0307", StringComparison.Ordinal).ToLowerInvariant();
    }

    private static string TrimEcmaWhitespace(string text)
    {
        int start = 0;
        int end = text.Length;
        while (start < end && IsEcmaTrimWhitespace(text[start]))
            start++;
        while (end > start && IsEcmaTrimWhitespace(text[end - 1]))
            end--;
        return start == 0 && end == text.Length ? text : text[start..end];
    }

    private static string TrimStartEcmaWhitespace(string text)
    {
        int start = 0;
        while (start < text.Length && IsEcmaTrimWhitespace(text[start]))
            start++;
        return start == 0 ? text : text[start..];
    }

    private static string TrimEndEcmaWhitespace(string text)
    {
        int end = text.Length;
        while (end > 0 && IsEcmaTrimWhitespace(text[end - 1]))
            end--;
        return end == text.Length ? text : text[..end];
    }

    private static TsValue ParseIntBuiltin(TsValue[] args)
    {
        if ((args.Length == 1 || (args.Length == 2 && args[1] is TsVoid)) &&
            args[0] is TsStringValue primitiveString)
            return Num(ParseIntExplicitRadix(primitiveString.Value, 0));

        string input = S(args[0]);
        if (args.Length > 1 && TryGetExplicitParseIntRadix(args[1], out int explicitRadix))
            return Num(ParseIntExplicitRadix(input, explicitRadix));

        string text = input.Trim();
        int radix = args.Length > 1 ? I(args[1]) : 10;
        try
        {
            return Num(Convert.ToInt64(text, radix));
        }
        catch
        {
            return Num(long.TryParse(text, out var direct) ? direct : double.NaN);
        }
    }

    private static bool TryGetExplicitParseIntRadix(TsValue value, out int radix)
    {
        radix = 0;
        // Check decimal integrality before conversion could round a fraction to an integer.
        if (value is TsDecimalValue decimalValue)
        {
            decimal number = decimalValue.Value;
            if (number is < int.MinValue or > int.MaxValue or 0 || decimal.Truncate(number) != number)
                return false;
            radix = (int)number;
            return true;
        }

        if (value is not (TsInt32Value or TsFloat32Value or TsFloat64Value))
            return false;

        double numeric = D(value);
        if (!double.IsFinite(numeric) || numeric is < int.MinValue or > int.MaxValue or 0 ||
            Math.Truncate(numeric) != numeric)
            return false;
        radix = (int)numeric;
        return true;
    }

    private static double ParseIntExplicitRadix(ReadOnlySpan<char> text, int radix)
    {
        if (radix != 0 && (radix is < 2 or > 36))
            return double.NaN;

        int index = 0;
        while (index < text.Length && IsEcmaTrimWhitespace(text[index]))
            index++;
        bool negative = index < text.Length && text[index] == '-';
        if (index < text.Length && text[index] is '+' or '-')
            index++;
        if ((radix is 0 or 16) && text.Length - index >= 2 &&
            text[index] == '0' && text[index + 1] is 'x' or 'X')
        {
            radix = 16;
            index += 2;
        }
        if (radix == 0)
            radix = 10;

        int start = index;
        System.Numerics.BigInteger magnitude = System.Numerics.BigInteger.Zero;
        for (; index < text.Length; index++)
        {
            char ch = text[index];
            int digit = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'A' and <= 'Z' => ch - 'A' + 10,
                >= 'a' and <= 'z' => ch - 'a' + 10,
                _ => radix
            };
            if (digit >= radix)
                break;
            magnitude = magnitude * radix + digit;
            // At 2^1024 or above, every extension of the valid prefix also overflows.
            if (magnitude.GetBitLength() > 1024)
                return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }

        if (index == start)
            return double.NaN;
        // Parse the exact magnitude to get binary64 nearest-even rounding.
        double rounded = double.Parse(magnitude.ToString(CultureInfo.InvariantCulture),
            NumberStyles.None, CultureInfo.InvariantCulture);
        return negative ? -rounded : rounded;
    }

    private static double ParseFloatPrefix(ReadOnlySpan<char> text)
    {
        static bool IsDigit(char ch) => ch is >= '0' and <= '9';

        int index = 0;
        while (index < text.Length && IsEcmaTrimWhitespace(text[index]))
            index++;

        int start = index;
        bool negative = index < text.Length && text[index] == '-';
        if (index < text.Length && text[index] is '+' or '-')
            index++;

        if (text[index..].StartsWith("Infinity", StringComparison.Ordinal))
            return negative ? double.NegativeInfinity : double.PositiveInfinity;

        int integerStart = index;
        while (index < text.Length && IsDigit(text[index]))
            index++;
        bool hasDigits = index > integerStart;
        if (index < text.Length && text[index] == '.')
        {
            int fractionStart = ++index;
            while (index < text.Length && IsDigit(text[index]))
                index++;
            hasDigits |= index > fractionStart;
        }

        if (!hasDigits)
            return double.NaN;

        int end = index;
        if (index < text.Length && text[index] is 'e' or 'E')
        {
            index++;
            if (index < text.Length && text[index] is '+' or '-')
                index++;
            int exponentStart = index;
            while (index < text.Length && IsDigit(text[index]))
                index++;
            // An incomplete exponent leaves the mantissa as the longest valid prefix.
            if (index > exponentStart)
                end = index;
        }

        if (!double.TryParse(text[start..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return double.NaN;
        return negative && value == 0d ? Math.CopySign(0d, -1d) : value;
    }

    private static string S(TsValue v) => v switch
    {
        TsStringValue s => s.Value,
        TsBoolValue b => b.Value ? "true" : "false",
        TsNull => "null",
        TsVoid => "undefined",
        TsBigIntValue b => b.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString() ?? ""
    };

    private static TsArray Arr(TsValue v) => v is TsArrayValue a
        ? a.Value
        : throw new InvalidOperationException($"Receiver is not an array, got {DescribeValue(v)}");

    private static TsUint8ArrayValue Bytes(TsValue v) => v is TsUint8ArrayValue b
        ? b
        : throw new InvalidOperationException("Receiver is not a Uint8Array");

    private static TsMap Map(TsValue v) => v is TsMapValue map
        ? map.Value
        : throw new InvalidOperationException("Receiver is not a Map");

    private static TsSet Set(TsValue v) => v is TsSetValue set
        ? set.Value
        : throw new InvalidOperationException("Receiver is not a Set");

    private const long DateMinUnixMilliseconds = -62135596800000;
    private const long DateMaxUnixMilliseconds = 253402300799999;

    private static string FormatDateUtcIso(long milliseconds) => DateTimeOffset
        .FromUnixTimeMilliseconds(milliseconds)
        .UtcDateTime
        .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static TsValue DateTimestamp(TsValue v)
    {
        if (v is TsObjectValue obj && obj.Value.GetField("__timestampMs") is TsFloat64Value timestamp)
            return timestamp;
        throw new InvalidOperationException("Receiver is not a Date");
    }

    private static TsArrayValue CreateEntryPair(TsValue key, TsValue value)
    {
        var pair = new TsArray(2);
        pair.Add(key);
        pair.Add(value);
        return new TsArrayValue(pair);
    }

    private static TsValue Num(double value) => TsValue.FromFloat64(value);

    private static TsValue Bool(bool value) => new TsBoolValue(value);

    private static int FillIndex(TsValue value, int length)
    {
        double number = D(value);
        if (double.IsNaN(number)) return 0;
        double index = Math.Truncate(number);
        if (index < 0)
            return index <= -length ? 0 : (int)(length + index);
        return index >= length ? length : (int)index;
    }

    private static int Normalize(int index, int length)
    {
        if (index < 0) index += length;
        return Math.Clamp(index, 0, length);
    }

    private static int IndexOf(TsArray arr, TsValue value, bool sameValueZero = false, int start = 0)
    {
        for (int i = start; i < arr.Count; i++)
        {
            // includes reads holes as Void; indexOf searches only present slots.
            if (!sameValueZero && arr.IsHole(i)) continue;
            var item = arr.Get(i);
            if (sameValueZero ? SameValueZero(item, value) : Interpreter.StrictEquals(item, value))
                return i;
        }
        return -1;
    }

    private static bool SameValueZero(TsValue left, TsValue right) =>
        Interpreter.StrictEquals(left, right) || (IsNumericNaN(left) && IsNumericNaN(right));

    private static bool IsNumericNaN(TsValue value) => value switch
    {
        TsFloat32Value number => float.IsNaN(number.Value),
        TsFloat64Value number => double.IsNaN(number.Value),
        _ => false,
    };

    private static object? ToJsonCompatible(TsValue value, HashSet<object> ancestors, int depth) => value switch
    {
        TsVoid => null,
        TsNull => null,
        TsBoolValue boolean => boolean.Value,
        TsInt32Value number => number.Value,
        TsInt64Value number => number.Value,
        TsUInt64Value number => number.Value,
        TsBigIntValue number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsFloat32Value number => float.IsFinite(number.Value) ? (object)(number.Value == 0f ? 0f : number.Value) : null,
        TsFloat64Value number => double.IsFinite(number.Value) ? (object)(number.Value == 0d ? 0d : number.Value) : null,
        TsDecimalValue number => number.Value,
        TsStringValue text => text.Value,
        TsArrayValue array => ConvertComposite(array.Value, ancestors, depth, () =>
            Enumerable.Range(0, array.Value.Count)
                .Select(index => ToJsonCompatible(array.Value.Get(index), ancestors, depth + 1))
                .ToList()),
        TsUint8ArrayValue bytes => ConvertJsonUint8Array(bytes),
        TsMapValue map => ConvertComposite(map.Value, ancestors, depth, () =>
            map.Value.Entries.ToDictionary(
                entry => S(entry.Key),
                entry => ToJsonCompatible(entry.Value, ancestors, depth + 1),
                StringComparer.Ordinal)),
        TsSetValue set => ConvertComposite(set.Value, ancestors, depth, () =>
            set.Value.Entries
                .Select(v => ToJsonCompatible(v, ancestors, depth + 1))
                .ToList()),
        TsObjectValue date when string.Equals(date.Value.TypeName, "Date", StringComparison.Ordinal)
            && date.Value.GetField("__timestampMs") is TsFloat64Value timestamp
            && double.IsFinite(timestamp.Value)
            && timestamp.Value >= DateMinUnixMilliseconds && timestamp.Value <= DateMaxUnixMilliseconds
            => FormatDateUtcIso((long)Math.Truncate(timestamp.Value)),
        TsObjectValue obj => ConvertComposite(obj.Value, ancestors, depth, () =>
        {
            var members = obj.Value.Fields.Where(pair => pair.Value is not TsVoid).ToDictionary(
                pair => pair.Key,
                pair => ToJsonCompatible(pair.Value, ancestors, depth + 1),
                StringComparer.Ordinal);
            return string.Equals(obj.Value.TypeName, "Object", StringComparison.Ordinal)
                ? OrderJsonObjectMembers(members)
                : members;
        }),
        _ => value.ToString()
    };

    private static OrderedDictionary ConvertJsonUint8Array(TsUint8ArrayValue bytes)
    {
        var members = new OrderedDictionary(bytes.Length, StringComparer.Ordinal);
        for (var index = 0; index < bytes.Length; index++)
            members.Add(index.ToString(CultureInfo.InvariantCulture), bytes.Get(index));
        return members;
    }

    private static object OrderJsonObjectMembers(Dictionary<string, object?> members)
    {
        if (members.Count < 2)
            return members;

        var indices = new List<(uint Index, KeyValuePair<string, object?> Member)>();
        var names = new List<KeyValuePair<string, object?>>();
        var needsReordering = false;
        uint previousIndex = 0;
        foreach (var member in members)
        {
            if (uint.TryParse(member.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index != uint.MaxValue
                && string.Equals(member.Key, index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                needsReordering |= names.Count != 0 || index < previousIndex;
                indices.Add((index, member));
                previousIndex = index;
            }
            else
            {
                names.Add(member);
            }
        }

        if (!needsReordering)
            return members;

        // Preserve converted values and use explicit insertion order for serialization.
        indices.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        var ordered = new OrderedDictionary(members.Count, StringComparer.Ordinal);
        foreach (var entry in indices)
            ordered.Add(entry.Member.Key, entry.Member.Value);
        foreach (var member in names)
            ordered.Add(member.Key, member.Value);
        return ordered;
    }

    // Guarda compartida de las cuatro ramas compuestas de ToJsonCompatible:
    // primero ciclo (el contenedor ya está entre los ancestros), luego
    // profundidad (depth >= 1000). El contenedor se quita en finally para
    // que un mismo objeto referenciado dos veces sin ciclo (diamante) se
    // serialice dos veces.
    private static T ConvertComposite<T>(object container, HashSet<object> ancestors, int depth, Func<T> convert)
    {
        if (ancestors.Contains(container))
            throw new InvalidOperationException("Converting circular structure to JSON");
        if (depth >= 1000)
            throw new InvalidOperationException("Maximum JSON serialization depth exceeded");
        ancestors.Add(container);
        try
        {
            return convert();
        }
        finally
        {
            ancestors.Remove(container);
        }
    }

    private static byte ToByte(TsValue value) => value switch
    {
        TsInt32Value v => unchecked((byte)v.Value),
        TsInt64Value v => unchecked((byte)v.Value),
        TsUInt64Value v => unchecked((byte)v.Value),
        TsBigIntValue v => unchecked((byte)v.Value),
        TsFloat32Value v => unchecked((byte)v.Value),
        TsFloat64Value v => unchecked((byte)v.Value),
        TsDecimalValue v => unchecked((byte)v.Value),
        TsBoolValue v => v.Value ? (byte)1 : (byte)0,
        _ => 0
    };

    private static string DescribeValue(TsValue value) => value switch
    {
        TsObjectValue obj => $"object:{obj.Value.TypeName}",
        TsArrayValue => "Array",
        TsMapValue => "Map",
        TsSetValue => "Set",
        TsUint8ArrayValue => "Uint8Array",
        TsFunctionValue fn => $"function:{fn.FunctionName}",
        TsStringValue => "string",
        TsBoolValue => "boolean",
        TsInt32Value => "number:int32",
        TsInt64Value => "bigint:int64",
        TsUInt64Value => "bigint:uint64",
        TsBigIntValue => "bigint",
        TsNull => "null",
        TsVoid => "void",
        _ => value.GetType().Name
    };
}
