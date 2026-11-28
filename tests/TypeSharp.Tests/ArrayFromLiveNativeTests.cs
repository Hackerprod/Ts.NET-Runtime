using System.Globalization;
using TypeSharp.Hosting;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

public sealed class ArrayFromLiveNativeTests
{
    public static IEnumerable<object[]> Rows()
    {
        yield return new object[] { "from-array-basic",
            """
            function main() {
                let a = [1, 2];
                let copied = Array.from(a);
                copied[1] = 9;
                return [a, copied, Array.from(new Array(2)), Array.from(a, undefined)];
            }
            """ + "\n", "[[1,2],[1,9],[U,U],[1,2]]" };
        yield return new object[] { "from-array-replace",
            """
            function main() {
                let a = [1, 2];
                let b = Array.from(a, (value, index) => { if (index === 0) a[1] = 9; return value; });
                return [b, a];
            }
            """ + "\n", "[[1,9],[1,9]]" };
        yield return new object[] { "from-array-grow",
            """
            function main() {
                let a = [1, 2];
                let b = Array.from(a, (value, index) => { if (index === 0) a.push(3); return value; });
                return [b, a];
            }
            """ + "\n", "[[1,2,3],[1,2,3]]" };
        yield return new object[] { "from-array-shrink",
            """
            function main() {
                let a = [1, 2];
                let b = Array.from(a, (value, index) => { if (index === 0) a.pop(); return value; });
                return [b, a];
            }
            """ + "\n", "[[1],[1]]" };
        yield return new object[] { "from-bytes",
            """
            function main() {
                let a = new Uint8Array([1, 2]);
                let before = Array.from(a);
                let b = Array.from(a, (value, index) => { if (index === 0) a[1] = 9; return value; });
                return [before, b, Array.from(a)];
            }
            """ + "\n", "[[1,2],[1,9],[1,9]]" };
        yield return new object[] { "from-string",
            """
            function main() {
                return [Array.from("A😀B"), Array.from("A😀B", (value, index) => [value, index])];
            }
            """ + "\n", "[[s:0041,s:D83DDE00,s:0042],[[s:0041,0],[s:D83DDE00,1],[s:0042,2]]]" };
        yield return new object[] { "from-set-add",
            """
            function main() {
                let a = new Set([1, 2]);
                let b = Array.from(a, (value, index) => { if (index === 0) a.add(3); return value; });
                return [b, Array.from(a)];
            }
            """ + "\n", "[[1,2,3],[1,2,3]]" };
        yield return new object[] { "from-set-delete",
            """
            function main() {
                let a = new Set([1, 2, 3]);
                let b = Array.from(a, (value, index) => { if (index === 0) a.delete(2); return value; });
                return [b, Array.from(a)];
            }
            """ + "\n", "[[1,3],[1,3]]" };
        yield return new object[] { "from-map-update",
            """
            function main() {
                let a = new Map([["a", 1], ["b", 2]]);
                let b = Array.from(a, (pair, index) => { if (index === 0) a.set("b", 9); return pair; });
                return [b, Array.from(a)];
            }
            """ + "\n", "[[[s:0061,1],[s:0062,9]],[[s:0061,1],[s:0062,9]]]" };
        yield return new object[] { "from-map-add",
            """
            function main() {
                let a = new Map([["a", 1], ["b", 2]]);
                let b = Array.from(a, (pair, index) => { if (index === 0) a.set("c", 3); return pair; });
                return [b, Array.from(a)];
            }
            """ + "\n", "[[[s:0061,1],[s:0062,2],[s:0063,3]],[[s:0061,1],[s:0062,2],[s:0063,3]]]" };
        yield return new object[] { "from-map-delete",
            """
            function main() {
                let a = new Map([["a", 1], ["b", 2], ["c", 3]]);
                let b = Array.from(a, (pair, index) => { if (index === 0) a.delete("b"); return pair; });
                return [b, Array.from(a)];
            }
            """ + "\n", "[[[s:0061,1],[s:0063,3]],[[s:0061,1],[s:0063,3]]]" };
        yield return new object[] { "from-array-like-bracket",
            """
            function main() {
                let a = {length: 2, 0: 1, 1: 2};
                let before = Array.from(a);
                let b = Array.from(a, (value, index) => {
                    if (index === 0) { a[1] = 9; a["length"] = 1; }
                    return value;
                });
                return [before, b, Array.from(a)];
            }
            """ + "\n", "[[1,2],[1,9],[1]]" };
        yield return new object[] { "from-bytes-view",
            """
            function main() {
                let base = new Uint8Array([7, 1, 2, 8]);
                let view = base.subarray(1, 3);
                let before = Array.from(view);
                let mapped = Array.from(view, (value, index) => {
                    if (index === 0) base[2] = 9;
                    return [value, index];
                });
                return [before, mapped, Array.from(view, undefined), Array.from(base)];
            }
            """ + "\n", "[[1,2],[[1,0],[9,1]],[1,9],[7,1,9,8]]" };
        yield return new object[] { "from-map-pairs",
            """
            function main() {
                let a = new Map([["a", 1], ["b", 2]]);
                let b = Array.from(a, (pair, index) => {
                    if (index === 0) {
                        pair[1] = 7;
                        a.set("b", 9);
                    }
                    return [pair, index];
                });
                let seen = b[1][0][1];
                b[1][0][1] = 8;
                return [seen, b, Array.from(a)];
            }
            """ + "\n", "[9,[[[s:0061,7],0],[[s:0062,8],1]],[[s:0061,1],[s:0062,9]]]" };
        yield return new object[] { "from-map-clear",
            """
            function main() {
                let a = new Map([["a", 1], ["b", 2]]);
                let b = Array.from(a, (pair, index) => {
                    if (index === 0) {
                        a.clear();
                        a.set("c", 3);
                    }
                    return [pair, index];
                });
                return [b, Array.from(a, undefined)];
            }
            """ + "\n", "[[[[s:0061,1],0],[[s:0063,3],1]],[[s:0063,3]]]" };
        yield return new object[] { "from-set-clear",
            """
            function main() {
                let a = new Set([1, 2]);
                let b = Array.from(a, (value, index) => {
                    if (index === 0) {
                        a.clear();
                        a.add(3);
                    }
                    return [value, index];
                });
                return [b, Array.from(a, undefined)];
            }
            """ + "\n", "[[[1,0],[3,1]],[3]]" };
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task MappedArrayFrom_UsesLiveNativeReads(string id, string source, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("array-from-live-");
        try
        {
            string path = Path.Combine(directory.FullName, id + ".ts");
            await File.WriteAllTextAsync(path, source);
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceFile(path).BuildAsync();
            string observed = Display(runtime.Invoke("main"));
            Assert.True(observed == expected, $"mapped Array.from observation: {observed}");
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
        TsFloat64Value n => n.Value.ToString("R", CultureInfo.InvariantCulture),
        TsFloat32Value n => n.Value.ToString("R", CultureInfo.InvariantCulture),
        TsStringValue s => "s:" + string.Concat(s.Value.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture))),
        TsVoid => "U",
        TsNull => "null",
        _ => throw new InvalidOperationException("Unexpected carrier: " + x?.GetType().Name),
    };
}
