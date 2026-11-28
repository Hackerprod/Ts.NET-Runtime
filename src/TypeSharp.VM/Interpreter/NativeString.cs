using System.Globalization;
using System.Text;
using TypeSharp.VM.Memory;

namespace TypeSharp.VM.Interpreter;

internal static class NativeString
{
    private const string Prefix = "String::";
    internal const string InvokeMemberReference = "String::<invoke-member-reference>";

    // The registered native targets are the sole source of trusted identities.
    private static readonly Lazy<Dictionary<string, TsFunctionValue>> References = new(() =>
        Builtins.Names.Where(name => name.StartsWith(Prefix, StringComparison.Ordinal) && name != InvokeMemberReference)
            .ToDictionary(name => name, name => new TsFunctionValue(name), StringComparer.Ordinal));

    internal static TsFunctionValue? GetReference(string qualified) =>
        qualified.StartsWith(Prefix, StringComparison.Ordinal) &&
        References.Value.TryGetValue(qualified, out var value) ? value : null;

    internal static bool IsIntrinsicReference(TsValue value) =>
        value is TsFunctionValue function && ReferenceEquals(GetReference(function.FunctionName), function);

    internal static bool IsMethod(string qualified) =>
        GetReference(qualified) is not null;

    internal static TsValue Read(TsStringValue receiver, string key)
    {
        if (key == "length") return TsValue.FromInt32(receiver.Value.Length);
        if (key.Length != 0 && key[0] is >= '0' and <= '9' && (key[0] != '0' || key.Length == 1))
        {
            int index = 0;
            foreach (char c in key)
            {
                int digit = c - '0';
                if ((uint)digit > 9 || index > (int.MaxValue - digit) / 10)
                    return TsValue.Void;
                index = index * 10 + digit;
            }
            return index < receiver.Value.Length
                ? TsValue.FromString(receiver.Value[index].ToString())
                : TsValue.Void;
        }
        return (TsValue?)GetReference(Prefix + key) ?? TsValue.Void;
    }

    internal static TsValue Read(TsStringValue receiver, TsValue key) => Read(receiver, Text(key));

    internal static string Receiver(TsValue[] args) =>
        args.Length > 0 && args[0] is TsStringValue value ? value.Value : throw ReceiverError();

    internal static TsThrownException ReceiverError() => Error("TypeError", "String method requires a String receiver");

    // This spelling is reserved by the linker via the builtin table; only the VM's
    // dedicated receiver/target/arguments instruction path may dispatch through it.
    internal static TsValue RejectUnscopedMemberReference(TsValue[] _) => throw ReceiverError();

    internal static TsValue InvokeReference(TsFunctionValue target, TsValue? receiver, TsValue[] positional)
    {
        if (receiver is not TsStringValue || !IsIntrinsicReference(target) ||
            !Builtins.TryGet(target.FunctionName, out var function))
            throw ReceiverError();

        var nativeArgs = new TsValue[positional.Length + 1];
        nativeArgs[0] = receiver;
        Array.Copy(positional, 0, nativeArgs, 1, positional.Length);
        return function(nativeArgs);
    }

    private static TsThrownException Error(string name, string message)
    {
        var error = new TsObject(name);
        error.SetField("name", TsValue.FromString(name));
        error.SetField("message", TsValue.FromString(message));
        return new TsThrownException(TsValue.FromObject(error));
    }

    private static TsValue Slot(TsValue[] args, int index) => index < args.Length ? args[index] : TsValue.Void;

    internal static TsValue ConvertValue(TsValue[] args) =>
        TsValue.FromString(args.Length == 0 ? "" : Text(args[0]));

    private static string Text(TsValue value) => value switch
    {
        TsStringValue s => s.Value,
        TsFloat64Value number => Uint8ArrayOperations.CanonicalNumberText(number.Value),
        TsFloat32Value number => Uint8ArrayOperations.CanonicalNumberText(number.Value),
        TsInt32Value number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsInt64Value number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsUInt64Value number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsDecimalValue number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsBigIntValue number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsBoolValue boolean => boolean.Value ? "true" : "false",
        TsNull => "null",
        TsVoid => "undefined",
        TsArrayValue array => ArrayText(array.Value),
        TsObjectValue { Value.TypeName: "Object" } => "[object Object]",
        _ => value.ToString(),
    };

