using System.Globalization;
using System.Text.Json;
using TypeSharp.VM.Memory;

namespace TypeSharp.VM.Interpreter;

// Native implementations of the JS-style globals the binder exposes
// (Math.*, Number.*, console.*) and of array/string instance members.
// Instance members receive the receiver as args[0].
public static class Builtins
{
    private static readonly Dictionary<string, Func<TsValue[], TsValue>> Table = Build();

    public static bool TryGet(string name, out Func<TsValue[], TsValue> builtin) =>
        Table.TryGetValue(name, out builtin!);

    private static Dictionary<string, Func<TsValue[], TsValue>> Build()
    {
        var t = new Dictionary<string, Func<TsValue[], TsValue>>(StringComparer.Ordinal)
        {
            // ── Math ──
            ["Math::abs"] = args => Num(Math.Abs(D(args[0]))),
            ["Math::floor"] = args => Num(Math.Floor(D(args[0]))),
            ["Math::ceil"] = args => Num(Math.Ceiling(D(args[0]))),
            ["Math::round"] = args => Num(Math.Round(D(args[0]), MidpointRounding.AwayFromZero)),
            ["Math::trunc"] = args => Num(Math.Truncate(D(args[0]))),
            ["Math::sqrt"] = args => Num(Math.Sqrt(D(args[0]))),
            ["Math::cbrt"] = args => Num(Math.Cbrt(D(args[0]))),
            ["Math::pow"] = args => Num(Math.Pow(D(args[0]), D(args[1]))),
            ["Math::log"] = args => Num(Math.Log(D(args[0]))),
            ["Math::log2"] = args => Num(Math.Log2(D(args[0]))),
            ["Math::log10"] = args => Num(Math.Log10(D(args[0]))),
            ["Math::exp"] = args => Num(Math.Exp(D(args[0]))),
            ["Math::sign"] = args => Num(Math.Sign(D(args[0]))),
            ["Math::random"] = _ => Num(Random.Shared.NextDouble()),
            ["Math::min"] = args => Num(args.Select(D).Min()),
            ["Math::max"] = args => Num(args.Select(D).Max()),
            ["Math::hypot"] = args => Num(Math.Sqrt(args.Select(D).Sum(v => v * v))),

            // ── Number ──
            ["Number::isInteger"] = args => Bool(args[0] switch
            {
                TsInt32Value => true,
                TsFloat32Value f => float.IsFinite(f.Value) && f.Value == MathF.Truncate(f.Value),
                TsFloat64Value d => double.IsFinite(d.Value) && d.Value == Math.Truncate(d.Value),
                _ => false
            }),
            ["Number::isFinite"] = args => Bool(args[0] is TsInt32Value ||
                (args[0] is TsFloat64Value fd && double.IsFinite(fd.Value)) ||
                (args[0] is TsFloat32Value ff && float.IsFinite(ff.Value))),
            ["Number::isNaN"] = args => Bool(
                (args[0] is TsFloat64Value fd && double.IsNaN(fd.Value)) ||
                (args[0] is TsFloat32Value ff && float.IsNaN(ff.Value))),
            ["Number::parseFloat"] = args => Num(double.TryParse(S(args[0]),
                System.Globalization.CultureInfo.InvariantCulture, out var pf) ? pf : double.NaN),
            ["Number::parseInt"] = args => Num(long.TryParse(S(args[0]), out var pi) ? pi : double.NaN),
            ["Number"] = args => Num(args.Length == 0 ? 0 : D(args[0])),

            // ── console ──
            ["console::log"] = args => { Console.WriteLine(string.Join(" ", args.Select(a => a.ToString()))); return TsValue.Null; },
            ["console::error"] = args => { Console.Error.WriteLine(string.Join(" ", args.Select(a => a.ToString()))); return TsValue.Null; },
            ["console::warn"] = args => { Console.Error.WriteLine(string.Join(" ", args.Select(a => a.ToString()))); return TsValue.Null; },

            ["JSON::stringify"] = args => TsValue.FromString(args.Length == 0
                ? "undefined"
                : JsonSerializer.Serialize(ToJsonCompatible(args[0]))),

            // ── Array construction + statics ──
            ["Array::ctor"] = args =>
            {
                // new Array(n) → n empty slots; new Array(a, b, …) → elements.
                if (args.Length == 1 && args[0] is TsArrayValue source)
                {
                    var cloned = new TsArray(source.Value.Count);
                    for (int i = 0; i < source.Value.Count; i++)
                        cloned.Add(source.Value.Get(i));
                    return new TsArrayValue(cloned);
                }

                if (args.Length == 1 && args[0] is TsInt32Value or TsInt64Value or TsFloat64Value)
                {
                    int size = I(args[0]);
                    var sized = new TsArray(Math.Max(size, 4));
                    for (int i = 0; i < size; i++) sized.Add(TsValue.Null);
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
                int end = args.Length > 2 ? Normalize(I(args[2]), bytes.Length) : bytes.Length;
                return bytes.Slice(start, end);
            },
            ["Uint8Array::subarray"] = args =>
            {
                var bytes = Bytes(args[0]);
                int start = args.Length > 1 ? Normalize(I(args[1]), bytes.Length) : 0;
                int end = args.Length > 2 ? Normalize(I(args[2]), bytes.Length) : bytes.Length;
                return bytes.Slice(start, end);
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
                }
                else if (args[1] is TsArrayValue sourceArray)
                {
                    for (int i = 0; i < sourceArray.Value.Count && offset + i < target.Length; i++)
                        target.Value[offset + i] = ToByte(sourceArray.Value.Get(i));
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
                return TsValue.Null;
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

                return TsValue.FromString(DateTimeOffset
                    .FromUnixTimeMilliseconds(milliseconds)
                    .UtcDateTime
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
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
                return TsValue.Null;
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
            ["parseInt"] = args =>
            {
                string text = S(args[0]).Trim();
                int radix = args.Length > 1 ? I(args[1]) : 10;
                try
                {
                    return Num(Convert.ToInt64(text, radix));
                }
                catch
                {
                    return Num(long.TryParse(text, out var direct) ? direct : double.NaN);
                }
            },
            ["parseFloat"] = args => Num(double.TryParse(S(args[0]).Trim(),
                System.Globalization.CultureInfo.InvariantCulture, out var pf) ? pf : double.NaN),
            ["isNaN"] = args => Bool(double.IsNaN(D(args[0]))),
            ["isFinite"] = args => Bool(double.IsFinite(D(args[0]))),
            ["String"] = args => TsValue.FromString(args.Length > 0 ? S(args[0]) : ""),
            ["Boolean"] = args => Bool(args.Length > 0 && args[0] switch
            {
                TsBoolValue b => b.Value,
                TsNull => false,
                TsStringValue s => s.Value.Length > 0,
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
                if (arr.Count == 0) return TsValue.Null;
                var last = arr.Get(arr.Count - 1);
                arr.RemoveLast();
                return last;
            },
            ["Array::shift"] = args =>
            {
                var arr = Arr(args[0]);
                if (arr.Count == 0) return TsValue.Null;
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
                int start = args.Length > 1 ? Normalize(I(args[1]), arr.Count) : 0;
                int deleteCount = args.Length > 2
                    ? Math.Clamp(I(args[2]), 0, arr.Count - start)
                    : arr.Count - start;
                var removed = new TsArray(deleteCount);
                for (int i = 0; i < deleteCount; i++)
                {
                    removed.Add(arr.Get(start));
                    arr.RemoveAt(start);
                }

                for (int i = args.Length - 1; i >= 3; i--)
                    arr.Insert(start, args[i]);

                return new TsArrayValue(removed);
            },
            ["Array::reverse"] = args => { Arr(args[0]).Reverse(); return args[0]; },
            ["Array::includes"] = args => Bool(IndexOf(Arr(args[0]), args[1]) >= 0),
            ["Array::indexOf"] = args => TsValue.FromInt32(IndexOf(Arr(args[0]), args[1])),
            ["Array::lastIndexOf"] = args =>
            {
                var arr = Arr(args[0]);
                for (int i = arr.Count - 1; i >= 0; i--)
                    if (ValueEquals(arr.Get(i), args[1])) return TsValue.FromInt32(i);
                return TsValue.FromInt32(-1);
            },
            ["Array::join"] = args =>
            {
                var arr = Arr(args[0]);
                string sep = args.Length > 1 ? S(args[1]) : ",";
                return TsValue.FromString(string.Join(sep, Enumerable.Range(0, arr.Count).Select(i => arr.Get(i).ToString())));
            },
            ["Array::slice"] = args =>
            {
                var arr = Arr(args[0]);
                int start = args.Length > 1 ? Normalize(I(args[1]), arr.Count) : 0;
                int end = args.Length > 2 ? Normalize(I(args[2]), arr.Count) : arr.Count;
                var result = new TsArray(Math.Max(end - start, 0));
                for (int i = start; i < end; i++) result.Add(arr.Get(i));
                return new TsArrayValue(result);
            },
            ["Array::concat"] = args =>
            {
                var result = new TsArray();
                foreach (var arg in args)
                {
                    if (arg is TsArrayValue av)
                        for (int i = 0; i < av.Value.Count; i++) result.Add(av.Value.Get(i));
                    else
                        result.Add(arg);
                }
                return new TsArrayValue(result);
            },
            ["Array::fill"] = args =>
            {
                var arr = Arr(args[0]);
                for (int i = 0; i < arr.Count; i++) arr.Set(i, args[1]);
                return args[0];
            },

            // ── String instance members (receiver = args[0]) ──
            ["String::charAt"] = args =>
            {
                string s = S(args[0]);
                int i = I(args[1]);
                return TsValue.FromString(i >= 0 && i < s.Length ? s[i].ToString() : "");
            },
            ["String::charCodeAt"] = args =>
            {
                string s = S(args[0]);
                int i = I(args[1]);
                return TsValue.FromInt32(i >= 0 && i < s.Length ? s[i] : -1);
            },
            ["String::toUpperCase"] = args => TsValue.FromString(S(args[0]).ToUpperInvariant()),
            ["String::toLowerCase"] = args => TsValue.FromString(S(args[0]).ToLowerInvariant()),
            ["String::trim"] = args => TsValue.FromString(S(args[0]).Trim()),
            ["String::substring"] = args =>
            {
                string s = S(args[0]);
                int start = Math.Clamp(args.Length > 1 ? I(args[1]) : 0, 0, s.Length);
                int end = Math.Clamp(args.Length > 2 ? I(args[2]) : s.Length, 0, s.Length);
                if (start > end) (start, end) = (end, start);
                return TsValue.FromString(s[start..end]);
            },
            ["String::slice"] = args =>
            {
                string s = S(args[0]);
                int start = Normalize(args.Length > 1 ? I(args[1]) : 0, s.Length);
                int end = Normalize(args.Length > 2 ? I(args[2]) : s.Length, s.Length);
                return TsValue.FromString(start < end ? s[start..end] : "");
            },
            ["String::repeat"] = args => TsValue.FromString(string.Concat(Enumerable.Repeat(S(args[0]), Math.Max(I(args[1]), 0)))),
            ["String::concat"] = args => TsValue.FromString(string.Concat(args.Select(a => a is TsStringValue sv ? sv.Value : a.ToString()))),
            ["String::includes"] = args => Bool(S(args[0]).Contains(S(args[1]), StringComparison.Ordinal)),
            ["String::startsWith"] = args => Bool(S(args[0]).StartsWith(S(args[1]), StringComparison.Ordinal)),
            ["String::endsWith"] = args => Bool(S(args[0]).EndsWith(S(args[1]), StringComparison.Ordinal)),
            ["String::indexOf"] = args => TsValue.FromInt32(S(args[0]).IndexOf(S(args[1]), StringComparison.Ordinal)),
            ["String::lastIndexOf"] = args => TsValue.FromInt32(S(args[0]).LastIndexOf(S(args[1]), StringComparison.Ordinal)),
            ["String::replace"] = args =>
            {
                string s = S(args[0]);
                string find = S(args[1]);
                int idx = s.IndexOf(find, StringComparison.Ordinal);
                return TsValue.FromString(idx < 0 ? s : s[..idx] + S(args[2]) + s[(idx + find.Length)..]);
            },
            ["String::replaceAll"] = args => TsValue.FromString(S(args[0]).Replace(S(args[1]), S(args[2]))),
            ["String::padStart"] = args => TsValue.FromString(S(args[0]).PadLeft(I(args[1]), args.Length > 2 ? S(args[2])[0] : ' ')),
            ["String::padEnd"] = args => TsValue.FromString(S(args[0]).PadRight(I(args[1]), args.Length > 2 ? S(args[2])[0] : ' ')),
            ["String::split"] = args =>
            {
                string s = S(args[0]);
                string sep = S(args[1]);
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

    private static int Normalize(int index, int length)
    {
        if (index < 0) index += length;
        return Math.Clamp(index, 0, length);
    }

    private static int IndexOf(TsArray arr, TsValue value)
    {
        for (int i = 0; i < arr.Count; i++)
            if (ValueEquals(arr.Get(i), value)) return i;
        return -1;
    }

    private static bool ValueEquals(TsValue left, TsValue right)
    {
        if (left is TsStringValue ls && right is TsStringValue rs) return ls.Value == rs.Value;
        if (left is TsBoolValue lb && right is TsBoolValue rb) return lb.Value == rb.Value;
        if (left is TsNull && right is TsNull) return true;
        double dl = D(left), dr = D(right);
        return !double.IsNaN(dl) && dl == dr;
    }

    private static object? ToJsonCompatible(TsValue value) => value switch
    {
        TsVoid => null,
        TsNull => null,
        TsBoolValue boolean => boolean.Value,
        TsInt32Value number => number.Value,
        TsInt64Value number => number.Value,
        TsUInt64Value number => number.Value,
        TsBigIntValue number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsFloat32Value number => number.Value,
        TsFloat64Value number => number.Value,
        TsDecimalValue number => number.Value,
        TsStringValue text => text.Value,
        TsArrayValue array => Enumerable.Range(0, array.Value.Count)
            .Select(index => ToJsonCompatible(array.Value.Get(index)))
            .ToList(),
        TsUint8ArrayValue bytes => Enumerable.Range(0, bytes.Length)
            .Select(index => bytes.Get(index))
            .ToList(),
        TsMapValue map => map.Value.Entries.ToDictionary(
            entry => S(entry.Key),
            entry => ToJsonCompatible(entry.Value),
            StringComparer.Ordinal),
        TsSetValue set => set.Value.Entries
            .Select(ToJsonCompatible)
            .ToList(),
        TsObjectValue obj => obj.Value.Fields.ToDictionary(
            pair => pair.Key,
            pair => ToJsonCompatible(pair.Value),
            StringComparer.Ordinal),
        _ => value.ToString()
    };

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
