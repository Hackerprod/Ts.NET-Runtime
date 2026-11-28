using TypeSharp.VM.Memory;

namespace TypeSharp.VM.Interpreter;

internal static class NativeRegex
{
    internal const string TestName = "RegExp::test";
    internal const string InvokeMemberReference = "RegExp::<invoke-member-reference>";
    internal const string ReadTypedTestReference = "RegExp::<typed-test-reference>";
    private const string LastIndex = "lastIndex";
    private static readonly TsFunctionValue TestReference = new(TestName);

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

    internal static TsValue TestWithReceiver(TsValue? receiver, TsValue[] positional) =>
        receiver is TsRegexValue regex
            ? Test(regex, positional.Length > 0 ? positional[0] : TsValue.Void)
            : throw Error("Receiver is not a RegExp");

    internal static TsValue Test(TsRegexValue regex, TsValue inputValue)
    {
        var input = Builtins.S(inputValue);
        bool sticky = regex.Flags.Contains('y');
        if (!sticky && !regex.Flags.Contains('g'))
            return TsValue.FromBool(regex.Test(input));

        double number = CursorNumber(regex.ScriptLastIndex);
        // Do not narrow to an integer before checking the UTF-16 input bound.
        double start = number > 0 ? Math.Truncate(number) : 0;
        if (start <= input.Length)
        {
            var match = regex.Regex.Match(input, (int)start);
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

    internal static TsThrownException RejectCursorDeletion() => Error("Cannot delete RegExp.lastIndex");

    private static TsThrownException Error(string message)
    {
        var error = new TsObject("TypeError");
        error.SetField("name", TsValue.FromString("TypeError"));
        error.SetField("message", TsValue.FromString(message));
        return new TsThrownException(TsValue.FromObject(error));
    }
}
