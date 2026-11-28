using System.Globalization;
using System.Numerics;
using TypeSharp.VM.Interpreter;

namespace TypeSharp.VM.Memory;

/// <summary>Numeric, array-like, and integer-indexed rules specific to Uint8Array.</summary>
internal static class Uint8ArrayOperations
{
    private const double MaxArrayLikeLength = 9_007_199_254_740_991d;
    private const string SetRangeMessage = "Offset or source length is outside the destination Uint8Array bounds";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly BigInteger MaxFixedIntegerKey = BigInteger.Pow(10, 21);

    // Supplied only by the active VM for its represented typed byte operations.
    // Contextless callers never gain script callback dispatch.
    internal delegate TsValue InvokeOwnMethod(TsFunctionValue method, TsValue receiver);

    internal static TsUint8ArrayValue Range(TsUint8ArrayValue receiver, TsValue[] args, bool view, InvokeOwnMethod? invoke = null)
    {
        int start = args.Length > 1 ? RelativeIndex(args[1], receiver.Length, invoke) : 0;
        int end = args.Length > 2 && args[2] is not TsVoid ? RelativeIndex(args[2], receiver.Length, invoke) : receiver.Length;
        end = Math.Max(start, end);
        return view ? receiver.Subarray(start, end) : receiver.Slice(start, end);
    }

    internal static TsValue Set(TsUint8ArrayValue target, TsValue[] args, InvokeOwnMethod? invoke = null)
    {
        double offset = args.Length > 2 ? ToIntegerOrInfinity(args[2], invoke) : 0;
        if (offset < 0)
            throw ScriptError("RangeError", SetRangeMessage);
        if (args.Length < 2 || args[1] is TsNull or TsVoid)
            throw ScriptError("TypeError", "Uint8Array.set requires a non-null source");

        var source = args[1];
        double length = ArrayLikeLength(source, invoke);
        if (offset > target.Length || length > target.Length - offset)
            throw ScriptError("RangeError", SetRangeMessage);

        int start = (int)offset;
        if (source is TsUint8ArrayValue typed)
        {
            typed.CopyTo(target, start);
        }
        else
        {
            // Capacity was checked before coercing any source elements. A later
            // TypeError does not roll back successfully converted earlier bytes.
            for (int i = 0; i < (int)length; i++)
                target.SetByte(start + i, ToUint8(ArrayLikeAt(source, i), invoke));
        }
        return TsValue.Void;
    }

    internal static TsUint8ArrayValue Construct(TsValue[] args, InvokeOwnMethod? invoke = null)
    {
        var source = args.Length == 0 ? TsValue.Void : args[0];
        if (source is TsUint8ArrayValue typed)
            return new TsUint8ArrayValue(typed.CopyBytes());

        if (source is TsSetValue set)
        {
            var bytes = Allocate(set.Value.Count);
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = ToUint8(set.Value.GetAt(i), invoke);
            return new TsUint8ArrayValue(bytes);
        }

        if (source is TsMapValue map)
        {
            var bytes = Allocate(map.Value.Count);
            foreach (var entry in map.Value.Entries)
            {
                // A native Map entry converts as [key, value]. Its comma guarantees
                // NaN, but the two default string conversions can throw in order.
                _ = JoinPrimitive(entry.Key);
                _ = JoinPrimitive(entry.Value);
            }
            return new TsUint8ArrayValue(bytes);
        }

        if (source is TsArrayValue or TsObjectValue)
        {
            var bytes = Allocate(ArrayLikeLength(source, invoke));
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = ToUint8(ArrayLikeAt(source, i), invoke);
            return new TsUint8ArrayValue(bytes);
        }

        return new TsUint8ArrayValue(Allocate(ToIntegerOrInfinity(source, invoke)));
    }

    internal static TsValue ReadElement(TsUint8ArrayValue target, TsValue index, InvokeOwnMethod? invoke = null)
    {
        var key = ClassifyKey(index, target.Length, invoke, out int element);
        return key switch
        {
            KeyKind.Element => TsValue.FromFloat64(target.Get(element)),
            KeyKind.Length => TsValue.FromFloat64(target.Length),
            _ => TsValue.Void
        };
    }

