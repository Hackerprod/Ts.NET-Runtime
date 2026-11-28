using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TypeSharp.Hosting;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

public sealed class NativeUint8ArrayOwnConversionTests
{
    // Exact public Hosting witnesses, with frozen source bytes, IDs, roles,
    // and independently observed Node return values. No fixture rewriting.
    [Theory]
    [InlineData("construct-controls", "2323c8d189361c7ff950329443813e88e1e04b33687003c1518e20c73572b311", "CONTROL_PASS", "function main() {\n    let a: Uint8Array = new Uint8Array([257, -2, 5]);\n    let v: Uint8Array = a.subarray(1);\n    let b: Uint8Array = new Uint8Array(v);\n    v[0] = 9;\n    return [new Uint8Array(), new Uint8Array(2), a, v, b, v === b];\n}\n", "[b:[],b:[0,0],b:[1,9,5],b:[9,5],b:[254,5],false]")]
    [InlineData("construct-length", "80b3c8b69df75c999c339ba317a33894b444e09ccf34477323894ff71840385b", "VALUE_RED", "function main() {\n    let trace = 0;\n    let length = {valueOf: () => { trace = trace * 10 + 1; return 2; }, toString: () => { trace = trace * 10 + 9; return \"1\"; }};\n    let x: Uint8Array = new Uint8Array({length, \"0\": 12, \"1\": 23});\n    return [trace, x.length, x];\n}\n", "[1,2,b:[12,23]]")]
    [InlineData("construct-elements", "86cf40b417bad5405c243c89e04f684bc3f9d6645e396fb7c9092aaa0b7209af", "VALUE_RED", "function main() {\n    let trace = 0;\n    let a = {valueOf: () => { trace = trace * 10 + 1; return 258; }};\n    let b = {valueOf: () => { trace = trace * 10 + 2; return {}; }, toString: () => { trace = trace * 10 + 3; return \"-2\"; }};\n    let x: Uint8Array = new Uint8Array([a, b]);\n    let y: Uint8Array = new Uint8Array({length: 1, \"0\": a});\n    return [trace, x, y];\n}\n", "[1231,b:[2,254],b:[2]]")]
    [InlineData("construct-set-elements", "78bd6578774ea99af7a541c765248c31740c644ce5515b96dfa63b16de2db6f2", "VALUE_RED", "function main() {\n    let trace = 0;\n    let a = {valueOf: () => { trace = trace * 10 + 1; return 4; }};\n    let b = {toString: () => { trace = trace * 10 + 2; return \"8\"; }};\n    let x: Uint8Array = new Uint8Array(new Set([a as any, b as any]));\n    return [trace, x];\n}\n", "[12,b:[4,8]]")]
    [InlineData("construct-arraylike-source", "d934a57bb6697c024aa5868ab2b76d7f237c2e0d45fbbec30e4e4c2c0e1fc583", "CONTROL_PASS", "function main() {\n    let trace = 0;\n    let input = {length: 1, \"0\": 19, valueOf: () => { trace = 9; return 3; }};\n    let x: Uint8Array = new Uint8Array(input);\n    return [trace, x];\n}\n", "[0,b:[19]]")]
    [InlineData("slice-bounds", "624294742b9b9b48d9d35f9c44269246b34ff9f0c863eeaa6a60fa7907c6713f", "VALUE_RED", "function main() {\n    let trace = 0;\n    let x: Uint8Array = new Uint8Array([10,20,30,40]);\n    let begin = {valueOf: () => { trace = trace * 10 + 1; return 1.8; }};\n    let end = {valueOf: () => { trace = trace * 10 + 2; return {}; }, toString: () => { trace = trace * 10 + 3; return \"-1\"; }};\n    let y: Uint8Array = x.slice(begin, end);\n    x[1] = 99;\n    return [trace, x, y, x === y];\n}\n", "[123,b:[10,99,30,40],b:[20,30],false]")]
    [InlineData("subarray-bounds", "8b7a8f76ed3f97b6c747caaa3105e244743c9b6843f812e3714af127eb7419db", "VALUE_RED", "function main() {\n    let trace = 0;\n    let x: Uint8Array = new Uint8Array([10,20,30,40,50]);\n    let outer: Uint8Array = x.subarray(1);\n    let begin = {valueOf: () => { trace = trace * 10 + 1; return -2; }};\n    let end = {toString: () => { trace = trace * 10 + 2; return \"3\"; }};\n    let y: Uint8Array = outer.subarray(begin, end);\n    y[0] = 77;\n    return [trace, x, outer, y, y.length, y.byteLength];\n}\n", "[12,b:[10,20,30,77,50],b:[20,30,77,50],b:[77],1,1]")]
    [InlineData("range-controls", "8f27ca139babf55e297e8742e913d26f83b35c05c029f76d063f9d18dfbd9b7b", "CONTROL_PASS", "function main() {\n    let x: Uint8Array = new Uint8Array([10,20,30,40]);\n    let a: Uint8Array = x.slice(1, undefined);\n    let b: Uint8Array = x.slice(1, null);\n    let c: Uint8Array = x.subarray(-2);\n    let d: Uint8Array = x.subarray(1, null);\n    x[2] = 99;\n    return [a, b, c, d, a.length, c.byteLength];\n}\n", "[b:[20,30,40],b:[],b:[99,40],b:[],3,2]")]
    [InlineData("set-coercions", "ee0bed706fc9566c892c68763be4c5266f7f9b38bdef57b939896ccbd31773ca", "VALUE_RED", "function main() {\n    let trace = 0;\n    let x: Uint8Array = new Uint8Array([10,20,30,40]);\n    let offset = {valueOf: () => { trace = trace * 10 + 1; return 1; }};\n    let length = {valueOf: () => { trace = trace * 10 + 2; return 2; }};\n    let a = {valueOf: () => { trace = trace * 10 + 3; return 257; }};\n    let b = {valueOf: () => { trace = trace * 10 + 4; return {}; }, toString: () => { trace = trace * 10 + 5; return \"-1\"; }};\n    let ret = x.set({length, \"0\": a, \"1\": b}, offset);\n    return [trace, ret, x];\n}\n", "[12345,U,b:[10,1,255,40]]")]
    [InlineData("set-live-array", "c4ae2f93de72679d37a01f86d4d5c2a4d159071e6ddb3fefd72a42c4017ec604", "VALUE_RED", "function main() {\n    let x: Uint8Array = new Uint8Array([10,20,30,40]);\n    let a: any[] = [0,0];\n    a[0] = {valueOf: () => { a[1] = 42; return 17; }};\n    let ret = x.set(a, 1);\n    return [ret, x, a[1]];\n}\n", "[U,b:[10,17,42,40],42]")]
    [InlineData("set-overlap-control", "0e0cf017b3c3be21e8d1d9f23cae9122dcd1f6f79c8b6506a8f51d519fb40e60", "CONTROL_PASS", "function main() {\n    let x: Uint8Array = new Uint8Array([10,20,30,40,50]);\n    let r = x.subarray(1).set(x.subarray(0,3));\n    let a: Uint8Array = x.slice();\n    let s = x.subarray(0,3).set(x.subarray(2));\n    return [r, s, a, x];\n}\n", "[U,U,b:[10,10,20,30,50],b:[20,30,50,30,50]]")]
    [InlineData("reverse-view-control", "8f6a4911fc5fcc2938b397e22956b313e5c152f27c48066664d22c8460b2552f", "CONTROL_PASS", "function main() {\n    let x: Uint8Array = new Uint8Array([9,10,20,30,8]);\n    let v: Uint8Array = x.subarray(1,4);\n    let r: Uint8Array = v.reverse();\n    return [v === r, x, v, r, v.length, v.byteLength];\n}\n", "[true,b:[9,30,20,10,8],b:[30,20,10],b:[30,20,10],3,3]")]
    [InlineData("toreversed-view-control", "8cf5b1fd643f896c63b3c3b3d07bd218e8d0de9d762a5741a1dd97d5e341a58c", "CONTROL_PASS", "function main() {\n    let x: Uint8Array = new Uint8Array([9,10,20,30,8]);\n    let v: Uint8Array = x.subarray(1,4);\n    let r: Uint8Array = v.toReversed();\n    r[1] = 77;\n    v[0] = 99;\n    return [v === r, x, v, r];\n}\n", "[false,b:[9,99,20,30,8],b:[99,20,30],b:[30,77,10]]")]
    [InlineData("index-read-key-hint", "7f5a5618680de0bb3fd084edff86c78d20909b54fceb56385b3756905a9dd59c", "VALUE_RED", "function main() {\n    let trace = 0;\n    let x: Uint8Array = new Uint8Array([10,20,30]);\n    let a: any = {toString: () => { trace = trace * 10 + 1; return \"1\"; }, valueOf: () => { trace = trace * 10 + 9; return 2; }};\n    let b: any = {toString: () => { trace = trace * 10 + 2; return {}; }, valueOf: () => { trace = trace * 10 + 3; return 2; }};\n    return [x[a], x[b], trace];\n}\n", "[20,30,123]")]
    [InlineData("index-write-value-hint", "d5a3f92df69608124ea020811171bbc1ef28d59108599390332258e24880f415", "VALUE_RED", "function main() {\n    let trace = 0;\n    let x: Uint8Array = new Uint8Array([10,20,30]);\n    let v: Uint8Array = x.subarray(1);\n    let key: any = {toString: () => { trace = trace * 10 + 1; return \"1\"; }, valueOf: () => { trace = trace * 10 + 9; return 0; }};\n    let elt: any = {valueOf: () => { trace = trace * 10 + 2; return -3; }, toString: () => { trace = trace * 10 + 9; return \"5\"; }};\n    v[key] = elt;\n    v[0] = elt;\n    return [trace, x, v];\n}\n", "[122,b:[10,253,253],b:[253,253]]")]
    [InlineData("index-canonical-invalid-no-write", "6c911146af0e25a4c2cdde13b6d2ce1979621efe0d886deaf29b9a6205f1c576", "VALUE_RED", "function main() {\n    let trace = 0;\n    let x: Uint8Array = new Uint8Array([10,20]);\n    let e: any = {valueOf: () => { trace++; return 99; }};\n    x[\"-0\" as any] = e;\n    let a = trace;\n    x[3] = e;\n    return [a, trace, x[\"-0\" as any], x[3], x];\n}\n", "[1,2,U,U,b:[10,20]]")]
    [InlineData("fields-and-computed-keys", "07de30a02e1e5f603c762ec1a3641aceadf080ec6b896d1e07f68ae0406f9616", "VALUE_RED", "function main() {\n    let trace = 0;\n    let x: Uint8Array = new Uint8Array([10,20,30]).subarray(1);\n    let key1: any = {toString: () => { trace = trace * 10 + 1; return \"length\"; }};\n    let key2: any = {toString: () => { trace = trace * 10 + 2; return \"byteLength\"; }};\n    return [x.length, x.byteLength, x[key1], x[key2], trace];\n}\n", "[2,2,2,2,12]")]
    [InlineData("fields-and-index-controls", "2fba6c4cee7301bd2420187ef94bb7ed81f2769f4f54702027a6a6d17c545447", "CONTROL_PASS", "function main() {\n    let x: Uint8Array = new Uint8Array([10,20,30]).subarray(1);\n    let y = x.subarray(1,1);\n    return [x.length, x.byteLength, x[\"length\" as any], x[\"byteLength\" as any],\n            x[0], x[1], x[2], x[\"-0\" as any], y.length, y.byteLength];\n}\n", "[2,2,2,2,20,30,U,U,0,0]")]

    public async Task NativeUint8ArrayUsesOwnMethodsOnlyAtRepresentedTypedOperations(
        string id, string sha256, string role, string source, string expected)
    {
        Assert.True(role == "VALUE_RED" || role == "CONTROL_PASS");
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var folder = Directory.CreateTempSubdirectory("typesharp-uint8-own-");
        try
        {
            var script = Path.Combine(folder.FullName, id + ".ts");
            await File.WriteAllBytesAsync(script, bytes);
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceFile(script).BuildAsync();
            Assert.Equal(expected, Display(runtime.Invoke("main")));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static string Display(TsValue? x) => x switch
    {
        TsArrayValue a => "[" + string.Join(",", Enumerable.Range(0, a.Value.Count).Select(i =>
            a.Value.IsHole(i) ? "H" : Display(a.Value.Get(i)))) + "]",
        TsUint8ArrayValue bytes => "b:[" + string.Join(",", Enumerable.Range(0, bytes.Length).Select(i => bytes.Get(i).ToString(CultureInfo.InvariantCulture))) + "]",
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
