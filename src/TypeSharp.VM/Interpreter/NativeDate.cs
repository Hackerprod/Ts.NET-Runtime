using System.Globalization;
using System.Numerics;
using TypeSharp.VM.Memory;

namespace TypeSharp.VM.Interpreter;

// Date conversions are deliberately local. The runtime's shared Number,
// BigInt, object/operator and CLR interop policies are not Date conversions.
internal static class NativeDate
{
    private const double ClipLimit = 8_640_000_000_000_000d;
    private const long DayMilliseconds = 86_400_000;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal enum UtcPart { Year, Month, Date, Day, Hours, Minutes, Seconds, Milliseconds }

    internal delegate TsValue InvokeOwnMethod(TsFunctionValue method, TsValue receiver);

    // The same descriptors back the existing qualified builtin entries, native
    // property reads and callable-value dispatch. Reference identity, not a
    // TypeName or a function spelling, identifies a read intrinsic.
    internal sealed class Method(string selector, Func<TsValue, TsValue> implementation)
    {
        internal string Selector { get; } = selector;
        internal TsFunctionValue Reference { get; } = new("Date::" + selector);
        internal string QualifiedName => Reference.FunctionName;
        internal TsValue Invoke(TsValue receiver) => implementation(receiver);
    }

    internal static readonly Method GetTimeMethod = new("getTime", Timestamp);
    internal static readonly Method ValueOfMethod = new("valueOf", Timestamp);
    internal static readonly Method ToIsoStringMethod = new("toISOString", ToIsoString);
    internal static readonly Method GetUtcYearMethod = new("getUTCFullYear", receiver => GetUtc(receiver, UtcPart.Year));
    internal static readonly Method GetUtcMonthMethod = new("getUTCMonth", receiver => GetUtc(receiver, UtcPart.Month));
    internal static readonly Method GetUtcDateMethod = new("getUTCDate", receiver => GetUtc(receiver, UtcPart.Date));
    internal static readonly Method GetUtcDayMethod = new("getUTCDay", receiver => GetUtc(receiver, UtcPart.Day));
    internal static readonly Method GetUtcHoursMethod = new("getUTCHours", receiver => GetUtc(receiver, UtcPart.Hours));
    internal static readonly Method GetUtcMinutesMethod = new("getUTCMinutes", receiver => GetUtc(receiver, UtcPart.Minutes));
    internal static readonly Method GetUtcSecondsMethod = new("getUTCSeconds", receiver => GetUtc(receiver, UtcPart.Seconds));
    internal static readonly Method GetUtcMillisecondsMethod = new("getUTCMilliseconds", receiver => GetUtc(receiver, UtcPart.Milliseconds));

    private static readonly Method[] Methods =
    [
        GetTimeMethod, ValueOfMethod, ToIsoStringMethod, GetUtcYearMethod, GetUtcMonthMethod,
        GetUtcDateMethod, GetUtcDayMethod, GetUtcHoursMethod, GetUtcMinutesMethod,
        GetUtcSecondsMethod, GetUtcMillisecondsMethod,
    ];

    private static readonly Dictionary<string, Method> ByQualifiedName =
        Methods.ToDictionary(method => method.QualifiedName, StringComparer.Ordinal);
    private static readonly Dictionary<string, Method> BySelector =
        Methods.ToDictionary(method => method.Selector, StringComparer.Ordinal);

    internal static TsValue ReadProperty(TsObjectValue date, string selector) =>
        date.Value.Fields.TryGetValue(selector, out var slot) ? slot
        : BySelector.TryGetValue(selector, out var method) ? method.Reference
        : TsValue.Void;

    internal static bool TryGetOwnMethod(TsValue receiver, string qualifiedName, out TsValue slot)
    {
        if (receiver is TsObjectValue { Value.NativeDateTimestamp: not null } date &&
            ByQualifiedName.TryGetValue(qualifiedName, out var method) &&
            date.Value.Fields.TryGetValue(method.Selector, out var own))
        {
            slot = own;
            return true;
        }
        slot = TsValue.Void;
        return false;
    }

