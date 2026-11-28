using System.Text;
using System.Text.RegularExpressions;
using TypeSharp.VM.Memory;

namespace TypeSharp.VM.Interpreter;

internal static class NativeRegex
{
    internal const string TestName = "RegExp::test";
    internal const string InvokeMemberReference = "RegExp::<invoke-member-reference>";
    internal const string ReadTypedTestReference = "RegExp::<typed-test-reference>";
    private const string LastIndex = "lastIndex";
    private const string FlagOrder = "gimsuy";
    private const string Word = "A-Za-z0-9_";
    private const string Digit = "0-9";
    private const string Space = @"\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
    private const string Line = @"\r\n\u2028\u2029";
    private static readonly TsFunctionValue TestReference = new(TestName);

    internal delegate TsValue InvokeOwnMethod(TsFunctionValue method, TsValue receiver);

    internal static bool HasProperty(string name) => name is LastIndex or "test";
    internal static bool IsCursorKey(TsValue key) => key is TsStringValue { Value: LastIndex };
    internal static bool IsIntrinsicReference(TsValue? value) => ReferenceEquals(value, TestReference);
    internal static TsValue ReadProperty(TsRegexValue regex, string name) => name switch
    {
        LastIndex => regex.ScriptLastIndex,
        "test" => TestReference,
        _ => TsValue.Null,
    };
    internal static TsValue ReadTypedReference(TsValue receiver) =>
        receiver is TsRegexValue ? TestReference : receiver;

    internal static TsValue TestWithReceiver(TsValue? receiver, TsValue[] positional, InvokeOwnMethod invoke) =>
        receiver is TsRegexValue regex
            ? Test(regex, positional.Length > 0 ? positional[0] : TsValue.Void, invoke)
            : throw Error("Receiver is not a RegExp");

    // Keep the legacy contextless Builtins.TryGet compatibility; it cannot call
    // script callbacks without a real module, execution context and caller.
    internal static TsValue TestBuiltin(TsValue[] args, InvokeOwnMethod? invoke = null)
    {
        if (args.Length == 0 || args[0] is not TsRegexValue regex)
            throw new InvalidOperationException("RegExp.test requires a RegExp receiver and input");
        var input = args.Length > 1 ? args[1] : TsValue.Void;
        return invoke is null ? TestContextless(regex, input) : Test(regex, input, invoke);
    }

    internal static TsValue Test(TsRegexValue regex, TsValue inputValue, InvokeOwnMethod invoke)
    {
        // Input may mutate lastIndex. Read the carrier only after converting the input;
        // throwing during either conversion must not introduce a cursor write.
        var input = StringText(inputValue, invoke);
        var cursor = Primitive(regex.ScriptLastIndex, stringHint: false, invoke);
        if (cursor is TsBigIntValue)
            throw Error("Cannot convert a BigInt value to a number");
        var number = CursorNumber(cursor);
        var start = number > 0 ? Math.Min(Math.Truncate(number), 9007199254740991d) : 0d;
        bool sticky = regex.Flags.Contains('y');
        var matcher = regex.ScriptMatcher;
        if (!sticky && !regex.Flags.Contains('g'))
            return TsValue.FromBool(matcher.IsMatch(input));
        return MatchAtCursor(regex, matcher, input, start, sticky);
    }

    private static TsValue TestContextless(TsRegexValue regex, TsValue inputValue)
    {
        var input = Builtins.S(inputValue);
        bool sticky = regex.Flags.Contains('y');
        if (!sticky && !regex.Flags.Contains('g'))
            return TsValue.FromBool(regex.Test(input));
        var number = CursorNumber(regex.ScriptLastIndex);
        return MatchAtCursor(regex, regex.Regex, input, number > 0 ? Math.Truncate(number) : 0, sticky);
    }

