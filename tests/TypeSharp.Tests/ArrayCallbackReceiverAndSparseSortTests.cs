using System.Globalization;
using TypeSharp.Hosting;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

public sealed class ArrayCallbackReceiverAndSparseSortTests
{
    public static IEnumerable<object[]> ReceiverRows()
    {
        yield return new object[] { "map-this", "function main() {\n    let a = [1, 2];\n    let context = { label: \"callback\" };\n    let trace = [];\n    let result = a.map(function (v, i, owner) {\n        let same = this === context;\n        trace.push([same, v, i, owner === a]);\n        return same ? v + 4 : v;\n    }, context);\n    return [result, trace];\n}\n", "[[5,6],[[true,1,0,true],[true,2,1,true]]]" };
        yield return new object[] { "flatMap-this", "function main() {\n    let a = [1, 2];\n    let context = { label: \"callback\" };\n    let trace = [];\n    let result = a.flatMap(function (v, i, owner) {\n        let same = this === context;\n        trace.push([same, v, i, owner === a]);\n        return same ? [v, v + 4] : [v];\n    }, context);\n    return [result, trace];\n}\n", "[[1,5,2,6],[[true,1,0,true],[true,2,1,true]]]" };
        yield return new object[] { "filter-this", "function main() {\n    let a = [1, 2];\n    let context = { label: \"callback\" };\n    let trace = [];\n    let result = a.filter(function (v, i, owner) {\n        let same = this === context;\n        trace.push([same, v, i, owner === a]);\n        return same && v === 2;\n    }, context);\n    return [result, trace];\n}\n", "[[2],[[true,1,0,true],[true,2,1,true]]]" };
        yield return new object[] { "forEach-this", "function main() {\n    let a = [1, 2];\n    let context = { label: \"callback\" };\n    let trace = [];\n    let result = a.forEach(function (v, i, owner) {\n        let same = this === context;\n        trace.push([same, v, i, owner === a]);\n        return;\n    }, context);\n    return [result, trace];\n}\n", "[U,[[true,1,0,true],[true,2,1,true]]]" };
        yield return new object[] { "some-this", "function main() {\n    let a = [1, 2];\n    let context = { label: \"callback\" };\n    let trace = [];\n    let result = a.some(function (v, i, owner) {\n        let same = this === context;\n        trace.push([same, v, i, owner === a]);\n        return same;\n    }, context);\n    return [result, trace];\n}\n", "[true,[[true,1,0,true]]]" };
        yield return new object[] { "every-this", "function main() {\n    let a = [1, 2];\n    let context = { label: \"callback\" };\n    let trace = [];\n    let result = a.every(function (v, i, owner) {\n        let same = this === context;\n        trace.push([same, v, i, owner === a]);\n        return same;\n    }, context);\n    return [result, trace];\n}\n", "[true,[[true,1,0,true],[true,2,1,true]]]" };
        yield return new object[] { "find-this", "function main() {\n    let a = [1, 2];\n    let context = { label: \"callback\" };\n    let trace = [];\n    let result = a.find(function (v, i, owner) {\n        let same = this === context;\n        trace.push([same, v, i, owner === a]);\n        return same && v === 2;\n    }, context);\n    return [result, trace];\n}\n", "[2,[[true,1,0,true],[true,2,1,true]]]" };
        yield return new object[] { "findIndex-this", "function main() {\n    let a = [1, 2];\n    let context = { label: \"callback\" };\n    let trace = [];\n    let result = a.findIndex(function (v, i, owner) {\n        let same = this === context;\n        trace.push([same, v, i, owner === a]);\n        return same && v === 2;\n    }, context);\n    return [result, trace];\n}\n", "[1,[[true,1,0,true],[true,2,1,true]]]" };
        yield return new object[] { "findLast-this", "function main() {\n    let a = [1, 2];\n    let context = { label: \"callback\" };\n    let trace = [];\n    let result = a.findLast(function (v, i, owner) {\n        let same = this === context;\n        trace.push([same, v, i, owner === a]);\n        return same;\n    }, context);\n    return [result, trace];\n}\n", "[2,[[true,2,1,true]]]" };
        yield return new object[] { "findLastIndex-this", "function main() {\n    let a = [1, 2];\n    let context = { label: \"callback\" };\n    let trace = [];\n    let result = a.findLastIndex(function (v, i, owner) {\n        let same = this === context;\n        trace.push([same, v, i, owner === a]);\n        return same;\n    }, context);\n    return [result, trace];\n}\n", "[1,[[true,2,1,true]]]" };
        yield return new object[] { "arrow-lexical", "function main() {\n    let outer = new Map();\n    outer.set(1, 2);\n    let lexical = { label: \"outer\" };\n    let ignored = { label: \"inner\" };\n    let trace = [];\n    outer.forEach(function (value) {\n        let a = [value];\n        trace.push(a.some((v) => this === lexical && v === value, ignored));\n    }, lexical);\n    return trace;\n}\n", "[true]" };
        yield return new object[] { "omitted-this", "function main() {\n    let a = [2];\n    return a.map(function (v) {\n        \"use strict\";\n        return [this === undefined, v];\n    });\n}\n", "[[true,2]]" };
        yield return new object[] { "explicit-receivers", "function main() {\n    let a = [2];\n    let callback = function(v, i, owner) {\n        \"use strict\";\n        return [this === undefined, this === null, v, i, owner === a];\n    };\n    return [a.map(callback, undefined), a.map(callback, null)];\n}\n", "[[[true,false,2,0,true]],[[false,true,2,0,true]]]" };
        yield return new object[] { "reduce-no-receiver", "function main() {\n    let a = new Array(3); a[0] = 2; a[2] = 5;\n    let trace = [];\n    let result = a.reduce(function(acc, v, i, owner) {\n        \"use strict\";\n        trace.push([this === undefined, acc, v, i, owner === a]);\n        return acc + v;\n    }, 10);\n    return [result, trace];\n}\n", "[17,[[true,10,2,0,true],[true,12,5,2,true]]]" };
    }

