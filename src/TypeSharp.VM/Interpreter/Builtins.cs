using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeSharp.VM.Memory;

namespace TypeSharp.VM.Interpreter;

// Native implementations of the JS-style globals the binder exposes
// (Math.*, Number.*, console.*) and of array/string instance members.
// Instance members receive the receiver as args[0].
public static class Builtins
{
    private static readonly Dictionary<string, Func<TsValue[], TsValue>> Table = Build();
    private const int JsonCompositeDepthLimit = 1000;
    private static readonly JsonSerializerOptions LegacyJsonDepthOptions = new()
    {
        MaxDepth = JsonCompositeDepthLimit + 1,
        Converters = { new NativeDateJsonConverter() },
    };

    public static IReadOnlyCollection<string> Names => Table.Keys;

    public static bool TryGet(string name, out Func<TsValue[], TsValue> builtin) =>
        Table.TryGetValue(name, out builtin!);

    private static Dictionary<string, Func<TsValue[], TsValue>> Build()
    {
        var t = new Dictionary<string, Func<TsValue[], TsValue>>(StringComparer.Ordinal)
        {
            // ── Math ──
            ["Math::abs"] = args => Num(Math.Abs(MathArgument(args))),
            ["Math::floor"] = args => Num(Math.Floor(MathArgument(args))),
            ["Math::ceil"] = args => Num(Math.Ceiling(MathArgument(args))),
            ["Math::round"] = args => Num(RoundJsNumber(MathArgument(args))),
            ["Math::trunc"] = args => Num(Math.Truncate(MathArgument(args))),
            ["Math::sqrt"] = args => Num(Math.Sqrt(MathArgument(args))),
            ["Math::cbrt"] = args => Num(Math.Cbrt(MathArgument(args))),
            ["Math::pow"] = args =>
            {
                var @base = MathArgument(args);
                var exponent = MathArgument(args, 1);
                // ECMAScript differs from Math.Pow for these five input combinations.
                if ((@base == 1.0 && double.IsNaN(exponent)) ||
                    ((@base == 1.0 || @base == -1.0) && double.IsInfinity(exponent)))
                    return Num(double.NaN);

                return Num(Math.Pow(@base, exponent));
            },
            ["Math::log"] = args => Num(Math.Log(MathArgument(args))),
            ["Math::log2"] = args => Num(Math.Log2(MathArgument(args))),
            ["Math::log10"] = args => Num(Math.Log10(MathArgument(args))),
            ["Math::exp"] = args => Num(Math.Exp(MathArgument(args))),
            ["Math::sign"] = args =>
            {
                var n = MathArgument(args);
                return Num(double.IsNaN(n) || n == 0.0 ? n : n < 0.0 ? -1.0 : 1.0);
            },
            ["Math::sin"] = args =>
            {
                var n = MathArgument(args);
                return Num(n == 0.0 ? n : System.Math.Sin(n));
            },
            ["Math::cos"] = args => Num(System.Math.Cos(MathArgument(args))),
            ["Math::fround"] = args => Num((double)(float)MathArgument(args)),
            ["Math::clz32"] = args => Num(CountLeadingZeroBits32(MathArgument(args))),
            ["Math::imul"] = args =>
            {
                double left = MathArgument(args);
                double right = MathArgument(args, 1);
                return Num(unchecked((int)(ToUInt32(left) * ToUInt32(right))));
            },
            ["Math::acos"] = args => Num(System.Math.Acos(MathArgument(args))),
            ["Math::acosh"] = args => Num(System.Math.Acosh(MathArgument(args))),
            ["Math::asin"] = args =>
            {
                var n = MathArgument(args);
                return Num(n == 0.0 ? n : System.Math.Asin(n));
            },
            ["Math::asinh"] = args =>
            {
                var n = MathArgument(args);
                return Num(n == 0.0 ? n : System.Math.Asinh(n));
            },
            ["Math::cosh"] = args => Num(System.Math.Cosh(MathArgument(args))),
            ["Math::sinh"] = args =>
            {
                var n = MathArgument(args);
                return Num(n == 0.0 ? n : System.Math.Sinh(n));
            },
            ["Math::tan"] = args =>
            {
                var n = MathArgument(args);
                return Num(n == 0.0 ? n : System.Math.Tan(n));
            },
            ["Math::atan"] = args =>
            {
                var n = MathArgument(args);
                return Num(n == 0.0 ? n : System.Math.Atan(n));
            },
            ["Math::atan2"] = args =>
            {
                double y = MathArgument(args);
                double x = MathArgument(args, 1);
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
                var n = MathArgument(args);
                return Num(n == 0.0 ? n : System.Math.Atanh(n));
            },
            ["Math::tanh"] = args =>
            {
                var n = MathArgument(args);
                return Num(n == 0.0 ? n : System.Math.Tanh(n));
            },
            ["Math::random"] = _ => Num(Random.Shared.NextDouble()),
            ["Math::min"] = Min,
            ["Math::max"] = Max,
            ["Math::hypot"] = Hypot,

            // ── Number ──
            ["Number::isInteger"] = args => Bool(NumberSlot(args) switch
            {
                TsInt32Value => true,
                TsFloat32Value f => float.IsFinite(f.Value) && f.Value == MathF.Truncate(f.Value),
                TsFloat64Value d => double.IsFinite(d.Value) && d.Value == Math.Truncate(d.Value),
                _ => false
            }),
            ["Number::isFinite"] = args => Bool(NumberSlot(args) switch
            {
                TsInt32Value or TsDecimalValue => true,
                TsFloat64Value d => double.IsFinite(d.Value),
                TsFloat32Value f => float.IsFinite(f.Value),
                _ => false
            }),
            ["Number::isNaN"] = args => Bool(NumberSlot(args) switch
            {
                TsFloat64Value d => double.IsNaN(d.Value),
                TsFloat32Value f => float.IsNaN(f.Value),
                _ => false
            }),
            ["Number::isSafeInteger"] = args =>
            {
                static bool IsSafeBinary64(double value) =>
                    value >= -9007199254740991d && value <= 9007199254740991d && value == Math.Truncate(value);

                return Bool(NumberSlot(args) switch
                {
                    TsInt32Value => true,
                    TsFloat32Value f => IsSafeBinary64((double)f.Value),
                    TsFloat64Value d => IsSafeBinary64(d.Value),
                    TsDecimalValue m => m.Value >= -9007199254740991m && m.Value <= 9007199254740991m
                        && m.Value == decimal.Truncate(m.Value),
                    _ => false
                });
            },
            ["Number::parseFloat"] = args => Num(ParseFloatPrefix(NumberPrimitiveText(NumberSlot(args)))),
            ["Number::parseInt"] = NumberParseInt,
            ["Number"] = args => Num(args.Length == 0 ? 0d : NumberPrimitiveValue(NumberSlot(args))),

            // ── console ──
            ["console::log"] = args => { Console.WriteLine(string.Join(" ", args.Select(a => a.ToString()))); return TsValue.Void; },
            ["console::error"] = args => { Console.Error.WriteLine(string.Join(" ", args.Select(a => a.ToString()))); return TsValue.Void; },
            ["console::warn"] = args => { Console.Error.WriteLine(string.Join(" ", args.Select(a => a.ToString()))); return TsValue.Void; },

            ["JSON::stringify"] = args => args.Length == 0 || args[0] is TsVoid
                ? TsValue.Void
                : TsValue.FromString(JsonStringify(args[0])),

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
            ["Uint8Array::slice"] = args => Uint8ArrayOperations.Range(Bytes(args[0]), args, view: false),
            ["Uint8Array::subarray"] = args => Uint8ArrayOperations.Range(Bytes(args[0]), args, view: true),
            ["Uint8Array::reverse"] = args =>
            {
                var bytes = Bytes(args[0]);
                bytes.Reverse();
                return bytes;
            },
            ["Uint8Array::toReversed"] = args =>
            {
                var copy = new TsUint8ArrayValue(Bytes(args[0]).CopyBytes());
                copy.Reverse();
                return copy;
            },
            ["Uint8Array::set"] = args => Uint8ArrayOperations.Set(Bytes(args[0]), args),

            // ── Map instance members (receiver = args[0]) ──
            ["Map::set"] = args =>
            {
                var receiver = NativeMap.RequireReceiver(args);
                receiver.Value.Set(NativeMap.ArgOrVoid(args, 1), NativeMap.ArgOrVoid(args, 2));
                return receiver;
            },
            ["Map::get"] = args =>
                NativeMap.RequireReceiver(args).Value.Get(NativeMap.ArgOrVoid(args, 1)),
            ["Map::has"] = args =>
                Bool(NativeMap.RequireReceiver(args).Value.Contains(NativeMap.ArgOrVoid(args, 1))),
            ["Map::delete"] = args =>
                Bool(NativeMap.RequireReceiver(args).Value.Remove(NativeMap.ArgOrVoid(args, 1))),
            ["Map::clear"] = args =>
            {
                NativeMap.RequireReceiver(args).Value.Clear();
                return TsValue.Void;
            },
            ["Map::values"] = args =>
            {
                var receiver = NativeMap.RequireReceiver(args);
                var result = new TsArray();
                foreach (var entry in receiver.Value.Entries)
                    result.Add(entry.Value);
                return new TsArrayValue(result);
            },
            ["Map::keys"] = args =>
            {
                var receiver = NativeMap.RequireReceiver(args);
                var result = new TsArray();
                foreach (var entry in receiver.Value.Entries)
                    result.Add(entry.Key);
                return new TsArrayValue(result);
            },
            ["Map::entries"] = args =>
            {
                var receiver = NativeMap.RequireReceiver(args);
                var result = new TsArray();
                foreach (var entry in receiver.Value.Entries)
                    result.Add(CreateEntryPair(entry.Key, entry.Value));
                return new TsArrayValue(result);
            },

            // Date instance members (receiver = args[0]).
            ["Date::getTime"] = args => NativeDate.Timestamp(args[0]),
            ["Date::valueOf"] = args => NativeDate.Timestamp(args[0]),
            ["Date::toISOString"] = args => NativeDate.ToIsoString(args[0]),
            ["Date::getUTCFullYear"] = args => NativeDate.GetUtc(args[0], NativeDate.UtcPart.Year),
            ["Date::getUTCMonth"] = args => NativeDate.GetUtc(args[0], NativeDate.UtcPart.Month),
            ["Date::getUTCDate"] = args => NativeDate.GetUtc(args[0], NativeDate.UtcPart.Date),
            ["Date::getUTCDay"] = args => NativeDate.GetUtc(args[0], NativeDate.UtcPart.Day),
            ["Date::getUTCHours"] = args => NativeDate.GetUtc(args[0], NativeDate.UtcPart.Hours),
            ["Date::getUTCMinutes"] = args => NativeDate.GetUtc(args[0], NativeDate.UtcPart.Minutes),
            ["Date::getUTCSeconds"] = args => NativeDate.GetUtc(args[0], NativeDate.UtcPart.Seconds),
            ["Date::getUTCMilliseconds"] = args => NativeDate.GetUtc(args[0], NativeDate.UtcPart.Milliseconds),

            // Set instance members use the historical receiver-prefixed Call ABI.
            ["Set::add"] = args =>
            {
                var receiver = NativeSet.RequireReceiver(args);
                receiver.Value.Add(NativeSet.ArgOrVoid(args, 1));
                return receiver;
            },
            ["Set::has"] = args =>
                Bool(NativeSet.RequireReceiver(args).Value.Contains(NativeSet.ArgOrVoid(args, 1))),
            ["Set::delete"] = args =>
                Bool(NativeSet.RequireReceiver(args).Value.Remove(NativeSet.ArgOrVoid(args, 1))),
            ["Set::clear"] = args =>
            {
                NativeSet.RequireReceiver(args).Value.Clear();
                return TsValue.Void;
            },
            ["Set::values"] = SnapshotSetValues,
            ["Set::keys"] = SnapshotSetValues,
            ["Set::entries"] = args =>
            {
                var receiver = NativeSet.RequireReceiver(args);
                var result = new TsArray();
                foreach (var value in receiver.Value.Entries)
                    result.Add(CreateEntryPair(value, value));
                return new TsArrayValue(result);
            },

            // ── Global functions ──
            ["parseInt"] = ParseIntBuiltin,
            ["parseFloat"] = args => Num(ParseFloatPrefix(GlobalParseFloatText(NumberSlot(args)))),
            ["isNaN"] = args => Bool(double.IsNaN(MathNumber(NumberSlot(args)))),
            ["isFinite"] = args => Bool(double.IsFinite(MathNumber(NumberSlot(args)))),
            ["String"] = NativeString.ConvertValue,
            ["Boolean"] = args => Bool(args.Length > 0 && Interpreter.AsBool(args[0])),
            ["BigInt"] = args => ToBigInt(args.Length == 0 ? TsValue.Void : args[0]),
            ["RegExp::test"] = args =>
            {
                if (args.Length == 0 || args[0] is not TsRegexValue regex)
                    throw new InvalidOperationException("RegExp.test requires a RegExp receiver and input");
                return NativeRegex.Test(regex, args.Length > 1 ? args[1] : TsValue.Void);
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
                    return element switch
                    {
                        TsNull or TsVoid => string.Empty,
                        TsBoolValue => S(element),
                        TsFloat32Value f32 when f32.Value == 0f => "0",
                        TsFloat64Value f64 when f64.Value == 0d => "0",
                        TsFloat32Value f32 when float.IsFinite(f32.Value) => f32.Value.ToString(CultureInfo.InvariantCulture),
                        TsFloat64Value f64 when double.IsFinite(f64.Value) => f64.Value.ToString(CultureInfo.InvariantCulture),
                        TsDecimalValue dec when dec.Value != 0m => dec.Value.ToString(CultureInfo.InvariantCulture),
                        _ => element.ToString()
                    };
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
                static int Bound(TsValue bound, int length) =>
                    bound is TsStringValue text && TryGetBoundedHexPosition(text.Value, length, out int hexPosition)
                        ? hexPosition : Normalize(I(bound), length);
                int start = args.Length > 1 ? Bound(args[1], arr.Count) : 0;
                int end = args.Length > 2 && args[2] is not TsVoid ? Bound(args[2], arr.Count) : arr.Count;
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
            [NativeString.InvokeMemberReference] = NativeString.RejectUnscopedMemberReference,
            ["String::charAt"] = NativeString.CharAt,
            ["String::charCodeAt"] = NativeString.CharCodeAt,
            ["String::codePointAt"] = NativeString.CodePointAt,
            ["String::at"] = NativeString.At,
            ["String::toUpperCase"] = args => TsValue.FromString(StringUppercaseExpansions.Uppercase(NativeString.Receiver(args))),
            ["String::toLowerCase"] = args => TsValue.FromString(Lowercase(NativeString.Receiver(args))),
            ["String::trim"] = args => TsValue.FromString(TrimEcmaWhitespace(NativeString.Receiver(args))),
            ["String::trimStart"] = args => TsValue.FromString(TrimStartEcmaWhitespace(NativeString.Receiver(args))),
            ["String::trimEnd"] = args => TsValue.FromString(TrimEndEcmaWhitespace(NativeString.Receiver(args))),
            ["String::substring"] = NativeString.Substring,
            ["String::slice"] = NativeString.Slice,
            ["String::repeat"] = NativeString.Repeat,
            ["String::concat"] = NativeString.Concat,
            ["String::includes"] = args => NativeString.Search(args, NativeString.SearchKind.Includes),
            ["String::startsWith"] = args => NativeString.Search(args, NativeString.SearchKind.Starts),
            ["String::endsWith"] = args => NativeString.Search(args, NativeString.SearchKind.Ends),
            ["String::indexOf"] = args => NativeString.Search(args, NativeString.SearchKind.IndexOf),
            ["String::lastIndexOf"] = args => NativeString.Search(args, NativeString.SearchKind.LastIndexOf),
            ["String::replace"] = args => NativeString.Replace(args, all: false),
            ["String::replaceAll"] = args => NativeString.Replace(args, all: true),
            ["String::padStart"] = args => NativeString.Pad(args, before: true),
            ["String::padEnd"] = args => NativeString.Pad(args, before: false),
            ["String::split"] = NativeString.Split,
        };
        return t;
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

    // This policy is local to Math: fixed methods read their actual argument
    // slots in order, and an absent slot is the same as explicit undefined.
    private static double MathArgument(TsValue[] args, int index = 0) =>
        MathNumber(index < args.Length ? args[index] : TsValue.Void);

    private static double MathNumber(TsValue value) => value switch
    {
        TsNull => 0d,
        TsStringValue text => EcmaNumberText.Parse(text.Value),
        TsBigIntValue => throw MathBigIntTypeError(),
        TsInt32Value or TsInt64Value or TsUInt64Value or TsFloat32Value or
            TsFloat64Value or TsDecimalValue or TsBoolValue => D(value),
        _ => double.NaN
    };

    private static TsThrownException MathBigIntTypeError()
    {
        var error = new TsObject("TypeError");
        error.SetField("name", TsValue.FromString("TypeError"));
        error.SetField("message", TsValue.FromString("Cannot convert a BigInt value to a number"));
        return new TsThrownException(TsValue.FromObject(error));
    }

    private static TsValue Min(TsValue[] args) => Extremum(args, minimum: true);

    private static TsValue Max(TsValue[] args) => Extremum(args, minimum: false);

    private static TsValue Extremum(TsValue[] args, bool minimum)
    {
        double best = minimum ? double.PositiveInfinity : double.NegativeInfinity;
        bool hasNaN = false;
        foreach (var arg in args)
        {
            // A NaN determines the result but does not skip later coercion.
            double value = MathNumber(arg);
            if (double.IsNaN(value))
            {
                hasNaN = true;
                continue;
            }

            if (minimum ? value < best : value > best)
                best = value;
            else if (value == 0d && best == 0d && double.IsNegative(value) == minimum)
                best = value;
        }

        return Num(hasNaN ? double.NaN : best);
    }

    private static TsValue Hypot(TsValue[] args)
    {
        Span<double> magnitudes = args.Length <= 16
            ? stackalloc double[args.Length]
            : new double[args.Length];
        double max = 0d;
        bool hasInfinity = false;
        bool hasNaN = false;
        for (int i = 0; i < args.Length; i++)
        {
            double magnitude = Math.Abs(MathNumber(args[i]));
            magnitudes[i] = magnitude;
            if (double.IsInfinity(magnitude))
                hasInfinity = true;
            else if (double.IsNaN(magnitude))
                hasNaN = true;
            else if (magnitude > max)
                max = magnitude;
        }

        if (hasInfinity) return Num(double.PositiveInfinity);
        if (hasNaN) return Num(double.NaN);
        if (max == 0d) return Num(0d);

        double sum = 0d;
        double compensation = 0d;
        foreach (double magnitude in magnitudes)
        {
            // Direct division also works when the scale is subnormal.
            double scaled = magnitude / max;
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

    private static int I(TsValue v) => (int)D(v);

    private static TsValue ToBigInt(TsValue value) => value switch
    {
        TsBigIntValue => value,
        TsInt32Value i => TsValue.FromBigInt(new BigInteger(i.Value)),
        TsInt64Value l => TsValue.FromBigInt(new BigInteger(l.Value)),
        TsUInt64Value u => TsValue.FromBigInt(new BigInteger(u.Value)),
        TsFloat32Value f => IntegralFloatToBigInt(f.Value),
        TsFloat64Value d => IntegralFloatToBigInt(d.Value),
        TsDecimalValue m => m.Value == decimal.Truncate(m.Value)
            ? TsValue.FromBigInt(new BigInteger(m.Value))
            : throw BigIntError("RangeError", "Cannot convert a non-integer number to BigInt"),
        TsStringValue s => TsValue.FromBigInt(ParseBigIntText(s.Value)),
        TsBoolValue b => TsValue.FromBigInt(b.Value ? BigInteger.One : BigInteger.Zero),
        _ => throw BigIntError("TypeError", "Cannot convert value to BigInt")
    };

    // Single widens to Double exactly. Decimal must stay on its separate exact path.
    private static TsValue IntegralFloatToBigInt(double value) =>
        double.IsFinite(value) && value == Math.Truncate(value)
            ? TsValue.FromBigInt(new BigInteger(value))
            : throw BigIntError("RangeError", "Cannot convert a non-integer number to BigInt");

    private static BigInteger ParseBigIntText(string text)
    {
        var token = TrimEcmaWhitespace(text).AsSpan();
        if (token.IsEmpty)
            return BigInteger.Zero;

        bool hasSign = token[0] is '+' or '-';
        bool negative = token[0] == '-';
        int radix = 10;
        if (!hasSign && token.Length >= 2 && token[0] == '0')
        {
            radix = token[1] switch
            {
                'x' or 'X' => 16,
                'o' or 'O' => 8,
                'b' or 'B' => 2,
                _ => 10
            };
        }

        var digits = token[(hasSign ? 1 : radix == 10 ? 0 : 2)..];
        if (digits.IsEmpty)
            throw BigIntError("SyntaxError", "Cannot convert string to BigInt");

        // Validate the complete ASCII token before parsing; CLR parsing alone
        // does not enforce the ECMAScript digit and trailing-NUL rules.
        if (radix == 10)
        {
            foreach (char ch in digits)
            {
                if (ch is < '0' or > '9')
                    throw BigIntError("SyntaxError", "Cannot convert string to BigInt");
            }

            var magnitude = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
            return negative ? -magnitude : magnitude;
        }

        // Nondecimal forms are unsigned magnitudes, including 0xFF.
        var result = BigInteger.Zero;
        foreach (char ch in digits)
        {
            int digit = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'f' => ch - 'a' + 10,
                >= 'A' and <= 'F' => ch - 'A' + 10,
                _ => -1
            };
            if (digit < 0 || digit >= radix)
                throw BigIntError("SyntaxError", "Cannot convert string to BigInt");
            result = result * radix + digit;
        }
        return result;
    }

    private static TsThrownException BigIntError(string name, string message)
    {
        var error = new TsObject(name);
        error.SetField("name", TsValue.FromString(name));
        error.SetField("message", TsValue.FromString(message));
        return new TsThrownException(TsValue.FromObject(error));
    }

    // ECMAScript WhiteSpace and LineTerminator; NEL is not included.
    internal static bool IsEcmaTrimWhitespace(char ch) =>
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
            if (!StringFinalSigma.IsFinal(text, i))
                continue;

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

    private static TsValue NumberSlot(TsValue[] args, int index = 0) =>
        index < args.Length ? args[index] : TsValue.Void;

    private static double NumberPrimitiveValue(TsValue value) => value switch
    {
        TsNull => 0d,
        TsStringValue text => EcmaNumberText.Parse(text.Value),
        TsBigIntValue big => EcmaNumberText.BoundedIntegerToDouble(big.Value),
        _ => D(value)
    };

    private static string NumberPrimitiveText(TsValue value) => value switch
    {
        TsFloat64Value number => Uint8ArrayOperations.CanonicalNumberText(number.Value),
        // A Float32 supplied by CLR is a Number with its exact widened value.
        TsFloat32Value number => Uint8ArrayOperations.CanonicalNumberText(number.Value),
        TsInt32Value n => n.Value.ToString(CultureInfo.InvariantCulture),
        TsInt64Value n => n.Value.ToString(CultureInfo.InvariantCulture),
        TsUInt64Value n => n.Value.ToString(CultureInfo.InvariantCulture),
        TsDecimalValue n => n.Value.ToString(CultureInfo.InvariantCulture),
        TsBigIntValue n => n.Value.ToString(CultureInfo.InvariantCulture),
        TsStringValue text => text.Value,
        TsBoolValue boolean => boolean.Value ? "true" : "false",
        TsNull => "null",
        TsVoid => "undefined",
        // This is not an object/array ToPrimitive implementation.
        _ => S(value)
    };

    private static TsValue NumberParseInt(TsValue[] args)
    {
        string text = NumberPrimitiveText(NumberSlot(args));
        var radix = NumberSlot(args, 1);
        if (radix is TsBigIntValue)
            throw MathBigIntTypeError(); // Both native ToNumber sites reject the explicit BigInt carrier.
        int radix32 = unchecked((int)ToUInt32(NumberPrimitiveValue(radix)));
        return Num(ParseIntExplicitRadix(text, radix32));
    }

    // Only floating carriers change their text representation here; preserve other legacy carriers.
    private static string GlobalParseFloatText(TsValue value) =>
        value is TsFloat32Value or TsFloat64Value ? NumberPrimitiveText(value) : S(value);

    private static bool IsDefaultGlobalParseIntRadix(TsValue radix) => radix switch
    {
        TsVoid or TsNull => true,
        TsBoolValue boolean => !boolean.Value,
        TsInt32Value number => number.Value == 0,
        TsFloat32Value number => number.Value == 0 || !float.IsFinite(number.Value),
        TsFloat64Value number => number.Value == 0 || !double.IsFinite(number.Value),
        TsDecimalValue number => number.Value == 0m,
        _ => false
    };

    private static TsValue ParseIntBuiltin(TsValue[] args)
    {
        if (args.Length == 0)
            return Num(double.NaN);

        TsValue first = NumberSlot(args);
        TsValue radixArg = NumberSlot(args, 1);
        if (first is TsStringValue primitiveString)
        {
            if (IsDefaultGlobalParseIntRadix(radixArg))
                return Num(ParseIntExplicitRadix(primitiveString.Value, 0));
            if (radixArg is TsBoolValue) // false took the default branch; true is radix 1.
                return Num(ParseIntExplicitRadix(primitiveString.Value, 1));
        }

        // Non-string first arguments and unrecognized radix carriers retain the global legacy path.
        string input = S(first);
        if (args.Length > 1 && TryGetExplicitParseIntRadix(radixArg, out int explicitRadix))
            return Num(ParseIntExplicitRadix(input, explicitRadix));

        string text = input.Trim();
        int radix = args.Length > 1 ? I(radixArg) : 10;
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

    internal static string S(TsValue v) => v switch
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

    private static TsValue SnapshotSetValues(TsValue[] args)
    {
        var receiver = NativeSet.RequireReceiver(args);
        var result = new TsArray();
        foreach (var value in receiver.Value.Entries)
            result.Add(value);
        return new TsArrayValue(result);
    }

    internal static bool TryGetNativeDateTimestamp(TsObjectValue value, out double result) =>
        NativeDate.TryGetTimestamp(value, out result);

    private sealed record NativeDateJson(string Iso);

    private sealed class NativeDateJsonConverter : JsonConverter<NativeDateJson>
    {
        public override NativeDateJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, NativeDateJson value, JsonSerializerOptions options)
        {
            // An ISO string contains only checked ASCII Date characters. Write
            // it exactly (including a leading '+' on expanded positive years)
            // without changing escaping for any ordinary JSON string.
            writer.WriteRawValue("\"" + value.Iso + "\"", skipInputValidation: true);
        }
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

    private static string JsonStringify(TsValue value)
    {
        // Project the entire graph first: a projection failure, including a cycle,
        // always takes precedence over any independent backend depth failure.
        var projected = ToJsonCompatible(value, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        var output = new StringBuilder();
        bool needsLegacyDepthGuard = false;
        WriteJsonValue(output, projected, 0, ref needsLegacyDepthGuard);
        if (needsLegacyDepthGuard)
            _ = JsonSerializer.Serialize(projected, LegacyJsonDepthOptions);
        return output.ToString();
    }

    private static void WriteJsonValue(StringBuilder output, object? value, int depth, ref bool needsLegacyDepthGuard)
    {
        switch (value)
        {
            case null: output.Append("null"); break;
            case bool boolean: output.Append(boolean ? "true" : "false"); break;
            case byte number: output.Append(number.ToString(CultureInfo.InvariantCulture)); break;
            case int number: output.Append(number.ToString(CultureInfo.InvariantCulture)); break;
            case long number: output.Append(number.ToString(CultureInfo.InvariantCulture)); break;
            case ulong number: output.Append(number.ToString(CultureInfo.InvariantCulture)); break;
            case decimal number: output.Append(number.ToString(CultureInfo.InvariantCulture)); break;
            case float number: output.Append(Uint8ArrayOperations.CanonicalNumberText((double)number)); break;
            case double number: output.Append(Uint8ArrayOperations.CanonicalNumberText(number)); break;
            case string text: WriteJsonString(output, text); break;
            case NativeDateJson date: WriteJsonString(output, date.Iso); break;
            case List<object?> elements:
                output.Append('[');
                for (int i = 0; i < elements.Count; i++)
                {
                    if (i > 0) output.Append(',');
                    WriteJsonValue(output, elements[i], depth + 1, ref needsLegacyDepthGuard);
                }
                output.Append(']');
                break;
            case Dictionary<string, object?> properties:
                output.Append('{');
                bool first = true;
                foreach (var (key, member) in properties)
                    WriteJsonMember(output, key, member, depth, ref first, ref needsLegacyDepthGuard);
                output.Append('}');
                break;
            case OrderedDictionary ordered:
                // Only a Uint8Array can add an object beyond the guarded projection limit.
                // An empty terminal object has no byte write at the backend limit. For a
                // nonempty one, validate using the old backend so exception/path stay exact;
                // its text is never returned.
                if (depth == JsonCompositeDepthLimit && ordered.Count != 0)
                    needsLegacyDepthGuard = true;
                output.Append('{');
                bool firstOrdered = true;
                foreach (DictionaryEntry entry in ordered)
                    WriteJsonMember(output, (string)entry.Key, entry.Value, depth, ref firstOrdered, ref needsLegacyDepthGuard);
                output.Append('}');
                break;
            default:
                throw new InvalidOperationException("Unexpected JSON projection");
        }
    }

    private static void WriteJsonMember(StringBuilder output, string key, object? member, int depth,
        ref bool first, ref bool needsLegacyDepthGuard)
    {
        if (!first) output.Append(',');
        first = false;
        WriteJsonString(output, key);
        output.Append(':');
        WriteJsonValue(output, member, depth + 1, ref needsLegacyDepthGuard);
    }

    private static void WriteJsonString(StringBuilder output, string text)
    {
        output.Append('"');
        for (int index = 0; index < text.Length; index++)
        {
            char unit = text[index];
            switch (unit)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\t': output.Append("\\t"); break;
                case '\n': output.Append("\\n"); break;
                case '\f': output.Append("\\f"); break;
                case '\r': output.Append("\\r"); break;
                default:
                    if (char.IsHighSurrogate(unit) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                    {
                        output.Append(unit);
                        output.Append(text[++index]);
                    }
                    else if (unit < '\u0020' || char.IsSurrogate(unit))
                    {
                        const string hex = "0123456789abcdef";
                        output.Append("\\u");
                        output.Append(hex[unit >> 12]);
                        output.Append(hex[(unit >> 8) & 0xf]);
                        output.Append(hex[(unit >> 4) & 0xf]);
                        output.Append(hex[unit & 0xf]);
                    }
                    else
                    {
                        output.Append(unit);
                    }
                    break;
            }
        }
        output.Append('"');
    }

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
        TsObjectValue date when NativeDate.TryGetTimestamp(date, out double timestamp) =>
            double.IsNaN(timestamp) ? null : new NativeDateJson(NativeDate.FormatIso(timestamp)),
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
        if (depth >= JsonCompositeDepthLimit)
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
