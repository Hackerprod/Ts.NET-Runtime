using TypeSharp.VM.Memory;

namespace TypeSharp.VM.Interpreter;

internal static class NativeMap
{
    internal const string ReadonlySizeMessage = "Cannot assign to readonly Map.size";

    internal static bool IsMethod(string name) => name is
        "Map::set" or "Map::get" or "Map::has" or "Map::delete" or "Map::clear" or
        "Map::keys" or "Map::values" or "Map::entries" or "Map::forEach";

    internal static TsMapValue RequireReceiver(TsValue[] args) =>
        args.Length > 0 && args[0] is TsMapValue map
            ? map
            : throw TypeError("Receiver is not a Map");

    internal static TsValue ArgOrVoid(TsValue[] args, int index) =>
        index < args.Length ? args[index] : TsValue.Void;

    internal static bool IsSizeKey(TsValue key) => key is TsStringValue { Value: "size" };

    internal static TsThrownException TypeError(string message)
    {
        var error = new TsObject("TypeError");
        error.SetField("name", TsValue.FromString("TypeError"));
        error.SetField("message", TsValue.FromString(message));
        return new TsThrownException(TsValue.FromObject(error));
    }
}
