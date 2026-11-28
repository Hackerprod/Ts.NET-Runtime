using System.Globalization;
using TypeSharp.Hosting;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

public sealed class ArrayPrimitivePositionsFlatJoinTests
{
    public static IEnumerable<object[]> PositionRows()
    {
        yield return ["push", "function main() {\n    let a = [1]; return [a.push(), a.push(2, 3), a];\n}\n", "[1,3,[1,2,3]]"];
        yield return ["pop", "function main() {\n    let a = [1, 2]; return [a.pop(), a.pop(), a.pop(), a];\n}\n", "[2,1,U,[]]"];
        yield return ["shift", "function main() {\n    let a = new Array(3); a[1] = 7; return [a.shift(), a];\n}\n", "[U,[7,H]]"];
        yield return ["unshift", "function main() {\n    let a = new Array(2); a[1] = 7; return [a.unshift(), a.unshift(3, 4), a];\n}\n", "[2,4,[3,4,H,7]]"];
        yield return ["reverse", "function main() {\n    let a = new Array(3); a[0] = 7; return [a.reverse() === a, a];\n}\n", "[true,[H,H,7]]"];
        yield return ["toReversed", "function main() {\n    let a = new Array(3); a[0] = 7; let b = a.toReversed(); return [b === a, b, a];\n}\n", "[false,[U,U,7],[7,H,H]]"];
        yield return ["concat", "function main() {\n    let a = new Array(2); a[1] = 7; return [a.concat([8], 9), a];\n}\n", "[[H,7,8,9],[H,7]]"];
        yield return ["slice-sparse", "function main() {\n    let a = new Array(4); a[1] = 6; return [a.slice(-3, undefined), a];\n}\n", "[[6,H,H],[H,6,H,H]]"];
        yield return ["splice-sparse", "function main() {\n    let a = new Array(4); a[1] = 6; let none = a.splice(); let removed = a.splice(0, 2, 9); return [none, removed, a];\n}\n", "[[],[H,6],[9,H,H]]"];
        yield return ["indexOf-hole", "function main() {\n    let a = new Array(3); a[1] = undefined; return [a.indexOf(undefined), a.indexOf(undefined, 2)];\n}\n", "[1,-1]"];
        yield return ["lastIndexOf-omitted", "function main() {\n    let a = [8, 1, 8]; return [a.lastIndexOf(8), a.lastIndexOf(8, undefined), a.lastIndexOf(8, -4)];\n}\n", "[2,0,-1]"];
        yield return ["fill-holes", "function main() {\n    let a = new Array(3); return [a.fill(9, -2) === a, a];\n}\n", "[true,[H,9,9]]"];
        yield return ["slice-hex", "function main() {\n    let a = [1, 2, 3]; return a.slice(\"0x1\", \"0x3\");\n}\n", "[2,3]"];
        yield return ["splice-hex", "function main() {\n    let a = [1, 2, 3]; return [a.splice(\"0x1\", \"0x1\"), a];\n}\n", "[[2],[1,3]]"];
        yield return ["fill-hex", "function main() {\n    let a = [1, 2, 3]; return a.fill(8, \"0x1\", \"0x2\");\n}\n", "[1,8,3]"];
        yield return ["lastIndexOf-hex", "function main() {\n    let a = [1, 2, 1]; return a.lastIndexOf(1, \"0x2\");\n}\n", "2"];
        yield return ["slice-binary-octal", "function main() {\n    let a = [1, 2, 3, 4]; return a.slice(\"0b10\", \"0o3\");\n}\n", "[3]"];
        yield return ["splice-binary-octal", "function main() {\n    let a = [1, 2, 3, 4]; return [a.splice(\"0b1\", \"0o2\", 9), a];\n}\n", "[[2,3],[1,9,4]]"];
        yield return ["fill-binary-octal", "function main() {\n    let a = [1, 2, 3, 4]; return a.fill(9, \"0o1\", \"0b11\");\n}\n", "[1,9,9,4]"];
        yield return ["includes-hex-start", "function main() {\n    let a = [8, 1, 2]; return a.includes(8, \"0x1\");\n}\n", "false"];
        yield return ["indexOf-binary-start", "function main() {\n    let a = [8, 1, 8]; return a.indexOf(8, \"0b1\");\n}\n", "2"];
        yield return ["lastIndexOf-octal-start", "function main() {\n    let a = [8, 1, 8]; return a.lastIndexOf(8, \"0o2\");\n}\n", "2"];
        yield return ["slice-bom", "function main() {\n    let a = [1, 2, 3]; return a.slice(\"\\uFEFF1\\uFEFF\");\n}\n", "[2,3]"];
        yield return ["includes-hole", "function main() {\n    let a = new Array(2);\n    a[1] = Number(\"NaN\");\n    return [a.includes(undefined), a.includes(Number(\"NaN\"))];\n}\n", "[true,true]"];
        yield return ["position-omission", "function main() {\n    let a = [4, 1, 4, 2];\n    let b = [4, 1, 4, 2];\n    let c = [4, 1, 4, 2];\n    let out = new Array(8);\n    out[0] = a.splice();\n    out[1] = a.splice(\"-2.7\", \"-1.8\");\n    out[2] = a;\n    out[3] = b.splice(\"-2.7\");\n    out[4] = c.splice(\"-2.7\", undefined);\n    out[5] = c;\n    out[6] = c.lastIndexOf(4);\n    out[7] = c.lastIndexOf(4, undefined);\n    return out;\n}\n", "[[],[],[4,1,4,2],[4,2],[],[4,1,4,2],2,0]"];
        yield return ["position-ordinary", "function main() {\n    let a = [4, 1, 4, 2];\n    let out = new Array(6);\n    out[0] = a.slice(Number(\"NaN\"), undefined);\n    out[1] = a.indexOf(4, -0.0);\n    out[2] = a.includes(4, \"-2.8\");\n    out[3] = a.lastIndexOf(4, \"-2.8\");\n    out[4] = a.fill(7, \"0x1\", \"2.8\");\n    out[5] = a;\n    return out;\n}\n", "[[4,1,4,2],0,true,2,[4,7,4,2],[4,7,4,2]]"];
        yield return ["splice-octal-delete-zero-start", "function main() {\n    let a = new Array(4);\n    a[0] = 1;\n    a[2] = undefined;\n    a[3] = null;\n    let removed = a.splice(0, \"0o2\", 7);\n    let result = new Array(3);\n    result[0] = a;\n    result[1] = removed;\n    result[2] = removed === a;\n    return result;\n}\n", "[[7,U,null],[1,H],false]"];
        yield return ["splice-binary-start-zero-delete", "function main() {\n    let a = new Array(4);\n    a[0] = 1;\n    a[2] = undefined;\n    a[3] = null;\n    let removed = a.splice(\"0b10\", 0, 7);\n    let result = new Array(3);\n    result[0] = a;\n    result[1] = removed;\n    result[2] = removed === a;\n    return result;\n}\n", "[[1,H,7,U,null],[],false]"];
    }

