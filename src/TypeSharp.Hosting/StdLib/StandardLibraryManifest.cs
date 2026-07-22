namespace TypeSharp.Hosting.StdLib;

public enum StandardCapabilityStatus
{
    Supported,
    Partial,
    Unsupported
}

public sealed record StandardLibraryCapability(
    string Name,
    string Signature,
    StandardCapabilityStatus Status,
    string Semantics,
    string Limits,
    IReadOnlyList<string> Tests);

public static class StandardLibraryManifest
{
    public const string Version = "tsnet-stdlib-2026.07";

    public static IReadOnlyList<StandardLibraryCapability> Capabilities { get; } =
    [
        new(
            "IterableProtocol",
            "for...of, spread, Array.from",
            StandardCapabilityStatus.Supported,
            "Arrays, strings, Uint8Array, Set, Map and array-like objects are materialized through one shared iterable policy. Map iteration yields [key, value] pairs; Set iteration yields values.",
            "Symbol.iterator objects are not exposed as first-class user values yet; the VM consumes the declared protocol internally.",
            ["ForOf_ConsumesArrayLikeRuntimeObjectsThroughSharedIterablePolicy", "ArrayFrom_UsesSharedIterablePolicyAndMapper", "Spread_UsesSharedIterablePolicy"]),
        new(
            "Map",
            "Map<K,V>: size, set, get, has, delete, clear, keys, values, entries, forEach",
            StandardCapabilityStatus.Supported,
            "Keys use SameValueZero equality; insertion order is stable; updating an existing key does not move it. forEach uses the live ordered collection, so deletions before visitation are skipped and additions before completion can be visited.",
            "WeakMap, Symbol.iterator as a user-visible method and custom iterator closing are outside this capability.",
            ["Map_SupportsTypedLookupMutationAndSameValueZeroKeys", "Map_PreservesInsertionOrderAndUsesSameValueZero", "Map_ForEach_UsesLiveOrderedCollection"]),
        new(
            "Set",
            "Set<T>: size, add, has, delete, clear, keys, values, entries, forEach",
            StandardCapabilityStatus.Supported,
            "Values use SameValueZero equality; insertion order is stable; duplicates are ignored. keys and values are equivalent; entries yields [value, value]. forEach uses the live ordered collection.",
            "WeakSet, Symbol.iterator as a user-visible method and custom iterator closing are outside this capability.",
            ["Set_SupportsTypedMembershipMutationAndSize", "Set_PreservesInsertionOrderAndUsesSameValueZero", "Set_ForEach_UsesLiveOrderedCollection"]),
        new(
            "Date",
            "Date: constructor(), constructor(timestamp|string|utc components), getTime, valueOf, toISOString",
            StandardCapabilityStatus.Supported,
            "Timestamps are UTC milliseconds. Component constructors are interpreted as UTC, not host-local time. Invalid constructors store NaN; getTime/valueOf return NaN and toISOString throws Invalid Date.",
            "Locale-sensitive formatting and host-local timezone conversion are intentionally unsupported.",
            ["Date_GetTime_ReturnsUnixMilliseconds", "Date_SupportsUtcTimestampIsoAndInvalidSemantics"]),
        new(
            "ArrayStringMathNumberUint8ArrayRegExp",
            "Existing declared methods only",
            StandardCapabilityStatus.Partial,
            "The runtime exposes the currently bound members for these types, covered by their targeted tests.",
            "This is not a full lib.es2020 implementation.",
            ["Existing conformance tests"]),
        new(
            "ObjectJsonErrorPromiseProxyReflectWeakIntl",
            "Not declared as a supported standard surface",
            StandardCapabilityStatus.Unsupported,
            "These APIs must not be treated as generally available until semantics, limits and tests are designed.",
            "Promise microtasks, JSON host-object isolation, RegExp expansion, Proxy/Reflect/Weak collections and Intl remain outside the supported template.",
            [])
    ];

    public static StandardLibraryCapability? Find(string name) =>
        Capabilities.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
}