    private static TsValue MatchAtCursor(TsRegexValue regex, Regex matcher, string input, double start, bool sticky)
    {
        // Do not narrow to an integer before checking the UTF-16 input bound.
        if (start <= input.Length)
        {
            var match = matcher.Match(input, (int)start);
            if (match.Success && (!sticky || match.Index == (int)start))
            {
                regex.ScriptLastIndex = TsValue.FromFloat64(match.Index + match.Length);
                return TsValue.FromBool(true);
            }
        }
        regex.ScriptLastIndex = TsValue.FromFloat64(0);
        return TsValue.FromBool(false);
    }

    private static double CursorNumber(TsValue value) => value switch
    {
        TsInt32Value v => v.Value,
        TsInt64Value v => v.Value,
        TsUInt64Value v => v.Value,
        TsFloat32Value v => v.Value,
        TsFloat64Value v => v.Value,
        TsDecimalValue v => (double)v.Value,
        TsStringValue v => EcmaNumberText.Parse(v.Value),
        TsBoolValue v => v.Value ? 1 : 0,
        TsNull => 0,
        _ => double.NaN,
    };

    internal static string PropertyKey(TsValue value, InvokeOwnMethod invoke) =>
        value is TsStringValue text ? text.Value : StringText(value, invoke);

    private static string StringText(TsValue value, InvokeOwnMethod invoke) =>
        DefaultText(Primitive(value, stringHint: true, invoke));

    // Only the RegExp surface opts in. In particular, recursion through the
    // existing ArrayText helper does not canonicalize contained Regex wrappers.
    private static string DefaultText(TsValue value) =>
        value is TsRegexValue regex ? ScriptText(regex) : NativeString.RegExpDefaultText(value);

    private static bool IsPrimitive(TsValue v) => v is TsVoid or TsNull or TsBoolValue or
        TsInt32Value or TsInt64Value or TsUInt64Value or TsBigIntValue or TsFloat32Value or
        TsFloat64Value or TsDecimalValue or TsStringValue;

    private static TsValue Primitive(TsValue value, bool stringHint, InvokeOwnMethod invoke)
    {
        if (IsPrimitive(value))
            return value;
        if (value is TsObjectValue { Value.TypeName: "Object" } ordinary)
        {
            var first = stringHint ? "toString" : "valueOf";
            var second = stringHint ? "valueOf" : "toString";
            var result = TryOrdinaryMethod(first) ?? TryOrdinaryMethod(second);
            return result ?? throw Error("Cannot convert object to primitive value");

            TsValue? TryOrdinaryMethod(string name)
            {
                // Presence, not the carrier, decides whether the default is visible.
                if (!ordinary.Value.Fields.TryGetValue(name, out var slot))
                    return name == "toString" ? TsValue.FromString(DefaultText(ordinary)) : null;
                if (slot is not TsFunctionValue function)
                    return null;
                var converted = invoke(function, ordinary);
                return IsPrimitive(converted) ? converted : null;
            }
        }
        if (!stringHint && value is TsObjectValue date && NativeDate.TryGetTimestamp(date, out var timestamp))
            return TsValue.FromFloat64(timestamp);
        return TsValue.FromString(DefaultText(value));
    }

    private static bool RepresentedFlags(string flags)
    {
        int seen = 0;
        foreach (var flag in flags)
        {
            int bit = FlagOrder.IndexOf(flag);
            if (bit < 0 || (seen & (1 << bit)) != 0)
                return false;
            seen |= 1 << bit;
        }
        return true;
    }

    internal static string ScriptText(TsRegexValue regex)
    {
        if (!RepresentedFlags(regex.Flags))
            return regex.ToString();
        return $"/{regex.Pattern}/{new string(FlagOrder.Where(regex.Flags.Contains).ToArray())}";
    }

    internal static Regex CreateScriptMatcher(TsRegexValue regex)
    {
        if (!RepresentedFlags(regex.Flags) || !TryLowerPattern(regex.Pattern, regex.Flags, out var lowered)
            || lowered == regex.Pattern)
            return regex.Regex;
        return new Regex(lowered, regex.Regex.Options, regex.Regex.MatchTimeout);
    }