    private static string ArrayText(TsArray array)
    {
        var buffer = new StringBuilder();
        // Stack traversal preserves join semantics without reentering CLR or script
        // conversions. On a back-edge the nested array contributes an empty field.
        var active = new HashSet<TsArray>(ReferenceEqualityComparer.Instance) { array };
        var pending = new Stack<(TsArray Array, int Next)>();
        pending.Push((array, 0));
        while (pending.TryPop(out var frame))
        {
            for (int i = frame.Next; i < frame.Array.Count; i++)
            {
                if (i > 0) buffer.Append(',');
                if (frame.Array.IsHole(i)) continue;
                var value = frame.Array.Get(i);
                if (value is TsNull or TsVoid) continue;
                if (value is TsArrayValue nested)
                {
                    if (!active.Add(nested.Value)) continue;
                    pending.Push((frame.Array, i + 1));
                    frame = (nested.Value, 0);
                    i = -1;
                    continue;
                }
                buffer.Append(Text(value));
            }
            active.Remove(frame.Array);
        }
        return buffer.ToString();
    }

    private static double Number(TsValue value) => value switch
    {
        TsNull => 0d,
        TsStringValue s => EcmaNumberText.Parse(s.Value),
        TsBoolValue b => b.Value ? 1d : 0d,
        TsInt32Value n => n.Value,
        TsInt64Value n => n.Value,
        TsUInt64Value n => n.Value,
        TsFloat64Value n => n.Value,
        TsFloat32Value n => n.Value,
        TsDecimalValue n => (double)n.Value,
        TsBigIntValue => throw Error("TypeError", "Cannot convert a BigInt value to a number"),
        _ => double.NaN,
    };

    private static double Integer(TsValue value)
    {
        double number = Number(value);
        return double.IsNaN(number) ? 0d : Math.Truncate(number);
    }

    private static int Clamp(TsValue value, int length) => (int)Math.Clamp(Integer(value), 0d, length);

    private static int RelativeBound(TsValue value, int length)
    {
        double index = Integer(value);
        return (int)Math.Clamp(index < 0 ? index + length : index, 0d, length);
    }

    internal static TsValue CharAt(TsValue[] args)
    {
        string text = Receiver(args);
        double index = Integer(Slot(args, 1));
        return TsValue.FromString(index >= 0d && index < text.Length ? text[(int)index].ToString() : "");
    }

    internal static TsValue CharCodeAt(TsValue[] args)
    {
        string text = Receiver(args);
        double index = Integer(Slot(args, 1));
        return index >= 0d && index < text.Length ? TsValue.FromInt32(text[(int)index]) : TsValue.FromFloat64(double.NaN);
    }

    internal static TsValue CodePointAt(TsValue[] args)
    {
        string text = Receiver(args);
        double index = Integer(Slot(args, 1));
        if (index < 0d || index >= text.Length) return TsValue.Void;
        int i = (int)index;
        char unit = text[i];
        return TsValue.FromInt32(char.IsHighSurrogate(unit) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
            ? char.ConvertToUtf32(unit, text[i + 1]) : unit);
    }

    internal static TsValue At(TsValue[] args)
    {
        string text = Receiver(args);
        double index = Integer(Slot(args, 1));
        if (index < 0d) index += text.Length;
        return index >= 0d && index < text.Length ? TsValue.FromString(text[(int)index].ToString()) : TsValue.Void;
    }

    internal static TsValue Substring(TsValue[] args)
    {
        string text = Receiver(args);
        int start = Clamp(Slot(args, 1), text.Length);
        var endValue = Slot(args, 2);
        int end = endValue is TsVoid ? text.Length : Clamp(endValue, text.Length);
        if (start > end) (start, end) = (end, start);
        return TsValue.FromString(text[start..end]);
    }

    internal static TsValue Slice(TsValue[] args)
    {
        string text = Receiver(args);
        int start = RelativeBound(Slot(args, 1), text.Length);
        var endValue = Slot(args, 2);
        int end = endValue is TsVoid ? text.Length : RelativeBound(endValue, text.Length);
        return TsValue.FromString(start >= end ? "" : text[start..end]);
    }