    public static IEnumerable<object[]> DepthRows()
    {
        yield return ["flat-depth", "function main() {\n    let a = new Array(2);\n    let inner = new Array(2);\n    inner[0] = 1;\n    inner[1] = [2];\n    a[1] = inner;\n    return [a.flat(undefined), a.flat(null), a];\n}\n", "[[1,[2]],[[1,[2]]],[H,[1,[2]]]]"];
        yield return ["flat-decimal", "function main() {\n    let a = new Array(2);\n    let inner = new Array(2);\n    a[0] = 1;\n    inner[0] = 2;\n    inner[1] = [3];\n    a[1] = inner;\n    return a.flat(\"1.9\");\n}\n", "[1,2,[3]]"];
        yield return ["flat-binary-depth", "function main() {\n    let a = new Array(2);\n    let inner = new Array(2);\n    a[0] = 1;\n    inner[0] = 2;\n    inner[1] = [3];\n    a[1] = inner;\n    return a.flat(\"0b10\");\n}\n", "[1,2,3]"];
        yield return ["flat-positive-infinity", "function main() {\n    let a = new Array(2);\n    let inner = new Array(2);\n    a[0] = 1;\n    inner[0] = 2;\n    let deep = new Array(2);\n    deep[0] = 3;\n    deep[1] = [4];\n    inner[1] = deep;\n    a[1] = inner;\n    return a.flat(Number(\"Infinity\"));\n}\n", "[1,2,3,4]"];
        yield return ["flat-negative-infinity", "function main() {\n    let a = new Array(2);\n    let inner = new Array(2);\n    a[0] = 1;\n    inner[0] = 2;\n    inner[1] = [3];\n    a[1] = inner;\n    return a.flat(Number(\"-Infinity\"));\n}\n", "[1,[2,[3]]]"];
        yield return ["flat-string-infinity", "function main() {\n    let a = new Array(2);\n    let inner = new Array(2);\n    a[0] = 1;\n    inner[0] = 2;\n    inner[1] = [3];\n    a[1] = inner;\n    return a.flat(\"Infinity\");\n}\n", "[1,2,3]"];
        yield return ["flat-hex-depth", "function main() {\n    let a = new Array(2);\n    let inner = new Array(2);\n    a[0] = 1;\n    inner[0] = 2;\n    inner[1] = [3];\n    a[1] = inner;\n    return a.flat(\"0x2\");\n}\n", "[1,2,3]"];
        yield return ["flat-shared", "function main() {\n    let child = new Array(3);\n    child[1] = undefined;\n    child[2] = 7;\n    let a = new Array(3);\n    a[1] = child;\n    a[2] = child;\n    let result = a.flat(1);\n    let shallow = a.flat(0);\n    let out = new Array(5);\n    out[0] = result;\n    out[1] = shallow;\n    out[2] = result === a;\n    out[3] = shallow[0] === child;\n    out[4] = a;\n    return out;\n}\n", "[[U,7,U,7],[[H,U,7],[H,U,7]],false,true,[H,[H,U,7],[H,U,7]]]"];
        yield return ["flat-shared-unbounded", "function main() {\n    let child = new Array(3);\n    child[1] = undefined;\n    child[2] = 7;\n    let a = new Array(3);\n    a[1] = child;\n    a[2] = child;\n    let result = a.flat(Number(\"Infinity\"));\n    let shallow = a.flat(0);\n    let out = new Array(5);\n    out[0] = result;\n    out[1] = shallow;\n    out[2] = result === a;\n    out[3] = shallow[0] === child;\n    out[4] = a;\n    return out;\n}\n", "[[U,7,U,7],[[H,U,7],[H,U,7]],false,true,[H,[H,U,7],[H,U,7]]]"];
    }