    private static string Predicate(string set, bool inverse = false) =>
        "(?-i:[" + (inverse ? "^" : "") + set + "])";

    // Not a regex grammar. If an unfamiliar construct, in-class inverse form,
    // or range-dependent shorthand could require semantic reconstruction, retain
    // the entire original matcher rather than applying a misleading partial rewrite.
    private static bool TryLowerPattern(string pattern, string flags, out string lowered)
    {
        lowered = pattern;
        bool inClass = false;
        bool multiline = flags.Contains('m');
        bool dotAll = flags.Contains('s');
        bool ignoreCase = flags.Contains('i');
        var result = new StringBuilder(pattern.Length);
        for (int i = 0; i < pattern.Length; ++i)
        {
            char token = pattern[i];
            if (token == '\\')
            {
                int start = i;
                if (++i == pattern.Length)
                    return false;
                char escape = pattern[i];
                string? set = char.ToLowerInvariant(escape) switch
                {
                    'd' => Digit,
                    's' => Space,
                    'w' => Word,
                    _ => null,
                };
                if (set is not null)
                {
                    bool inverse = char.IsUpper(escape);
                    if (!inClass)
                        result.Append(Predicate(set, inverse));
                    else
                    {
                        if (inverse || (ignoreCase && set == Word) ||
                            (start > 0 && pattern[start - 1] == '-') ||
                            (i + 1 < pattern.Length && pattern[i + 1] == '-'))
                            return false;
                        result.Append(set);
                    }
                    continue;
                }
                if (!inClass && escape is 'b' or 'B')
                {
                    string word = Predicate(Word);
                    result.Append(escape == 'b'
                        ? $"(?:(?<!{word})(?={word})|(?<={word})(?!{word}))"
                        : $"(?:(?<={word})(?={word})|(?<!{word})(?!{word}))");
                    continue;
                }
                if (char.IsAsciiDigit(escape))
                {
                    while (i + 1 < pattern.Length && char.IsAsciiDigit(pattern[i + 1]))
                        ++i;
                }
                else if (escape is 'x' or 'u' or 'c')
                {
                    int count = escape == 'x' ? 2 : escape == 'u' ? 4 : 1;
                    if (i + count >= pattern.Length)
                        return false;
                    i += count;
                }
                else if (char.IsAsciiLetter(escape) && escape is not ('b' or 'f' or 'n' or 'r' or 't' or 'v'))
                {
                    // E.g. named backrefs, property escapes and .NET-only anchors.
                    return false;
                }
                result.Append(pattern, start, i - start + 1);
                continue;
            }
            if (token == '[')
            {
                if (inClass)
                    return false;
                inClass = true;
            }
            else if (token == ']' && inClass)
                inClass = false;
            else if (!inClass)
            {
                if (token == '(' && i + 1 < pattern.Length && pattern[i + 1] == '?' &&
                    (i + 2 == pattern.Length || pattern[i + 2] is not (':' or '=' or '!')))
                    return false;
                if (token == '^')
                {
                    result.Append(multiline ? @"(?:\A|(?<=[" + Line + "]))" : @"\A");
                    continue;
                }
                if (token == '$')
                {
                    result.Append(multiline ? @"(?:\z|(?=[" + Line + "]))" : @"\z");
                    continue;
                }
                if (token == '.' && !dotAll)
                {
                    result.Append("[^").Append(Line).Append(']');
                    continue;
                }
            }
            result.Append(token);
        }
        if (inClass)
            return false;
        lowered = result.ToString();
        return true;
    }

    internal static TsThrownException RejectCursorDeletion() => Error("Cannot delete RegExp.lastIndex");

    private static TsThrownException Error(string message)
    {
        var error = new TsObject("TypeError");
        error.SetField("name", TsValue.FromString("TypeError"));
        error.SetField("message", TsValue.FromString(message));
        return new TsThrownException(TsValue.FromObject(error));
    }
}