    internal static TsValue Repeat(TsValue[] args)
    {
        string text = Receiver(args);
        double count = Integer(Slot(args, 1));
        if (count < 0d) throw Error("RangeError", "String.repeat count must not be negative");
        if (!double.IsFinite(count)) throw Error("RangeError", "Invalid String.repeat count");
        if (count == 0d || text.Length == 0) return TsValue.FromString("");
        if (count > int.MaxValue / text.Length) throw Error("RangeError", "Invalid String.repeat length");
        return TsValue.FromString(string.Create(text.Length * (int)count, text, static (destination, original) =>
        {
            for (int offset = 0; offset < destination.Length; offset += original.Length)
                original.AsSpan().CopyTo(destination[offset..]);
        }));
    }

    internal static TsValue Concat(TsValue[] args)
    {
        var result = new StringBuilder(Receiver(args));
        for (int i = 1; i < args.Length; i++) result.Append(Text(args[i]));
        return TsValue.FromString(result.ToString());
    }

    internal enum SearchKind { Includes, Starts, Ends, IndexOf, LastIndexOf }

    internal static TsValue Search(TsValue[] args, SearchKind kind)
    {
        string text = Receiver(args);
        var needleValue = Slot(args, 1);
        if (needleValue is TsRegexValue && kind is SearchKind.Includes or SearchKind.Starts or SearchKind.Ends)
            throw Error("TypeError", "A String search argument must not be a regular expression");

        string needle = Text(needleValue);
        var positionValue = Slot(args, 2);
        if (kind == SearchKind.LastIndexOf)
        {
            double position = Number(positionValue);
            int maxStart = text.Length - needle.Length;
            if (maxStart < 0) return TsValue.FromInt32(-1);
            int bound = double.IsNaN(position) ? maxStart : (int)Math.Clamp(Math.Truncate(position), 0d, maxStart);
            return TsValue.FromInt32(needle.Length == 0
                ? bound
                : text.AsSpan(0, bound + needle.Length).LastIndexOf(needle.AsSpan(), StringComparison.Ordinal));
        }
        if (kind == SearchKind.Ends)
        {
            int end = positionValue is TsVoid ? text.Length : Clamp(positionValue, text.Length);
            return TsValue.FromBool(text.AsSpan(0, end).EndsWith(needle.AsSpan(), StringComparison.Ordinal));
        }

        int start = Clamp(positionValue, text.Length);
        return kind switch
        {
            SearchKind.Starts => TsValue.FromBool(text.AsSpan(start).StartsWith(needle.AsSpan(), StringComparison.Ordinal)),
            SearchKind.Includes => TsValue.FromBool(needle.Length == 0 || text.IndexOf(needle, start, StringComparison.Ordinal) >= 0),
            _ => TsValue.FromInt32(needle.Length == 0 ? start : text.IndexOf(needle, start, StringComparison.Ordinal)),
        };
    }

    private static bool IsLiteralPrimitive(TsValue value) => value is TsStringValue or TsBoolValue or TsNull or TsVoid
        or TsInt32Value or TsInt64Value or TsUInt64Value or TsDecimalValue or TsFloat32Value or TsFloat64Value or TsBigIntValue;

    internal static TsValue Replace(TsValue[] args, bool all)
    {
        string text = Receiver(args);
        var needleValue = Slot(args, 1);
        var replacementValue = Slot(args, 2);
        bool primitivePair = IsLiteralPrimitive(needleValue) && IsLiteralPrimitive(replacementValue);
        string needle = primitivePair ? Text(needleValue) : Builtins.S(needleValue);

        // Keep both operands on the legacy conversion path when either is
        // outside the primitive pair. replaceAll's old CLR fallback treated
        // template tokens literally (and had its original operand arity).
        if (all && !primitivePair)
            return TsValue.FromString(text.Replace(needle, Builtins.S(args[2])));

        int match = needle.Length == 0 ? 0 : text.IndexOf(needle, StringComparison.Ordinal);
        if (match < 0) return TsValue.FromString(text);
        // Single replace still interprets template tokens after finding the
        // first match, including when this legacy path uses an object operand.
        string replacement = primitivePair ? Text(replacementValue) : Builtins.S(replacementValue);
        var result = new StringBuilder();
        int consumed = 0;
        while (true)
        {
            result.Append(text.AsSpan(consumed, match - consumed));
            consumed = match + needle.Length;
            AppendReplacement(result, replacement, text, match, consumed);
            if (!all || (needle.Length == 0 && match == text.Length)) break;
            match = needle.Length == 0 ? match + 1 : text.IndexOf(needle, consumed, StringComparison.Ordinal);
            if (match < 0) break;
        }
        result.Append(text.AsSpan(consumed));
        return TsValue.FromString(result.ToString());
    }