    public static IEnumerable<object[]> JoinRows()
    {
        yield return ["join-basic", "function main() {\n    let a = new Array(4); a[0] = 1.5; a[2] = true; a[3] = null; return a.join(\"|\");\n}\n", "s:0031002E0035007C007C0074007200750065007C"];
        yield return ["join-exponent", "function main() {\n    let a = [1e-7, 1e21]; return a.join();\n}\n", "s:00310065002D0037002C00310065002B00320031"];
        yield return ["join-nested", "function main() {\n    let a = new Array(2);\n    let inner = new Array(2);\n    a[0] = 1;\n    inner[0] = 2;\n    inner[1] = [3.5, 4];\n    a[1] = inner;\n    return a.join(\"|\");\n}\n", "s:0031007C0032002C0033002E0035002C0034"];
        yield return ["join-shared", "function main() {\n    let child = new Array(5);\n    child[0] = 1;\n    child[2] = undefined;\n    child[3] = null;\n    child[4] = true;\n    let a = new Array(3);\n    a[0] = child;\n    a[1] = child;\n    a[2] = 7;\n    let out = new Array(3);\n    out[0] = a.join(\"|\");\n    out[1] = a[0] === a[1];\n    out[2] = a;\n    return out;\n}\n", "[s:0031002C002C002C002C0074007200750065007C0031002C002C002C002C0074007200750065007C0037,true,[[1,H,U,null,true],[1,H,U,null,true],7]]"];
        yield return ["join-deep-shared", "function main() {\n    let child = new Array(3);\n    child[0] = 1;\n    child[2] = true;\n    let inner = [child, child];\n    let a = new Array(2);\n    a[0] = inner;\n    a[1] = 7;\n    let out = new Array(3);\n    out[0] = a.join(\"|\");\n    out[1] = inner[0] === inner[1];\n    out[2] = a;\n    return out;\n}\n", "[s:0031002C002C0074007200750065002C0031002C002C0074007200750065007C0037,true,[[[1,H,true],[1,H,true]],7]]"];
        yield return ["join-thresholds", "function main() {\n    let a = [1e-7, 1e-6, 1e20, 1e21, -0.0];\n    return a.join(\"|\");\n}\n", "s:00310065002D0037007C0030002E003000300030003000300031007C003100300030003000300030003000300030003000300030003000300030003000300030003000300030007C00310065002B00320031007C0030"];
    }

    [Theory]
    [MemberData(nameof(PositionRows))]
    public Task ArrayPositions_UsePrimitiveBounds(string id, string source, string expected) => Verify(id, source, expected);

    [Theory]
    [MemberData(nameof(DepthRows))]
    public Task ArrayFlat_UsesPrimitiveDepth(string id, string source, string expected) => Verify(id, source, expected);

    [Theory]
    [MemberData(nameof(JoinRows))]
    public Task ArrayJoin_FormatsOrdinaryPrimitiveFields(string id, string source, string expected) => Verify(id, source, expected);

    private static async Task Verify(string id, string source, string expected)
    {
        string dir = Path.Combine(Path.GetTempPath(), "typesharp-array-primitive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, id + ".ts");
            await File.WriteAllTextAsync(file, source);
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceFile(file).BuildAsync();
            TsValue? value = runtime.Invoke("main");
            string actual = Display(value);
            Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), "C334 observed: " + actual);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Display(TsValue? x) => x switch
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
        _ => throw new InvalidOperationException("Unexpected carrier: " + x?.GetType().Name)
    };
}