    internal static void WriteElement(TsUint8ArrayValue target, TsValue index, TsValue value, InvokeOwnMethod? invoke = null)
    {
        var key = ClassifyKey(index, target.Length, invoke, out int element);
        if (key is KeyKind.Element or KeyKind.CanonicalInvalid)
        {
            // The RHS conversion precedes the bounds check for a canonical
            // numeric property key, even if no backing element can be written.
            byte converted = ToUint8(value, invoke);
            if (key == KeyKind.Element)
                target.SetByte(element, converted);
        }
    }

    internal static byte ToUint8(TsValue value) => ToUint8(value, null);

    private static byte ToUint8(TsValue value, InvokeOwnMethod? invoke)
    {
        double number = ToNumber(value, invoke);
        if (!double.IsFinite(number) || number == 0)
            return 0;

        double remainder = Math.Truncate(number) % 256;
        return (byte)(remainder < 0 ? remainder + 256 : remainder);
    }

    private static byte[] Allocate(double length)
    {
        if (length < 0 || length > Array.MaxLength)
            throw ScriptError("RangeError", "Invalid Uint8Array length");
        return new byte[(int)length];
    }

    private static double ArrayLikeLength(TsValue source, InvokeOwnMethod? invoke) => source switch
    {
        TsUint8ArrayValue typed => typed.Length,
        TsArrayValue array => array.Value.Count,
        TsStringValue text => text.Value.Length,
        TsObjectValue obj => Math.Clamp(ToIntegerOrInfinity(OwnField(obj.Value, "length"), invoke), 0, MaxArrayLikeLength),
        _ => 0
    };

    private static TsValue ArrayLikeAt(TsValue source, int index) => source switch
    {
        TsArrayValue array => array.Value.Get(index),
        TsStringValue text => TsValue.FromString(text.Value[index].ToString()),
        TsObjectValue obj => OwnField(obj.Value, index.ToString(Invariant)),
        _ => TsValue.Void
    };

    private static TsValue OwnField(TsObject obj, string name) =>
        obj.Fields.TryGetValue(name, out var value) ? value : TsValue.Void;

    private static int RelativeIndex(TsValue value, int length, InvokeOwnMethod? invoke)
    {
        double index = ToIntegerOrInfinity(value, invoke);
        if (index >= length) return length;
        if (index <= -length) return 0;
        return index < 0 ? length + (int)index : (int)index;
    }

    private static double ToIntegerOrInfinity(TsValue value, InvokeOwnMethod? invoke)
    {
        double number = ToNumber(value, invoke);
        return double.IsNaN(number) ? 0 : Math.Truncate(number);
    }

    private static bool TryPrimitiveNumber(TsValue? value, out double number)
    {
        switch (value)
        {
            case TsInt32Value n: number = n.Value; return true;
            case TsInt64Value n: number = n.Value; return true;
            case TsUInt64Value n: number = n.Value; return true;
            case TsFloat32Value n: number = n.Value; return true;
            case TsFloat64Value n: number = n.Value; return true;
            case TsDecimalValue n: number = (double)n.Value; return true;
            default: number = double.NaN; return false;
        }
    }

    private static double ToNumber(TsValue value, InvokeOwnMethod? invoke)
    {
        value = ConvertOwnPrimitive(value, stringHint: false, invoke);
        if (TryPrimitiveNumber(value, out double number))
            return number;

        return value switch
        {
            TsNull => 0,
            TsBoolValue flag => flag.Value ? 1 : 0,
            TsBigIntValue => throw ScriptError("TypeError", "Cannot convert a BigInt value to a number"),
            TsStringValue text => ParseNumber(text.Value),
            TsArrayValue or TsUint8ArrayValue => JoinPrimitive(value) switch
            {
                null => double.NaN,
                TsVoid => 0,
                TsStringValue text => ParseNumber(text.Value),
                // Array joining turns a BigInt into decimal text first.
                TsBigIntValue big => EcmaNumberText.BoundedIntegerToDouble(big.Value),
                var leaf when TryPrimitiveNumber(leaf, out double n) => n,
                _ => double.NaN
            },
            TsObjectValue obj when Builtins.TryGetNativeDateTimestamp(obj, out var stamp) => stamp,
            TsObjectValue obj => DefaultObjectNumber(obj),
            _ => double.NaN
        };
    }