    private static void AppendReplacement(StringBuilder result, string template, string text, int match, int tail)
    {
        int literalStart = 0;
        for (int i = 0; i < template.Length - 1; i++)
        {
            if (template[i] != '$') continue;
            char token = template[i + 1];
            if (token is not ('$' or '&' or '`' or '\'')) continue;
            result.Append(template.AsSpan(literalStart, i - literalStart));
            switch (token)
            {
                case '$': result.Append('$'); break;
                case '&': result.Append(text.AsSpan(match, tail - match)); break;
                case '`': result.Append(text.AsSpan(0, match)); break;
                case '\'': result.Append(text.AsSpan(tail)); break;
            }
            literalStart = ++i + 1;
        }
        result.Append(template.AsSpan(literalStart));
    }

    internal static TsValue Pad(TsValue[] args, bool before)
    {
        string text = Receiver(args);
        double width = Integer(Slot(args, 1));
        if (width <= text.Length) return TsValue.FromString(text);
        var fillValue = Slot(args, 2);
        string fill = fillValue is TsVoid ? " " : Text(fillValue);
        if (fill.Length == 0) return TsValue.FromString(text);
        if (width > int.MaxValue) throw Error("RangeError", "Invalid String padding length");

        return TsValue.FromString(string.Create((int)width, (text, fill, before), static (destination, state) =>
        {
            int pad = destination.Length - state.text.Length;
            var part = state.before ? destination[..pad] : destination[state.text.Length..];
            state.text.AsSpan().CopyTo(state.before ? destination[pad..] : destination);
            for (int offset = 0; offset < part.Length;)
            {
                int count = Math.Min(state.fill.Length, part.Length - offset);
                state.fill.AsSpan(0, count).CopyTo(part[offset..]);
                offset += count;
            }
        }));
    }

    private static uint SplitLimit(TsValue value)
    {
        if (value is TsVoid) return uint.MaxValue;
        // These additional host integer/decimal carriers need not round through Double.
        if (value is TsUInt64Value u) return unchecked((uint)u.Value);
        if (value is TsInt64Value l) return unchecked((uint)l.Value);
        if (value is TsDecimalValue m)
        {
            const decimal modulus = 4294967296m;
            decimal remainder = decimal.Truncate(m.Value) % modulus;
            return (uint)(remainder < 0m ? remainder + modulus : remainder);
        }

        double number = Number(value);
        if (!double.IsFinite(number)) return 0;
        const double range = 4294967296d;
        double reduced = Math.Truncate(number) % range;
        return (uint)(reduced < 0d ? reduced + range : reduced);
    }

    internal static TsValue Split(TsValue[] args)
    {
        string text = Receiver(args);
        var separator = Slot(args, 1);
        // A nonprimitive separator keeps the old literal fallback, including
        // the original limit behavior and exact dense backing array capacity.
        if (!IsLiteralPrimitive(separator))
        {
            string delimiter = Builtins.S(separator);
            var legacyParts = delimiter.Length == 0
                ? text.Select(unit => unit.ToString()).ToArray()
                : text.Split(delimiter);
            var legacy = new TsArray(legacyParts.Length);
            foreach (var part in legacyParts) legacy.Add(TsValue.FromString(part));
            return new TsArrayValue(legacy);
        }

        uint limit = SplitLimit(Slot(args, 2));
        if (limit == 0) return new TsArrayValue(new TsArray(0));
        if (separator is TsVoid)
        {
            var whole = new TsArray(1);
            whole.Add(TsValue.FromString(text));
            return new TsArrayValue(whole);
        }

        string split = Text(separator);
        if (split.Length == 0)
        {
            int count = (int)Math.Min(limit, (uint)text.Length);
            var units = new TsArray(count);
            for (int i = 0; i < count; i++) units.Add(TsValue.FromString(text[i].ToString()));
            return new TsArrayValue(units);
        }

        var parts = new TsArray((int)Math.Min(limit, 4u));
        int start = 0;
        do
        {
            int match = text.IndexOf(split, start, StringComparison.Ordinal);
            if (match < 0)
            {
                parts.Add(TsValue.FromString(text[start..]));
                break;
            }
            parts.Add(TsValue.FromString(text[start..match]));
            start = match + split.Length;
        } while ((uint)parts.Count < limit);
        return new TsArrayValue(parts);
    }
}
