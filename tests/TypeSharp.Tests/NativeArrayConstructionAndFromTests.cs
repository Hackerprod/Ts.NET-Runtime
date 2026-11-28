using System.Globalization;
using TypeSharp.Hosting;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

// Exact runtime cases from the frozen native Array conformance oracles.
public sealed class NativeArrayConstructionAndFromTests
{
    [Theory]
    [InlineData("ctor-negative-call-dynamic-catch", "function nameOf(e) { return e.name; }\nfunction main() {\n    try { return [Array(-1).length]; } catch (e) { return nameOf(e) === \"RangeError\"; }\n}\n", "true")]
    [InlineData("ctor-fractional-new-dynamic-catch", "function nameOf(e) { return e.name; }\nfunction main() {\n    try { return [new Array(2.5).length]; } catch (e) { return nameOf(e) === \"RangeError\"; }\n}\n", "true")]
    [InlineData("ctor-fractional-call-dynamic-catch", "function nameOf(e) { return e.name; }\nfunction main() {\n    try { return [Array(1.5).length]; } catch (e) { return nameOf(e) === \"RangeError\"; }\n}\n", "true")]
    [InlineData("ctor-negative-new-dynamic-catch", "function nameOf(e) { return e.name; }\nfunction main() {\n    try { return [new Array(-2).length]; } catch (e) { return nameOf(e) === \"RangeError\"; }\n}\n", "true")]
    [InlineData("ctor-controls", "function main() {\n    return [Array(), new Array(2), Array(2), Array(\"2\"), new Array([1]), new Array(true), Array(1, 2)];\n}\n", "[[],[H,H],[H,H],[s:0032],[[1]],[true],[1,2]]")]
    [InlineData("of-controls", "function main() {\n    return [Array.of(), Array.of(2), Array.of(undefined, null, [3]), Array.of(\"2\", true)];\n}\n", "[[],[2],[U,null,[3]],[s:0032,true]]")]
    [InlineData("isarray-controls", "function main() {\n    return [Array.isArray([]), Array.isArray(Array(1)), Array.isArray(Array.of(3)), Array.isArray(new Uint8Array([1])), Array.isArray(\"ab\"), Array.isArray({length: 2}), Array.isArray(null), Array.isArray(undefined), Array.isArray()];\n}\n", "[true,true,true,false,false,false,false,false,false]")]
    [InlineData("from-empty-object", "function main() {\n    return Array.from({});\n}\n", "[]")]
    [InlineData("from-number", "function main() {\n    return Array.from(42);\n}\n", "[]")]
    [InlineData("from-boolean", "function main() {\n    return Array.from(true);\n}\n", "[]")]
    [InlineData("from-missing-length-null", "function main() {\n    return Array.from({length: null, 0: 9});\n}\n", "[]")]
    [InlineData("from-nullish-dynamic-catch", "function nameOf(e) { return e.name; }\nfunction main() {\n    let source = null; try { return Array.from(source); } catch (e) { return nameOf(e) === \"TypeError\"; }\n}\n", "true")]
    [InlineData("from-undefined-dynamic-catch", "function nameOf(e) { return e.name; }\nfunction main() {\n    try { return Array.from(undefined); } catch (e) { return nameOf(e) === \"TypeError\"; }\n}\n", "true")]
    [InlineData("from-text-length", "function main() {\n    return Array.from({length: \"2.8\", 0: 7, 1: 9, 2: 11});\n}\n", "[7,9]")]
    [InlineData("from-text-length-mapper", "function main() {\n    return Array.from({length: \"0x2\", 0: 7, 1: 9, 2: 11}, (v, i) => v + i);\n}\n", "[7,10]")]
    [InlineData("from-bigint-length-dynamic-catch", "function nameOf(e) { return e.name; }\nfunction main() {\n    try { return Array.from({length: 2n, 0: 7, 1: 9}); } catch(e) { return nameOf(e) === \"TypeError\"; }\n}\n", "true")]
    [InlineData("from-map-this-context", "function main() {\n    let ctx = {tag: 4}; return Array.from([2, 3], function (v, i) { \"use strict\"; return [this === ctx, v, i]; }, ctx);\n}\n", "[[true,2,0],[true,3,1]]")]
    [InlineData("from-map-this-null", "function main() {\n    return Array.from(\"A\", function (v, i) { \"use strict\"; return [this === null, v, i]; }, null);\n}\n", "[[true,s:0041,0]]")]
    [InlineData("from-mapper-empty-array-dynamic-catch", "function nameOf(e) { return e.name; }\nfunction main() {\n    try { return Array.from([], null); } catch (e) { return nameOf(e) === \"TypeError\"; }\n}\n", "true")]
    [InlineData("from-mapper-nonempty-array-dynamic-catch", "function nameOf(e) { return e.name; }\nfunction main() {\n    try { return Array.from([1], 5); } catch (e) { return nameOf(e) === \"TypeError\"; }\n}\n", "true")]
    [InlineData("from-holes-code-points", "function main() {\n    return [Array.from(new Array(2)), Array.from(\"A😀B\", undefined), Array.from({length: 3, 1: 9}), Array.from({length: -1, 0: 9})];\n}\n", "[[U,U],[s:0041,s:D83DDE00,s:0042],[U,9,U],[]]")]
    [InlineData("from-live-array", "function main() {\n    let a = [1, 2]; let b = Array.from(a, (v, i) => { if (i === 0) { a[1] = 9; a.push(3); } return v; }); return [b, a];\n}\n", "[[1,9,3],[1,9,3]]")]
    [InlineData("from-live-arraylike", "function main() {\n    let a = {length: 2, 0: 1, 1: 2}; let b = Array.from(a, (v, i) => { if (i === 0) { a[1] = 9; a[\"length\"] = 1; } return v; }); return [b, Array.from(a)];\n}\n", "[[1,9],[1]]")]
    [InlineData("from-live-byte-view", "function main() {\n    let a = new Uint8Array([7, 1, 2, 8]); let view = a.subarray(1, 3); let b = Array.from(view, (v, i) => { if (i === 0) a[2] = 9; return v; }); return [b, Array.from(a)];\n}\n", "[[1,9],[7,1,9,8]]")]
    [InlineData("from-live-set", "function main() {\n    let a = new Set([1, 2]); let b = Array.from(a, (v, i) => { if (i === 0) a.add(3); return v; }); return [b, Array.from(a)];\n}\n", "[[1,2,3],[1,2,3]]")]
    [InlineData("from-live-map", "function main() {\n    let a = new Map([[\"a\", 1], [\"b\", 2]]); let b = Array.from(a, (v, i) => { if (i === 0) a.set(\"b\", 9); return v[1]; }); return [b, Array.from(a)];\n}\n", "[[1,9],[[s:0061,1],[s:0062,9]]]")]
    [InlineData("from-map-this-omitted", "function main() {\n    return Array.from([2], function (v, i) { \"use strict\"; return [this === undefined, v, i]; });\n}\n", "[[true,2,0]]")]
    [InlineData("from-map-arrow-lexical", "function main() {\n    let outer = new Map(); outer.set(\"x\", 2); let lexical = {tag: 5}; let ignored = {tag: 6}; let values = []; outer.forEach(function (v) { values.push(Array.from([v], (x) => [this === lexical, x], ignored)); }, lexical); return values;\n}\n", "[[[true,2]]]")]
    [InlineData("prototype-receiver-guard", "function main() {\n    let context = {tag: 4}; return [2].map(function (v) { \"use strict\"; return [this === context, v]; }, context);\n}\n", "[[true,2]]")]
    [InlineData("control-catch-name", "function nameOf(e) { return e.name; }\nfunction main() {\n    try { throw {name: \"Proof\"}; } catch (e) { return nameOf(e) === \"Proof\"; }\n}\n", "true")]
    [InlineData("control-catch-nonthrow", "function nameOf(e) { return e.name; }\nfunction main() {\n    try { return Array.of(2); } catch (e) { return nameOf(e) === \"RangeError\"; }\n}\n", "[2]")]
    [InlineData("ctor-fractional-language-catch", "function main() {\n    try { let value = new Array(1.5); return false; } catch (e) { return true; }\n}\n", "true")]
    [InlineData("from-null-language-catch", "function main() {\n    try { let value = Array.from(null); return false; } catch (e) { return true; }\n}\n", "true")]
    [InlineData("from-mapper-empty-language-catch", "function main() {\n    try { let value = Array.from([], null); return false; } catch (e) { return true; }\n}\n", "true")]
    [InlineData("from-missing-vs-present", "function main() {\n    let x = {length: 3, 1: null, 2: undefined};\n    return [Array.from(x), Array.from(x, undefined), Array.from(x, (v) => v)];\n}\n", "[[U,null,U],[U,null,U],[U,null,U]]")]
    [InlineData("from-present-null-control", "function main() {\n    let x = {length: 2, 0: null, 1: undefined};\n    return [Array.from(x), Array.from(x, undefined), Array.from(x, (v) => v)];\n}\n", "[[null,U],[null,U],[null,U]]")]
    [InlineData("from-normalized-excess-arguments", "function main() {\n    return Array.from([7, 8], undefined, {label: \"ignored\"}, \"unused\");\n}\n", "[7,8]")]
    [InlineData("ctor-spread-call-single", "function main() {\n    let args = [2]; return Array(...args);\n}\n", "[H,H]")]
    [InlineData("ctor-spread-call-mixed", "function main() {\n    let args = [2, 3]; return Array(1, ...args);\n}\n", "[1,2,3]")]
    [InlineData("ctor-spread-new-single", "function main() {\n    let args = [2]; return new Array(...args);\n}\n", "[H,H]")]
    [InlineData("ctor-spread-new-mixed", "function main() {\n    let args = [2, 3]; return new Array(1, ...args);\n}\n", "[1,2,3]")]
    [InlineData("isarray-spread", "function main() {\n    let some = [[2]]; let empty = []; return [Array.isArray(...some), Array.isArray(...empty)];\n}\n", "[true,false]")]
    [InlineData("from-spread", "function main() {\n    let args = [[2, 3]]; return Array.from(...args);\n}\n", "[2,3]")]
    [InlineData("of-homogeneous-spread-mixed", "function main() {\n    let args = [2, 3]; return Array.of(1, ...args);\n}\n", "[1,2,3]")]
    [InlineData("of-homogeneous-spread-single", "function main() {\n    let args = [2]; return Array.of(...args);\n}\n", "[2]")]
    public async Task PublicHostingMatchesFrozenArraySemantics(string id, string source, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("typesharp-native-array-");
        try
        {
            var path = Path.Combine(directory.FullName, id + ".ts");
            await File.WriteAllTextAsync(path, source);
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceFile(path).BuildAsync();
            Assert.Equal(expected, Display(runtime.Invoke("main")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    static string Display(TsValue? x) => x switch
    {
        TsArrayValue a => "[" + string.Join(",", Enumerable.Range(0, a.Value.Count).Select(i =>
            a.Value.IsHole(i) ? "H" : Display(a.Value.Get(i)))) + "]",
        TsBoolValue b => b.Value ? "true" : "false",
        TsInt32Value n => n.Value.ToString(CultureInfo.InvariantCulture),
        TsInt64Value n => n.Value.ToString(CultureInfo.InvariantCulture),
        TsUInt64Value n => n.Value.ToString(CultureInfo.InvariantCulture),
        TsFloat64Value n => n.Value.ToString("R", CultureInfo.InvariantCulture),
        TsFloat32Value n => n.Value.ToString("R", CultureInfo.InvariantCulture),
        TsStringValue s => "s:" + string.Concat(s.Value.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture))),
        TsVoid => "U",
        TsNull => "null",
        _ => throw new InvalidOperationException("Unexpected carrier: " + x?.GetType().Name),
    };
}