    // Ordinary own methods only: no prototype lookup, generic conversion, or
    // recursive callback inside Array/Map/native default conversion.
    private static TsValue ConvertOwnPrimitive(TsValue value, bool stringHint, InvokeOwnMethod? invoke)
    {
        if (invoke is null || value is not TsObjectValue { Value: { TypeName: "Object" } plain })
            return value;

        bool HasCallable(string name) => plain.Fields.TryGetValue(name, out var method) && method is TsFunctionValue;
        if (!HasCallable("valueOf") && !HasCallable("toString"))
            return value;

        TsValue? TrySlot(string name)
        {
            // Re-read actual presence when this slot is reached: the previous
            // callback may replace or remove it. Own undefined is a shadow.
            if (!plain.Fields.TryGetValue(name, out var field))
                return name == "toString" ? TsValue.FromString("[object Object]") : null;
            if (field is not TsFunctionValue method)
                return null;
            var result = invoke(method, value);
            return result is TsVoid or TsNull or TsBoolValue or TsBigIntValue or TsStringValue ||
                TryPrimitiveNumber(result, out _) ? result : null;
        }

        return (stringHint
                ? TrySlot("toString") ?? TrySlot("valueOf")
                : TrySlot("valueOf") ?? TrySlot("toString"))
            ?? throw ScriptError("TypeError", "Cannot convert object to primitive value");
    }

    private static double DefaultObjectNumber(TsObjectValue obj)
    {
        ValidateObjectStringConversion(obj.Value);
        return double.NaN;
    }

    private static void ValidateObjectStringConversion(TsObject obj)
    {
        // No user callback/prototype dispatch here. A represented ordinary object
        // with a noncallable own toString has no available default string method.
        if (obj.TypeName == "Object" && obj.Fields.TryGetValue("toString", out var shadow)
            && shadow is not TsFunctionValue && OwnField(obj, "valueOf") is not TsFunctionValue)
            throw ScriptError("TypeError", "Cannot convert object to primitive value");
    }

    // For numeric/key classification only three forms are needed: empty (Void),
    // a single meaningful leaf, or text which cannot be numeric or a field (null).
    // Even in the last form all present children are visited, because a later
    // conversion can throw. Frames and the active-path set avoid CLR recursion.
    private static TsValue? JoinPrimitive(TsValue value)
    {
        if (value is not TsArrayValue root)
            return JoinLeaf(value);

        var active = new HashSet<TsArray>(ReferenceEqualityComparer.Instance) { root.Value };
        var frames = new List<(TsArray Array, int Next)> { (root.Value, 0) };
        TsValue? result = TsValue.Void;
        while (frames.Count != 0)
        {
            var (array, next) = frames[^1];
            if (next == array.Count)
            {
                if (next == 0) result = TsValue.Void;
                if (next > 1) result = null;
                active.Remove(array);
                frames.RemoveAt(frames.Count - 1);
                continue;
            }

            frames[^1] = (array, next + 1);
            var child = array.Get(next); // Holes are undefined.
            if (child is TsArrayValue nested)
            {
                if (active.Add(nested.Value))
                    frames.Add((nested.Value, 0));
                else
                    result = TsValue.Void; // A recursive join contributes no text.
            }
            else
            {
                result = JoinLeaf(child);
            }
        }
        return result;
    }

    private static TsValue? JoinLeaf(TsValue value)
    {
        switch (value)
        {
            case TsNull or TsVoid:
                return TsValue.Void;
            case TsUint8ArrayValue { Length: 0 }:
                return TsValue.Void;
            case TsUint8ArrayValue { Length: 1 } one:
                return TsValue.FromInt32(one.Get(0));
            case TsStringValue or TsBigIntValue:
                return value;
            case TsObjectValue obj:
                ValidateObjectStringConversion(obj.Value);
                return null; // Includes native Date's nonnumeric default string form.
            default:
                return TryPrimitiveNumber(value, out _) ? value : null;
        }
    }