    [Theory]
    [MemberData(nameof(ReceiverRows))]
    public Task ArrayPrototype_ReachesCorrectCallbackReceiver(string id, string source, string expected) =>
        AssertHosting(id, source, expected);

    public static IEnumerable<object[]> SortRows()
    {
        yield return new object[] { "reduce-control", "function main() {\n    let a = new Array(3); a[0] = 2; a[2] = 5;\n    let trace = [];\n    let result = a.reduce((acc, v, i, owner) => {\n        trace.push([acc, v, i, owner === a]);\n        return acc + v;\n    }, 10);\n    return [result, trace];\n}\n", "[17,[[10,2,0,true],[12,5,2,true]]]" };
        yield return new object[] { "sort-control", "function main() {\n    let dense = [3, 1, 2];\n    let result = dense.sort((a, b) => a - b);\n    let lexical = [2, 10, 1];\n    lexical.sort();\n    let sparse = new Array(3); sparse[0] = \"b\"; sparse[2] = \"a\";\n    sparse.sort();\n    return [result, result === dense, lexical, sparse];\n}\n", "[[1,2,3],true,[1,10,2],[s:0061,s:0062,H]]" };
        yield return new object[] { "sort-sparse-comparator", "function main() {\n    let a = new Array(3); a[0] = 3; a[2] = 1;\n    let sawUndefined = false;\n    let result = a.sort((x, y) => {\n        if (x === undefined || y === undefined) sawUndefined = true;\n        return (x === undefined ? 99 : x) - (y === undefined ? 99 : y);\n    });\n    return [result, result === a, sawUndefined];\n}\n", "[[1,3,H],true,false]" };
        yield return new object[] { "sparse-array-iteration", "function main() {\n    let a = new Array(3); a[0] = 2; a[2] = 5;\n    let mapped = a.map((v, i) => {\n        if (i === 0) a.push(8);\n        return v + i;\n    });\n    let found = a.findIndex((v) => v === undefined);\n    return [mapped, a, found];\n}\n", "[[2,H,7],[2,H,5,8],1]" };
        yield return new object[] { "sort-no-receiver-dense-assigned", "function main() {\n    \"use strict\";\n    let a = new Array(4); a[0] = 3; a[1] = undefined; a[2] = 1; a[3] = 2;\n    let called = false;\n    let valid = true;\n    let result = a.sort(function(...args) {\n        called = true;\n        if (this !== undefined || args.length !== 2 || args[0] === undefined || args[1] === undefined) valid = false;\n        return args[0] - args[1];\n    });\n    let omitted = [2, 10, 1];\n    let explicit = [2, 10, 1];\n    omitted.sort();\n    explicit.sort(undefined);\n    return [result, result === a, called, valid, omitted, explicit];\n}\n", "[[1,2,3,U],true,true,true,[1,10,2],[1,10,2]]" };
        yield return new object[] { "sort-mixed-sparse", "function main() {\n    \"use strict\";\n    let a = new Array(4); a[0] = 3; a[2] = undefined; a[3] = 1;\n    let sawUndefined = false;\n    let called = false;\n    let receiverOK = true;\n    let result = a.sort(function(x, y) {\n        called = true;\n        if (this !== undefined) receiverOK = false;\n        if (x === undefined || y === undefined) sawUndefined = true;\n        return (x === undefined ? 99 : x) - (y === undefined ? 99 : y);\n    });\n    return [result, result === a, called, receiverOK, sawUndefined];\n}\n", "[[1,3,U,H],true,true,true,false]" };
        yield return new object[] { "sort-stable-sparse", "function main() {\n    let first = { key: 1 };\n    let second = { key: 1 };\n    let a = new Array(3); a[0] = first; a[2] = second;\n    let result = a.sort((x, y) => (x === undefined ? 99 : x.key) - (y === undefined ? 99 : y.key));\n    let slots = result.map(value => value === undefined);\n    return [result === a, result[0] === first, result[1] === second, slots];\n}\n", "[true,true,true,[false,false,H]]" };
    }

    [Theory]
    [MemberData(nameof(SortRows))]
    public Task ArrayPrototype_PreservesSparseComparatorSortContract(string id, string source, string expected) =>
        AssertHosting(id, source, expected);

    private static async Task AssertHosting(string id, string source, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("array-callback-sort-");
        try
        {
            string fixture = Path.Combine(directory.FullName, id + ".ts");
            await File.WriteAllTextAsync(fixture, source);
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceFile(fixture).BuildAsync();
            string observed = Display(runtime.Invoke("main"));
            Assert.True(observed == expected, "C333 observed: " + observed);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string Display(TsValue? x) => x switch
    {
        TsArrayValue a => "[" + string.Join(",", Enumerable.Range(0, a.Value.Count).Select(i =>
            a.Value.IsHole(i) ? "H" : Display(a.Value.Get(i)))) + "]",
        TsBoolValue b => b.Value ? "true" : "false",
        TsInt32Value n => n.Value.ToString(CultureInfo.InvariantCulture),
        TsInt64Value n => n.Value.ToString(CultureInfo.InvariantCulture),
        TsFloat64Value n => n.Value.ToString("R", CultureInfo.InvariantCulture),
        TsFloat32Value n => n.Value.ToString("R", CultureInfo.InvariantCulture),
        TsStringValue s => "s:" + string.Concat(s.Value.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture))),
        TsVoid => "U",
        TsNull => "null",
        _ => throw new InvalidOperationException("Unexpected carrier: " + x?.GetType().Name),
    };
}
