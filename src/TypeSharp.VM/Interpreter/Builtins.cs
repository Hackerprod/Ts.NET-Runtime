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
                TsInt32Value or TsInt64Value or TsUInt64Value => true,
                TsFloat32Value f => float.IsFinite(f.Value) && f.Value == MathF.Truncate(f.Value),
                TsFloat64Value d => double.IsFinite(d.Value) && d.Value == Math.Truncate(d.Value),
                _ => false
            }),
            ["Number::isFinite"] = args => Bool(args[0] is TsInt32Value or TsInt64Value or TsUInt64Value ||
                (args[0] is TsFloat64Value fd && double.IsFinite(fd.Value)) ||
                (args[0] is TsFloat32Value ff && float.IsFinite(ff.Value))),
            ["Number::isNaN"] = args => Bool(
                (args[0] is TsFloat64Value fd && double.IsNaN(fd.Value)) ||
                (args[0] is TsFloat32Value ff && float.IsNaN(ff.Value))),
            ["Number::parseFloat"] = args => Num(double.TryParse(S(args[0]),
                System.Globalization.CultureInfo.InvariantCulture, out var pf) ? pf : double.NaN),
            ["Number::parseInt"] = args => Num(long.TryParse(S(args[0]), out var pi) ? pi : double.NaN),

            // ── console ──
            ["console::log"] = args => { Console.WriteLine(string.Join(" ", args.Select(a => a.ToString()))); return TsValue.Null; },
            ["console::error"] = args => { Console.Error.WriteLine(string.Join(" ", args.Select(a => a.ToString()))); return TsValue.Null; },
            ["console::warn"] = args => { Console.Error.WriteLine(string.Join(" ", args.Select(a => a.ToString()))); return TsValue.Null; },

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
        TsFloat32Value f => f.Value,
        TsFloat64Value d => d.Value,
        TsDecimalValue m => (double)m.Value,
        TsBoolValue b => b.Value ? 1 : 0,
        _ => double.NaN
    };

    private static int I(TsValue v) => (int)D(v);

    private static string S(TsValue v) => v is TsStringValue s ? s.Value : v.ToString() ?? "";

    private static TsArray Arr(TsValue v) => v is TsArrayValue a
        ? a.Value
        : throw new InvalidOperationException("Receiver is not an array");

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
}
