using TypeSharp.VM.Memory;

namespace TypeSharp.VM.Interpreter;

internal static class NativeSet
{
    internal const string InvokeMemberReference = "Set::<invoke-member-reference>";
    internal const string ForEach = "Set::forEach";
    internal const string ReadonlySizeMessage = "Cannot assign to readonly Set.size";
    private const string Prefix = "Set::";
    private static readonly TsFunctionValue Values = new("Set::values");
    private static readonly Dictionary<string, TsFunctionValue> Methods = new(StringComparer.Ordinal)
    {
        ["add"] = new("Set::add"),
        ["has"] = new("Set::has"),
        ["delete"] = new("Set::delete"),
        ["clear"] = new("Set::clear"),
        ["keys"] = Values,
        ["values"] = Values,
        ["entries"] = new("Set::entries"),
        ["forEach"] = new(ForEach),
    };

    internal static bool IsMethod(string name) => GetReference(name) is not null;

    internal static TsFunctionValue? GetReference(string name) =>
        name.StartsWith(Prefix, StringComparison.Ordinal)
            ? Methods.GetValueOrDefault(name[Prefix.Length..])
            : null;

    internal static bool IsIntrinsicReference(TsValue value) =>
        value is TsFunctionValue function && ReferenceEquals(value, GetReference(function.FunctionName));

    internal static bool HasProperty(string name) => name == "size" || Methods.ContainsKey(name);

    internal static TsValue ReadProperty(TsSetValue receiver, string name) =>
        name == "size" ? TsValue.FromFloat64(receiver.Value.Count)
        : Methods.TryGetValue(name, out var method) ? method
        : TsValue.Null;

    internal static bool IsSizeKey(TsValue key) => key is TsStringValue { Value: "size" };

    internal static TsSetValue RequireReceiver(TsValue[] args) =>
        args.Length > 0 && args[0] is TsSetValue set
            ? set
            : throw TypeError("Receiver is not a Set");

    internal static TsValue ArgOrVoid(TsValue[] args, int index) =>
        index < args.Length ? args[index] : TsValue.Void;

    internal static TsThrownException TypeError(string message)
    {
        var error = new TsObject("TypeError");
        error.SetField("name", TsValue.FromString("TypeError"));
        error.SetField("message", TsValue.FromString(message));
        return new TsThrownException(TsValue.FromObject(error));
    }
}