    private static double ParseNumber(string text) => EcmaNumberText.Parse(text);

    private enum KeyKind { Ordinary, CanonicalInvalid, Element, Length }

    private static KeyKind ClassifyKey(TsValue key, int length, InvokeOwnMethod? invoke, out int element)
    {
        key = ConvertOwnPrimitive(key, stringHint: true, invoke);
        element = 0;
        TsValue? primitive = key is TsArrayValue or TsUint8ArrayValue ? JoinPrimitive(key) : key;
        if (primitive is null) return KeyKind.Ordinary;
        if (TryPrimitiveNumber(primitive, out double number))
            return ClassifyNumber(number, length, out element);

        switch (primitive)
        {
            case TsStringValue text:
                return ClassifyString(text.Value, length, out element);
            case TsBigIntValue big:
                // At 10^21 a Number string uses exponent form, which cannot
                // match the decimal property key produced by a BigInt.
                if (big.Value <= -MaxFixedIntegerKey || big.Value >= MaxFixedIntegerKey)
                    return KeyKind.Ordinary;
                return ClassifyString(big.Value.ToString(Invariant), length, out element);
            case TsObjectValue obj:
                ValidateObjectStringConversion(obj.Value);
                return KeyKind.Ordinary;
            default:
                return KeyKind.Ordinary;
        }
    }

    private static KeyKind ClassifyString(string key, int length, out int element)
    {
        element = 0;
        if (key is "length" or "byteLength") return KeyKind.Length;
        if (key is "-0") return KeyKind.CanonicalInvalid;
        // Fast path for small, canonical decimal integers. The formatting path
        // handles overlarge and fractional keys and the exact JS exponent forms.
        if (key.Length != 0 && (key[0] != '0' || key.Length == 1)
            && int.TryParse(key, NumberStyles.None, Invariant, out int index)
            && key.AsSpan().IndexOf('\0') < 0)
            return ClassifyNumber(index, length, out element);
        if (key.Length > 32) return KeyKind.Ordinary;
        double number = ParseNumber(key);
        return key == CanonicalNumberText(number)
            ? ClassifyNumber(number, length, out element) : KeyKind.Ordinary;
    }

    private static KeyKind ClassifyNumber(double number, int length, out int element)
    {
        element = 0;
        if (!(number >= 0 && number < length && number == Math.Truncate(number)))
            return KeyKind.CanonicalInvalid;
        element = (int)number;
        return KeyKind.Element;
    }

    internal static string CanonicalNumberText(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == 0) return "0";

        string sign = value < 0 ? "-" : "";
        double magnitude = Math.Abs(value);
        string raw = magnitude.ToString("R", Invariant);
        int e = raw.IndexOf('E');
        int scale = e < 0 ? 0 : int.Parse(raw.AsSpan(e + 1), NumberStyles.AllowLeadingSign, Invariant);
        string significand = e < 0 ? raw : raw[..e];
        int dot = significand.IndexOf('.');
        int decimalPoint = (dot < 0 ? significand.Length : dot) + scale;
        string digits = dot < 0 ? significand : significand.Remove(dot, 1);
        int leading = 0;
        while (digits[leading] == '0') leading++;
        decimalPoint -= leading;
        digits = digits[leading..].TrimEnd('0');

        if (magnitude >= 1e-6 && magnitude < 1e21)
        {
            if (decimalPoint <= 0) return sign + "0." + new string('0', -decimalPoint) + digits;
            if (decimalPoint >= digits.Length) return sign + digits + new string('0', decimalPoint - digits.Length);
            return sign + digits.Insert(decimalPoint, ".");
        }

        int exponent = decimalPoint - 1;
        string mantissa = digits.Length == 1 ? digits : digits.Insert(1, ".");
        return sign + mantissa + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(Invariant);
    }

    private static TsThrownException ScriptError(string name, string message)
    {
        var error = new TsObject(name);
        error.SetField("name", TsValue.FromString(name));
        error.SetField("message", TsValue.FromString(message));
        return new TsThrownException(TsValue.FromObject(error));
    }
}
