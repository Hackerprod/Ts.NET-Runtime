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
            "Date: constructor(), constructor(timestamp|string|utc components), getTime, valueOf, toISOString, getUTCFullYear, getUTCMonth(), getUTCDate(), getUTCDay(), getUTCHours(), getUTCMinutes(), getUTCSeconds(), getUTCMilliseconds()",
            StandardCapabilityStatus.Supported,
            "Timestamps are integral UTC milliseconds in the inclusive range ±8,640,000,000,000,000. Component constructors and zone-less ISO times use UTC, not host-local time. Invalid timestamps produce NaN in numeric methods and a catchable RangeError (Invalid Date) in toISOString. Valid ISO output uses signed six-digit years outside 0000–9999. getUTCFullYear returns the UTC year or NaN if invalid. getUTCMonth returns the zero-based UTC month (January 0 through December 11), or NaN if invalid. getUTCDate returns the UTC day of the month (1 through 31), or NaN if invalid. getUTCDay returns the UTC weekday (Sunday 0 through Saturday 6), or NaN if invalid. getUTCHours returns the UTC hour (0 through 23), or NaN if invalid. getUTCMinutes returns the UTC minute (0 through 59), or NaN if invalid. getUTCSeconds returns the UTC second (0 through 59), or NaN if invalid. getUTCMilliseconds returns the millisecond within the UTC second (0 through 999), or NaN if invalid.",
            "Locale-sensitive formatting and host-local timezone conversion are intentionally unsupported.",
            ["Date_GetTime_ReturnsUnixMilliseconds", "Date_SupportsUtcTimestampIsoAndInvalidSemantics", "GetUtcFullYear_ReturnsNativeUtcYearAndInvalidNaN", "GetUtcMonth_ReturnsZeroBasedUtcMonth", "GetUtcDate_ReturnsOneBasedUtcDayAndInvalidNaN", "GetUtcDay_ReturnsZeroBasedUtcWeekdayAndInvalidNaN", "GetUtcHours_ReturnsHourOfStoredUtcInstant", "GetUtcMinutes_ReturnsMinuteOfStoredUtcInstant", "GetUtcSeconds_ReturnsSecondOfStoredUtcInstant", "GetUtcMilliseconds_ReturnsMillisecondOfStoredUtcInstant"]),
        new(
            "ArrayStringMathNumberUint8ArrayRegExp",
            "Existing declared methods, plus literal-backed RegExp.test and lastIndex",
            StandardCapabilityStatus.Partial,
            "The runtime exposes the existing declared members for these types. RegExp literals support test and a per-value lastIndex; global and sticky tests advance or reset it. Host Regex and Test remain stateless.",
            "This is not a full lib.es2020 implementation. RegExp construction, exec, metadata properties and full Unicode flag semantics are not supported.",
            ["Existing conformance tests", "RegExpNativeSurfaceTests"]),
        new(
            "ObjectJsonErrorPromiseProxyReflectWeakIntl",
            "Not declared as a supported standard surface",
            StandardCapabilityStatus.Unsupported,
            "These APIs must not be treated as generally available until semantics, limits and tests are designed.",
            "Promise microtasks, JSON host-object isolation, RegExp beyond its literal test surface, Proxy/Reflect/Weak collections and Intl remain outside the supported template.",
            [])
    ];

    public static StandardLibraryCapability? Find(string name) =>
        Capabilities.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
}