    internal static Method? GetIntrinsicMethod(TsValue? value) =>
        value is TsFunctionValue function && function.FunctionName.StartsWith("Date::", StringComparison.Ordinal) &&
        ByQualifiedName.TryGetValue(function.FunctionName, out var method) &&
        ReferenceEquals(function, method.Reference) ? method : null;

    internal static TsFunctionValue RequireOwnCallable(TsValue slot) =>
        slot as TsFunctionValue ?? throw ScriptError("TypeError", "Date member is not callable");

    internal static TsObjectValue Construct(ExecutionContext context, ReadOnlySpan<TsValue> args, InvokeOwnMethod invoke)
    {
        double timestamp = args.Length switch
        {
            0 => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            1 => FromSingle(context, args[0], invoke),
            _ => FromComponents(context, args, invoke),
        };
        return new TsObjectValue(context.Heap.AllocateNativeDate(TimeClip(timestamp)));
    }

    internal static bool TryGetTimestamp(TsValue value, out double timestamp)
    {
        if (value is TsObjectValue { Value.NativeDateTimestamp: double nativeTimestamp })
        {
            timestamp = nativeTimestamp;
            return true;
        }
        timestamp = double.NaN;
        return false;
    }

    internal static TsValue Timestamp(TsValue receiver) => TsValue.FromFloat64(RequireTimestamp(receiver));

    internal static TsValue ToIsoString(TsValue receiver)
    {
        double timestamp = RequireTimestamp(receiver);
        if (double.IsNaN(timestamp))
            throw ScriptError("RangeError", "Invalid Date");
        return TsValue.FromString(FormatIso(timestamp));
    }

    internal static TsValue GetUtc(TsValue receiver, UtcPart part)
    {
        double timestamp = RequireTimestamp(receiver);
        if (double.IsNaN(timestamp))
            return TsValue.FromFloat64(double.NaN);
        var calendar = Decompose((long)timestamp);
        return TsValue.FromFloat64(part switch
        {
            UtcPart.Year => calendar.Year,
            UtcPart.Month => calendar.Month - 1,
            UtcPart.Date => calendar.Date,
            UtcPart.Day => calendar.Day,
            UtcPart.Hours => calendar.Hours,
            UtcPart.Minutes => calendar.Minutes,
            UtcPart.Seconds => calendar.Seconds,
            _ => calendar.Milliseconds,
        });
    }

    private static double RequireTimestamp(TsValue value) =>
        TryGetTimestamp(value, out double timestamp)
            ? timestamp
            : throw ScriptError("TypeError", "Receiver is not a Date");

    private static TsThrownException ScriptError(string name, string message)
    {
        var error = new TsObject(name);
        error.SetField("name", TsValue.FromString(name));
        error.SetField("message", TsValue.FromString(message));
        return new TsThrownException(TsValue.FromObject(error));
    }

    private static double TimeClip(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > ClipLimit)
            return double.NaN;
        var clipped = Math.Truncate(value);
        return clipped == 0d ? 0d : clipped;
    }

    private static double FromSingle(ExecutionContext context, TsValue value, InvokeOwnMethod invoke)
    {
        // The native copy path preserves an invalid timestamp too, without
        // looking at any ordinary properties or inheriting them on the copy.
        if (TryGetTimestamp(value, out double timestamp))
            return timestamp;
        return ConvertArgument(context, value, component: false, invoke);
    }

    private static double ConvertArgument(ExecutionContext context, TsValue value, bool component, InvokeOwnMethod invoke)
    {
        if (value is TsArrayValue or TsUint8ArrayValue)
            return DateJoin.Convert(context, value, component, invoke);

        if (value is TsObjectValue obj && IsConversionObject(obj))
        {
            var primitive = ConvertObject(context, obj, stringHint: false, invoke);
            // The default ordinary and native Date texts are non-numeric; the
            // ordinary default is not a parseable date either. For native
            // Dates this branch is only a component fallback, not the copy.
            if (primitive is null)
                return double.NaN;
            value = primitive;
        }

        return value is TsStringValue text
            ? ReadText(context, text.Value, component)
            : FromPrimitiveNumber(value);
    }

    private static bool IsConversionObject(TsObjectValue value) =>
        value.Value.TypeName == "Object" || value.Value.NativeDateTimestamp.HasValue;

    private static bool IsPrimitive(TsValue value) => value is TsVoid or TsNull or TsBoolValue or
        TsInt32Value or TsInt64Value or TsUInt64Value or TsBigIntValue or TsFloat32Value or
        TsFloat64Value or TsDecimalValue or TsStringValue;

    // C# null denotes accepted intrinsic default text. Script null and
    // undefined are real TsValue instances, distinct from that default.
    private static TsValue? ConvertObject(ExecutionContext context, TsObjectValue receiver,
        bool stringHint, InvokeOwnMethod invoke, long? joinAllocated = null)
    {
        for (int i = 0; i != 2; ++i)
        {
            bool text = (i == 0) == stringHint;
            // Read the second slot only if needed: the first hook can modify it.
            if (!receiver.Value.Fields.TryGetValue(text ? "toString" : "valueOf", out var slot))
            {
                if (text)
                    return null;
                if (TryGetTimestamp(receiver, out var timestamp))
                    return TsValue.FromFloat64(timestamp);
                // Default ordinary valueOf returns the receiver (not a primitive).
                continue;
            }

            if (slot is not TsFunctionValue method)
                continue; // An own noncallable shadows the intrinsic default.
            var result = invoke(method, receiver);
            // Hooks run with the same heap and may allocate or reduce the
            // remaining budget. Check before trying another method or appending.
            if (joinAllocated is long pending)
                CheckJoinAvailable(context, pending, 0);
            if (IsPrimitive(result))
                return result;
        }
        throw ScriptError("TypeError", "Cannot convert object to primitive value");
    }

    private static double FromComponents(ExecutionContext context, ReadOnlySpan<TsValue> args, InvokeOwnMethod invoke)
    {
        Span<double> values = stackalloc double[7];
        values.Clear();
        values[2] = 1;
        bool invalid = false;
        // Do not short-circuit a nonfinite component: later conversions can
        // still run callbacks or throw. Extra arguments have already evaluated.
        for (int i = 0; i < Math.Min(values.Length, args.Length); ++i)
        {
            var n = ConvertArgument(context, args[i], component: true, invoke);
            invalid |= !double.IsFinite(n);
            values[i] = Math.Truncate(n);
        }
        if (invalid)
            return double.NaN;
        // Each finite binary64 input has at most 1024 magnitude bits. Widen
        // before normalization/rollover so independently signed parts may
        // cancel before TimeClip without intermediate integer wraparound.
        BigInteger year = new(values[0]);
        if (year >= 0 && year <= 99)
            year += 1900;
        BigInteger monthOffset = FloorDiv(new BigInteger(values[1]), 12, out int month);
        BigInteger days = DaysFromCivil(year + monthOffset, month + 1) + new BigInteger(values[2]) - 1;
        BigInteger millis = ((days * 24 + new BigInteger(values[3])) * 60 + new BigInteger(values[4])) * 60
            + new BigInteger(values[5]);
        return (double)(millis * 1_000 + new BigInteger(values[6]));
    }

    private static double FromPrimitiveNumber(TsValue value) => value switch
    {
        TsNull => 0d,
        TsBoolValue boolean => boolean.Value ? 1d : 0d,
        TsInt32Value number => number.Value,
        TsInt64Value number => number.Value,
        TsUInt64Value number => number.Value,
        TsFloat32Value number => number.Value,
        TsFloat64Value number => number.Value,
        TsDecimalValue number => (double)number.Value,
        TsBigIntValue => throw ScriptError("TypeError", "Cannot convert a BigInt value to a number"),
        _ => double.NaN,
    };

    private static double ReadText(ExecutionContext context, ReadOnlySpan<char> text, bool component)
    {
        Charge(context, 1L + text.Length);
        return component ? EcmaNumberText.Parse(text) : ParseDateText(text);
    }

    private static double ParseDateText(ReadOnlySpan<char> text)
    {
        if (TryParseIso(text, out double result))
            return result;
        // Do not let permissive CLR legacy parsing rescue a malformed ISO
        // date-time, including one with whitespace outside its raw spelling.
        if (HasIsoTimeCore(text))
            return double.NaN;
        return DateTimeOffset.TryParse(text, Invariant,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var legacy)
            ? legacy.ToUnixTimeMilliseconds()
            : double.NaN;
    }

    private static bool TryParseIso(ReadOnlySpan<char> text, out double result)
    {
        result = double.NaN;
        int cursor = 0;
        bool signed = !text.IsEmpty && text[0] is '+' or '-';
        bool negative = signed && text[cursor] == '-';
        if (signed) cursor++;
        if (!ReadDigits(text, ref cursor, signed ? 6 : 4, out int year))
            return false;
        int month = 1, day = 1;
        if (Take(text, ref cursor, '-'))
        {
            if (!ReadDigits(text, ref cursor, 2, out month))
                return false;
            if (Take(text, ref cursor, '-') && !ReadDigits(text, ref cursor, 2, out day))
                return false;
        }

        int hour = 0, minute = 0, second = 0, millis = 0, offset = 0;
        bool nonzeroFraction = false;
        if (TakeEither(text, ref cursor, 'T', 't'))
        {
            if (!ReadDigits(text, ref cursor, 2, out hour) || !Take(text, ref cursor, ':')
                || !ReadDigits(text, ref cursor, 2, out minute))
                return true;
            if (Take(text, ref cursor, ':'))
            {
                if (!ReadDigits(text, ref cursor, 2, out second))
                    return true;
                if (Take(text, ref cursor, '.'))
                {
                    int start = cursor;
                    while (cursor < text.Length && char.IsAsciiDigit(text[cursor]))
                    {
                        int digit = text[cursor] - '0';
                        nonzeroFraction |= digit != 0;
                        if (cursor - start < 3)
                            millis = millis * 10 + digit;
                        cursor++;
                    }
                    int digits = cursor - start;
                    if (digits == 0)
                        return true;
                    for (; digits < 3; ++digits)
                        millis *= 10;
                }
            }
            if (cursor < text.Length && !TakeEither(text, ref cursor, 'Z', 'z'))
            {
                int sign = text[cursor++] switch { '+' => 1, '-' => -1, _ => 0 };
                if (sign == 0 || !ReadDigits(text, ref cursor, 2, out int offsetHour))
                    return true;
                Take(text, ref cursor, ':');
                if (!ReadDigits(text, ref cursor, 2, out int offsetMinute)
                    || offsetHour > 23 || offsetMinute > 59)
                    return true;
                offset = sign * (offsetHour * 60 + offsetMinute);
            }
            if (cursor != text.Length)
                return true;
        }
        else if (cursor != text.Length)
        {
            return false;
        }

        if ((negative && year == 0) || month is < 1 or > 12 || day is < 1 or > 31
            || hour > 24 || minute > 59 || second > 59
            || (hour == 24 && (minute != 0 || second != 0 || nonzeroFraction)))
            return true;
        if (negative) year = -year;
        // Six-digit years guarantee the civil result fits Int64. Apply the
        // offset before TimeClip; a local time outside the range may be rescued.
        long days = (long)DaysFromCivil(year, month) + day - 1;
        result = days * DayMilliseconds + ((hour * 60L + minute - offset) * 60 + second) * 1_000 + millis;
        return true;
    }

    private static bool HasIsoTimeCore(ReadOnlySpan<char> text)
    {
        while (!text.IsEmpty && Builtins.IsEcmaTrimWhitespace(text[0])) text = text[1..];
        if (text.IsEmpty) return false;
        bool signed = text[0] is '+' or '-';
        int i = signed ? 1 : 0;
        if (!ReadDigits(text, ref i, signed ? 6 : 4, out _))
            return false;
        // Inspect plausible date cores, even when a component's width or a
        // separator is wrong. A legacy space-delimited time is not a T core.
        while (i < text.Length && (text[i] == '-' || char.IsAsciiDigit(text[i]))) i++;
        while (i < text.Length && Builtins.IsEcmaTrimWhitespace(text[i])) i++;
        return i < text.Length && text[i] is 'T' or 't';
    }

    private static bool ReadDigits(ReadOnlySpan<char> text, ref int cursor, int width, out int digits)
    {
        digits = 0;
        if (text.Length - cursor < width)
            return false;
        for (int i = 0; i < width; ++i)
        {
            var ch = text[cursor + i];
            if (!char.IsAsciiDigit(ch)) return false;
            digits = digits * 10 + ch - '0';
        }
        cursor += width;
        return true;
    }

    private static bool Take(ReadOnlySpan<char> text, ref int cursor, char terminal) =>
        TakeEither(text, ref cursor, terminal, terminal);

    private static bool TakeEither(ReadOnlySpan<char> text, ref int cursor, char one, char two)
    {
        if (cursor >= text.Length || (text[cursor] != one && text[cursor] != two)) return false;
        cursor++;
        return true;
    }

    private static BigInteger FloorDiv(BigInteger value, int modulus, out int remainder)
    {
        var quotient = BigInteger.DivRem(value, modulus, out var mod);
        remainder = (int)mod;
        if (remainder >= 0) return quotient;
        remainder += modulus;
        return quotient - 1;
    }

    // Move Jan/Feb to the end of a March-based year, then index the
    // 146097 days of a Gregorian 400-year era relative to Unix epoch.
    private static BigInteger DaysFromCivil(BigInteger year, int month)
    {
        if (month <= 2) year--;
        var era = FloorDiv(year, 400, out int yearOfEra);
        int marchMonth = month + (month > 2 ? -3 : 9);
        int dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + (153 * marchMonth + 2) / 5;
        return era * 146_097 + dayOfEra - 719_468;
    }

    private readonly record struct UtcCalendar(int Year, int Month, int Date, int Day,
        int Hours, int Minutes, int Seconds, int Milliseconds);

    private static UtcCalendar Decompose(long millis)
    {
        long days = Math.DivRem(millis, DayMilliseconds, out long time);
        if (time < 0)
        {
            days--;
            time += DayMilliseconds;
        }

        long civil = days + 719_468;
        long era = Math.DivRem(civil, 146_097, out long eraDay);
        if (eraDay < 0)
        {
            era--;
            eraDay += 146_097;
        }
        int dayOfEra = (int)eraDay;
        int yearOfEra = (dayOfEra - dayOfEra / 1_460 + dayOfEra / 36_524 - dayOfEra / 146_096) / 365;
        int year = (int)(yearOfEra + era * 400);
        int dayOfYear = dayOfEra - (365 * yearOfEra + yearOfEra / 4 - yearOfEra / 100);
        int marchMonth = (5 * dayOfYear + 2) / 153;
        int month = marchMonth + (marchMonth < 10 ? 3 : -9);
        int date = dayOfYear - (153 * marchMonth + 2) / 5 + 1;
        if (month <= 2) year++;
        int weekday = (int)((days + 4) % 7);
        if (weekday < 0) weekday += 7;
        return new(year, month, date, weekday, (int)(time / 3_600_000),
            (int)(time / 60_000 % 60), (int)(time / 1_000 % 60), (int)(time % 1_000));
    }

    internal static string FormatIso(double timestamp)
    {
        var utc = Decompose((long)timestamp);
        string year = utc.Year is >= 0 and <= 9999
            ? utc.Year.ToString("D4", Invariant)
            : string.Create(Invariant, $"{(utc.Year < 0 ? '-' : '+')}{Math.Abs(utc.Year):D6}");
        return string.Create(Invariant,
            $"{year}-{utc.Month:D2}-{utc.Date:D2}T{utc.Hours:D2}:{utc.Minutes:D2}:{utc.Seconds:D2}.{utc.Milliseconds:D3}Z");
    }

    private static void Charge(ExecutionContext context, long work)
    {
        context.CheckCancellation();
        long used = context.InstructionCount, limit = context.Limits.MaximumInstructions;
        if (used > limit || work > limit - used)
            throw new InvalidOperationException("Execution limit exceeded");
        context.InstructionCount = used + work;
    }

    private static void CheckJoinAvailable(ExecutionContext context, long allocated, long bytes)
    {
        long limit = Math.Min(context.Heap.MaxBytes, context.Limits.MaximumMemoryBytes);
        long used = context.Heap.LogicalBytes;
        if (used > limit || allocated > limit - used || bytes > limit - used - allocated)
            throw new InvalidOperationException("Memory limit exceeded while joining Date argument");
    }

    // Join only represented arrays; only ordinary/native-Date leaves use hooks.
    // Keep yielded characters in a bounded buffer and parse the span directly:
    // no final copied string, recursion, or unmetered active traversal set.
    private ref struct DateJoin(ExecutionContext context, InvokeOwnMethod invoke)
    {
        private const int AllocationOverhead = 64;
        private const int FrameBytes = 24; // reference + two 32-bit fields (16 bytes with 64-bit alignment), conservatively padded
        private struct Frame(TsArray array, int limit)
        {
            internal readonly TsArray Array = array;
            internal readonly int Limit = limit;
            internal int Index = 0;
        }

        private char[]? _chars;
        private Frame[]? _active;
        private int _length, _depth;
        // Count all capacity allocations, not just the live arrays, so even
        // old backing arrays awaiting GC remain inside the available budget.
        private long _allocated;

        internal static double Convert(ExecutionContext context, TsValue value, bool component, InvokeOwnMethod invoke)
        {
            var writer = new DateJoin(context, invoke);
            writer.AppendValue(value);
            while (writer._depth != 0)
            {
                TsValue element;
                {
                    ref var frame = ref writer._active![writer._depth - 1];
                    Charge(context, 1);
                    if (frame.Index == frame.Limit)
                    {
                        frame = default;
                        writer._depth--;
                        continue;
                    }
                    int position = frame.Index;
                    if (position != 0) writer.Append(',');
                    element = frame.Array.Get(position);
                    frame.Index = position + 1;
                }
                // No frame reference survives a hook or nested frame growth.
                writer.AppendValue(element);
            }
            return ReadText(context, writer._chars.AsSpan(0, writer._length), component);
        }

        private void AppendValue(TsValue value)
        {
            Charge(context, 1);
            switch (value)
            {
                case TsNull or TsVoid:
                    return;
                case TsArrayValue nested:
                    for (int i = _depth - 1; i >= 0; --i)
                    {
                        Charge(context, 1);
                        if (ReferenceEquals(_active![i].Array, nested.Value))
                            return; // Cycles contribute empty text only on this path.
                    }
                    int limit = nested.Value.Count;
                    if (_depth == (_active?.Length ?? 0))
                    {
                        int capacity = Growth(_depth, _depth + 1);
                        Reserve(AllocationOverhead + (long)capacity * FrameBytes);
                        Charge(context, _depth);
                        Array.Resize(ref _active, capacity);
                    }
                    _active![_depth++] = new Frame(nested.Value, limit);
                    return;
                case TsUint8ArrayValue bytes:
                    for (int i = 0; i < bytes.Length; ++i)
                    {
                        Charge(context, 1);
                        if (i != 0) Append(',');
                        AppendNumber(bytes.Get(i));
                    }
                    return;
                case TsObjectValue obj when IsConversionObject(obj):
                    var primitive = ConvertObject(context, obj, stringHint: true, invoke, _allocated);
                    if (primitive is not null)
                        AppendScalar(primitive);
                    else if (TryGetTimestamp(obj, out var timestamp))
                        AppendNativeDate(timestamp);
                    else
                        Append("[object Object]");
                    return;
                default:
                    AppendScalar(value);
                    return;
            }
        }

        private void AppendScalar(TsValue value)
        {
            switch (value)
            {
                // A hook's accepted null/undefined is text. Raw array slots
                // already took the separate empty branch in AppendValue.
                case TsNull: Append("null"); return;
                case TsVoid: Append("undefined"); return;
                case TsStringValue text: Append(text.Value); return;
                case TsBoolValue boolean: Append(boolean.Value ? "true" : "false"); return;
                case TsInt32Value n: AppendNumber(n.Value); return;
                case TsInt64Value n: AppendNumber(n.Value); return;
                case TsUInt64Value n: AppendNumber(n.Value); return;
                case TsFloat32Value n: AppendDouble(n.Value); return;
                case TsFloat64Value n: AppendDouble(n.Value); return;
                case TsDecimalValue n: AppendDouble((double)n.Value); return;
                case TsBigIntValue n: AppendBigInt(n.Value); return;
                default: Append("[object Object]"); return;
            }
        }

        private void AppendNativeDate(double timestamp)
        {
            if (double.IsNaN(timestamp))
            {
                Append("Invalid Date");
                return;
            }

            // Date's default display exposes only its UTC civil second in
            // this runtime. Project that second as internal parseable text,
            // not as a public toString or a shortcut around array joining.
            // Decompose folds pre-epoch remainders into the previous day.
            Charge(context, 32);
            var utc = Decompose((long)timestamp);
            Span<char> text = stackalloc char[23]; // longest signed-year projection
            int pos = 0;
            if (utc.Year is >= 0 and <= 9999)
            {
                WriteDigits(text, ref pos, utc.Year, 4);
            }
            else
            {
                text[pos++] = utc.Year < 0 ? '-' : '+';
                WriteDigits(text, ref pos, Math.Abs(utc.Year), 6);
            }
            text[pos++] = '-';
            WriteDigits(text, ref pos, utc.Month, 2);
            text[pos++] = '-';
            WriteDigits(text, ref pos, utc.Date, 2);
            text[pos++] = 'T';
            WriteDigits(text, ref pos, utc.Hours, 2);
            text[pos++] = ':';
            WriteDigits(text, ref pos, utc.Minutes, 2);
            text[pos++] = ':';
            WriteDigits(text, ref pos, utc.Seconds, 2);
            text[pos++] = 'Z';
            Append(text[..pos]); // existing copy/work and pre-allocation checks
        }

        private static void WriteDigits(Span<char> output, ref int pos, int value, int width)
        {
            for (int i = pos + width - 1; i >= pos; --i)
            {
                output[i] = (char)('0' + value % 10);
                value /= 10;
            }
            pos += width;
        }

        private void AppendDouble(double value) => AppendNumber(value == 0d ? 0d : value, "R");

        private void AppendNumber<T>(T value, string? format = null) where T : ISpanFormattable
        {
            Span<char> text = stackalloc char[64];
            if (!value.TryFormat(text, out int written, format, Invariant))
                throw new InvalidOperationException("Native Date numeric text exceeded its fixed buffer");
            Append(text[..written]);
        }

        private void AppendBigInt(BigInteger value)
        {
            long bits = value.GetBitLength();
            if (bits > int.MaxValue - 3)
                throw new InvalidOperationException("Execution limit exceeded");
            // Decimal formatting can need scratch storage and work quadratic
            // in the limb count. Check both before CLR formats the value.
            long digitsBound = bits / 3 + 3;
            Charge(context, bits + 1 + (bits / 32) * (bits / 32));
            CheckAvailable(AllocationOverhead + 32L * digitsBound);
            EnsureChars((int)digitsBound, AllocationOverhead + 32L * digitsBound);
            if (!value.TryFormat(_chars.AsSpan(_length), out int written, default, Invariant))
                throw new InvalidOperationException("Native Date BigInt text exceeded its checked bound");
            Charge(context, written);
            _length += written;
        }

        private void Append(char character)
        {
            Charge(context, 1);
            EnsureChars(1);
            _chars![_length++] = character;
        }

        private void Append(scoped ReadOnlySpan<char> text)
        {
            Charge(context, text.Length);
            if (text.IsEmpty) return;
            EnsureChars(text.Length);
            text.CopyTo(_chars.AsSpan(_length));
            _length += text.Length;
        }

        private void EnsureChars(int additional, long scratch = 0)
        {
            long needed = (long)_length + additional;
            if (needed <= (_chars?.Length ?? 0)) return;
            if (needed > Array.MaxLength)
                throw new InvalidOperationException("Memory limit exceeded while joining Date argument");
            int capacity = Growth(_chars?.Length ?? 0, (int)needed);
            Reserve(AllocationOverhead + (long)capacity * sizeof(char), scratch);
            Charge(context, _length);
            Array.Resize(ref _chars, capacity);
        }

        private static int Growth(int old, int minimum) =>
            (int)Math.Max(minimum, Math.Min(Array.MaxLength, Math.Max(8L, (long)old * 2)));

        private void Reserve(long bytes, long scratch = 0)
        {
            CheckAvailable(bytes + scratch);
            _allocated += bytes;
        }

        private void CheckAvailable(long bytes) => CheckJoinAvailable(context, _allocated, bytes);
    }
}
