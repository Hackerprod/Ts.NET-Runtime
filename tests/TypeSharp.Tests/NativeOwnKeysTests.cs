using System.Buffers.Binary;
using System.Globalization;
using TypeSharp.Hosting;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

public sealed class NativeOwnKeysTests
{
    public static IEnumerable<object[]> Sources()
    {
        yield return ["spread", "spread-string", """
            function main() { let copy = { ...'😀Z' }; return ['0' in copy, copy[0], copy[1], copy[2], 'length' in copy]; }
            """ + "\n", "[true,s:D83D,s:DE00,s:005A,false]", 0];
        yield return ["spread", "spread-byte-view", """
            function main() { let bytes = new Uint8Array([4, 5, 6, 7]); let view = bytes.subarray(1, 3); let copy = { ...view }; bytes[1] = 99; return ['0' in copy, copy[0], copy[1], '2' in copy, 'length' in copy]; }
            """ + "\n", "[true,5,6,false,false]", 0];
        yield return ["spread", "spread-object-overwrite", """
            function main() { let item = { x: 2, present: undefined }; let copy = { x: 1, ...item, x: 3, tail: 5 }; return [copy.x, 'present' in copy, copy.present, copy.tail]; }
            """ + "\n", "[3,true,U,5]", 0];
        yield return ["spread", "spread-empty-carriers", """
            function main() { let other = null; let copy = { ...other, ...undefined, ...true, ...123, k: 4 }; let seen = ''; for (let key in copy) { seen = seen + key; } return seen; }
            """ + "\n", "s:006B", 0];
        yield return ["spread", "spread-sparse-overwrite", """
            function main() { let row = new Array(3); row[0] = 10; row[2] = undefined; let copy = { [0]: 3, ...row, tail: 8 }; return [copy[0], '1' in copy, '2' in copy, copy[2], copy.tail]; }
            """ + "\n", "[10,false,true,U,8]", 0];
        yield return ["rest", "rest-string", """
            function main() { let { length: size, ...copy } = '😀Z'; return [size, copy[0], copy[1], copy[2], 'length' in copy]; }
            """ + "\n", "[3,s:D83D,s:DE00,s:005A,false]", 0];
        yield return ["rest", "rest-byte-view", """
            function main() { let bytes = new Uint8Array([4, 5, 6, 7]); let view = bytes.subarray(1, 3); let { length: size, ...copy } = view; bytes[2] = 99; return [size, copy[0], copy[1], '2' in copy, 'length' in copy]; }
            """ + "\n", "[2,5,6,false,false]", 0];
        yield return ["rest", "rest-number", """
            function main() { let { ...copy } = 42; return ['0' in copy]; }
            """ + "\n", "[false]", 0];
        yield return ["rest", "rest-boolean", """
            function main() { let { ...copy } = true; return ['0' in copy]; }
            """ + "\n", "[false]", 0];
        yield return ["rest", "rest-named-alias", """
            function main() { let { x: selected, ...copy } = { x: 2, present: undefined, y: 4 }; return [selected, 'x' in copy, 'present' in copy, copy.present, copy.y]; }
            """ + "\n", "[2,false,true,U,4]", 0];
        yield return ["rest", "rest-sparse", """
            function main() { let row = new Array(3); row[0] = 10; row[2] = undefined; let { length: size, ...copy } = row; return [size, copy[0], '1' in copy, '2' in copy, copy[2], 'length' in copy]; }
            """ + "\n", "[3,10,false,true,U,false]", 0];
        yield return ["rest", "sparse-exclusion", """
            function main() { let row = new Array(4); row[0] = 10; row[2] = undefined; let zero = "0"; let hole = "1"; let missing = "9"; let present = "2";
                let { [zero]: selected, [hole]: fallback = 41, [missing]: out = 42, [present]: u = 43, ...copy } = row;
                let kept = {...row};
                return [selected, fallback, out, u, "2" in kept, kept[2], "0" in copy, "1" in copy, "2" in copy, "9" in copy, row.length, row[0], "2" in row]; }
            """ + "\n", "[10,41,42,43,true,U,false,false,false,false,4,10,true]", 0];
        yield return ["rest", "sparse-kept-selected", """
            function main() { let row = new Array(4); row[0] = 10; row[2] = undefined; let zero = "0"; let hole = "1";
                let { [zero]: selected, [hole]: fallback = 41, ...copy } = row;
                return [selected, fallback, "0" in copy, "1" in copy, "2" in copy, copy[2], "length" in copy, row.length, row[0], "2" in row]; }
            """ + "\n", "[10,41,false,false,true,U,false,4,10,true]", 0];
        yield return ["for-in", "forin-byte-view", """
            function main() { let view = new Uint8Array([2, 7, 8, 9]).subarray(1); let seen = ''; for (let key in view) { seen = seen + key; } return seen; }
            """ + "\n", "s:003000310032", 0];
        yield return ["for-in", "forin-order", """
            function main() { let seen = ''; for (let key in { a: 1, '10': 1, '2': 1, '02': 1, b: 1 }) { seen = seen + key + '|'; } return seen; }
            """ + "\n", "s:0032007C00310030007C0061007C00300032007C0062007C", 0];
        yield return ["for-in", "forin-string", """
            function main() { let seen = ''; let input = '😀Z'; for (let key in input) { seen = seen + key; } return seen; }
            """ + "\n", "s:003000310032", 0];
        yield return ["for-in", "forin-sparse", """
            function main() { let row = new Array(3); row[0] = 10; row[2] = undefined; let seen = ''; for (let key in row) { seen = seen + key; } return seen; }
            """ + "\n", "s:00300032", 0];
        yield return ["for-in", "byte-roots-empty-copy", """
            function main() { let root = new Uint8Array([24]); let empty = root.subarray(1, 1); let spread = {...root}; let {length: n, ...rest} = root;
                let {length: m, ...none} = empty; let a = ""; let b = ""; for (let k in root) a = a + k; for (let k in empty) b = b + k;
                return ["0" in spread, spread[0], "length" in spread, "0" in rest, rest[0], "0" in none, a, b]; }
            """ + "\n", "[true,24,false,true,24,false,s:0030,s:]", 0];
        yield return ["computed", "rest-computed-declaration", """
            function main() { let calls = 0; function pick() { calls = calls + 1; return 'x'; } let { [pick()]: selected, ...copy } = { x: 2, y: 4 }; return [selected, calls, 'x' in copy, copy.y]; }
            """ + "\n", "[2,1,false,4]", 0];
        yield return ["computed", "rest-computed-assignment", """
            function main() { let calls = 0; function pick() { calls = calls + 1; return 'x'; } let selected = 0; let copy = { x: 0, y: 0 }; ({ [pick()]: selected, ...copy } = { x: 2, y: 4 }); return [selected, calls, 'x' in copy, copy.y]; }
            """ + "\n", "[2,1,false,4]", 0];
        yield return ["computed", "text-index-array-decl", """
            function main() { let input = [10, 20]; let key = "1"; let { [key]: selected, ...copy } = input; return [selected, "0" in copy, copy[0], "1" in copy, "length" in copy]; }
            """ + "\n", "[20,true,10,false,false]", 0];
        yield return ["computed", "text-index-array-assign", """
            function main() { let input = [10, 20]; let key = "1"; let selected = 0; let copy = { "0": selected, "1": selected }; ({ [key]: selected, ...copy } = input); return [selected, "0" in copy, copy[0], "1" in copy, "length" in copy]; }
            """ + "\n", "[20,true,10,false,false]", 0];
        yield return ["computed", "text-index-object-decl", """
            function main() { let input = { "0": 10, "1": 20 }; let key = "1"; let { [key]: selected, ...copy } = input; return [selected, "0" in copy, copy[0], "1" in copy, "length" in copy]; }
            """ + "\n", "[20,true,10,false,false]", 0];
        yield return ["computed", "text-index-string-assign", """
            function main() { let input = '😀Z'; let key = "1"; let selected = ''; let copy = { "0": selected, "1": selected }; ({ [key]: selected, ...copy } = input); return [selected, "0" in copy, copy[0], "1" in copy, "length" in copy]; }
            """ + "\n", "[s:DE00,true,s:D83D,false,false]", 0];
        yield return ["computed", "text-index-bytes-decl", """
            function main() { let input = new Uint8Array([99, 10, 20, 98]).subarray(1, 3); let key = "1"; let { [key]: selected, ...copy } = input; return [selected, "0" in copy, copy[0], "1" in copy, "length" in copy]; }
            """ + "\n", "[20,true,10,false,false]", 0];
        yield return ["computed", "quoted-minus-zero", """
            function main() { let key = "-0"; let input = { "0": 10, "1": 20, "-0": 90 }; let { [key]: selected, ...copy } = input;
                return [selected, "0" in copy, copy[0], "-0" in copy, "1" in copy, copy[1]]; }
            """ + "\n", "[90,true,10,false,true,20]", 0];
        yield return ["computed", "array-computed-length", """
            function main() { let row = [10, 20]; let key = "length"; let { [key]: selected, ...copy } = row;
                return [selected, "0" in copy, "1" in copy, "length" in copy]; }
            """ + "\n", "[2,true,true,false]", 0];
        yield return ["computed", "string-computed-length", """
            function main() { let row = '😀Z'; let key = "length"; let { [key]: selected, ...copy } = row;
                return [selected, "0" in copy, "1" in copy, "length" in copy]; }
            """ + "\n", "[3,true,true,false]", 0];
        yield return ["computed", "string-computed-reference", """
            function referencePair() { let row = "ab"; let key = "toUpperCase"; let { [key]: selected } = row; return [selected, row[key]]; }
            function main() {
                let row = "ab"; let key = "toUpperCase"; let { [key]: selected, ...copy } = row;
                return [typeof selected, selected === row[key], "0" in copy, "toUpperCase" in copy];
            }
            """ + "\n", "[s:00660075006E006300740069006F006E,true,true,false]", 0];
        yield return ["computed", "timing-declaration", """
            function main() { let trace = ""; let count = 0; let row = { a: undefined, b: undefined, c: 30, d: 40, e: 50 };
                function rhs() { trace = trace + "R"; count = count + 1; return row; }
                function before() { trace = trace + "A"; return 10; }
                function pick(previous) { trace = trace + (previous === 10 ? "B" : "?"); return "b"; }
                function fallback() { trace = trace + "D"; return 20; }
                function next(previous) { trace = trace + (previous === 20 ? "C" : "?"); return "c"; } let { a: first = before(), [pick(first)]: selected = fallback(), [next(selected)]: third = 31, d: last, ...copy } = rhs(); return [count, trace, first, selected, third, last, "a" in copy, "b" in copy, "c" in copy, "d" in copy, "e" in copy, copy.e]; }
            """ + "\n", "[1,s:00520041004200440043,10,20,30,40,false,false,false,false,true,50]", 0];
        yield return ["computed", "timing-assignment", """
            function main() { let trace = ""; let count = 0; let row = { a: undefined, b: undefined, c: 30, d: 40, e: 50 };
                function rhs() { trace = trace + "R"; count = count + 1; return row; }
                function before() { trace = trace + "A"; return 10; }
                function pick(previous) { trace = trace + (previous === 10 ? "B" : "?"); return "b"; }
                function fallback() { trace = trace + "D"; return 20; }
                function next(previous) { trace = trace + (previous === 20 ? "C" : "?"); return "c"; } let first = 0; let selected = 0; let third = 0; let last = 0; let copy = { e: 0 };
                let result = ({ a: first = before(), [pick(first)]: selected = fallback(), [next(selected)]: third = 31, d: last, ...copy } = rhs());
                return [count, trace, first, selected, third, last, "a" in copy, "b" in copy, "c" in copy, "d" in copy, "e" in copy, copy.e, result === row]; }
            """ + "\n", "[1,s:00520041004200440043,10,20,30,40,false,false,false,false,true,50,true]", 0];
        yield return ["computed", "negzero-object-decl-zeroeq-r4", """
            function keyProof() { return Number("-0"); }
            function main() { let input = { "0": 10, "1": 20, "-0": 90 }; let key = keyProof(); let { [key]: selected, ...copy } = input; return [key === 0, selected, "0" in copy, "1" in copy, copy[1], "-0" in copy]; }
            """ + "\n", "[true,10,false,true,20,true]", 0];
        yield return ["computed", "negzero-array-assign-zeroeq-r4", """
            function keyProof() { return Number("-0"); }
            function main() { let input = [10, 20]; let key = keyProof(); let selected = 0; let copy = { "0": selected, "1": selected }; ({ [key]: selected, ...copy } = input); return [key === 0, selected, "0" in copy, "1" in copy, copy[1], "-0" in copy]; }
            """ + "\n", "[true,10,false,true,20,false]", 0];
        yield return ["computed", "negzero-string-decl-zeroeq-r4", """
            function keyProof() { return Number("-0"); }
            function main() { let input = '😀Z'; let key = keyProof(); let { [key]: selected, ...copy } = input; return [key === 0, selected, "0" in copy, "1" in copy, copy[1], "-0" in copy]; }
            """ + "\n", "[true,s:D83D,false,true,s:DE00,false]", 0];
        yield return ["computed", "negzero-bytes-assign-zeroeq-r4", """
            function keyProof() { return Number("-0"); }
            function main() { let input = new Uint8Array([99, 10, 20, 98]).subarray(1, 3).subarray(0, 2); let key = keyProof(); let selected = 0; let copy = { "0": selected, "1": selected }; ({ [key]: selected, ...copy } = input); return [key === 0, selected, "0" in copy, "1" in copy, copy[1], "-0" in copy]; }
            """ + "\n", "[true,10,false,true,20,false]", 0];
        yield return ["guards", "ordinary-missing-null", """
            function main() { let row = { u: undefined, n: null, k: 7 }; let key = "missing"; let { [key]: missing, ...copy } = row;
                return [missing, copy.u, copy.n, "missing" in copy, "u" in copy, "n" in copy]; }
            """ + "\n", "[null,U,null,false,true,true]", 0];
        yield return ["guards", "string-reference-legacy", """
            function referencePair() { let row = "ab"; let key = "toUpperCase"; let { [key]: selected } = row; return [selected, row[key]]; }
            function main() { let pair = referencePair(); return [typeof pair[0], pair[0] === pair[1]]; }
            """ + "\n", "[s:00660075006E006300740069006F006E,true]", 0];
        yield return ["guards", "held-boxed-target", """
            function main() { let selected = 0; let copy = { x: 0, y: 0 }; function key() { return "x"; } function observe() { return selected; }
                ({ [key()]: selected, ...copy } = { x: 2, y: 4 }); return [observe(), "x" in copy, copy.y]; }
            """ + "\n", "[2,true,4]", 0];
        yield return ["guards", "held-nested-target", """
            function main() { let selected = 0; let inner = 0; let copy = { x: 0, y: 0 }; function key() { return "x"; }
                ({ [key()]: selected, nested: { item: inner }, ...copy } = { x: 2, nested: { item: 5 }, y: 4 });
                return [selected, inner, "x" in copy, copy.y]; }
            """ + "\n", "[2,5,true,4]", 0];
        yield return ["guards", "held-effectful-target", """
            function main() { let trace = ""; let target = [0]; let copy = { x: 0, y: 0 };
                function address() { trace = trace + "T"; return 0; } function key() { trace = trace + "K"; return "x"; }
                ({ [key()]: target[address()], ...copy } = { x: 2, y: 4 }); return [trace, target[0], "x" in copy, copy.y]; }
            """ + "\n", "[s:0054004B,2,true,4]", 0];
        yield return ["guards", "held-effectful-rest", """
            function main() { let trace = ""; let selected = 0; let target = [{ x: 0, y: 0 }];
                function address() { trace = trace + "T"; return 0; } function key() { trace = trace + "K"; return "x"; }
                ({ [key()]: selected, ...target[address()] } = { x: 2, y: 4 }); return [trace, selected, "x" in target[0], target[0].y]; }
            """ + "\n", "[s:004B0054,2,true,4]", 0];
        yield return ["wire", "wire-v2-i32-r4", """
            function main() { function inner() { let operand = 220; return operand - 199; } let {a,...copy} = {a: 21, b: 42}; return copy.b + inner() - a; }
            """ + "\n", "42", 2];
        yield return ["wire", "wire-v3", """
            function* gen() { try { yield 1; } finally { yield 31; } }
            function main() { let g = gen(); g.next(); let r = g.return?.(7); return r.value; }
            """ + "\n", "31", 3];
        yield return ["wire", "wire-computed-v4", """
            function main() { function key() { return "x"; } function add() { return 9; } let { [key()]: selected, ...copy } = { x: 2, y: 4 }; return [selected, "x" in copy, copy.y, add()]; }
            """ + "\n", "[2,false,4,9]", 4];
        yield return ["wire", "wire-computed-generator-v4", """
            function key() { return "x"; } function extra() { return 9; }
            function* gen() { try { yield 1; } finally { let { [key()]: selected, ...copy } = { x: 2, y: 4 }; yield [selected, "x" in copy, copy.y, extra()]; } }
            function main() { let g = gen(); g.next(); let r = g.return?.(7); return r.value; }
            """ + "\n", "[2,false,4,9]", 4];
        yield return ["computed-local", "colliding-declaration", """
            function main() {
              let $destructure_0_key_0_raw = 71;
              let $destructure_0_key_0_normalized = 72;
              let key = 'x';
              let { [key]: selected, ...copy } = { x: 2, y: 4 };
              return [selected, $destructure_0_key_0_raw, $destructure_0_key_0_normalized, 'x' in copy, copy.y];
            }
            """ + "\n", "[2,71,72,false,4]", 4];
        yield return ["computed-local", "colliding-assignment", """
            function main() {
              let $destructure_assign_0_key_0_raw = 81;
              let $destructure_assign_0_key_0_normalized = 82;
              let key = 'x';
              let selected = 0;
              let copy = { x: 0, y: 0 };
              ({ [key]: selected, ...copy } = { x: 2, y: 4 });
              return [selected, $destructure_assign_0_key_0_raw, $destructure_assign_0_key_0_normalized, 'x' in copy, copy.y];
            }
            """ + "\n", "[2,81,82,false,4]", 4];
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task OwnSlotsAndArtifactEnvelope(string group, string id, string source, string expected, int version)
    {
        var dir = Directory.CreateTempSubdirectory("typesharp-own-slots-");
        try
        {
            var path = Path.Combine(dir.FullName, id + ".ts");
            await File.WriteAllTextAsync(path, source);
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceFile(path).BuildAsync();
            Assert.Equal(expected, Show(runtime.Invoke("main")));
            if (id.StartsWith("negzero-", StringComparison.Ordinal))
                Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(Assert.IsType<TsFloat64Value>(runtime.Invoke("keyProof")).Value));
            var raw = runtime.ActiveGeneration!.Modules[id].Bytecode;
            BytecodeVerifier.Verify(raw);
            if (version == 0)
                return;
            using var payload = new MemoryStream();
            BytecodeSerializer.Serialize(payload, raw);
            Assert.Equal(version, BinaryPrimitives.ReadInt32LittleEndian(payload.ToArray().AsSpan(4)));
            payload.Position = 0;
            var readback = BytecodeSerializer.Deserialize(payload);
            Assert.Equal(payload.Length, payload.Position);
            Assert.Equal(expected, Show(new TypeSharp.VM.Interpreter.Interpreter().Execute(readback, "main")));
            Assert.NotEmpty(group);
        }
        finally { dir.Delete(recursive: true); }
    }

    private static string Show(TsValue? value) => value switch
    {
        TsArrayValue a => "[" + string.Join(",", Enumerable.Range(0, a.Value.Count)
            .Select(i => a.Value.IsHole(i) ? "H" : Show(a.Value.Get(i)))) + "]",
        TsInt32Value i => i.Value.ToString(CultureInfo.InvariantCulture),
        TsInt64Value i => i.Value.ToString(CultureInfo.InvariantCulture),
        TsFloat64Value f => f.Value.ToString("R", CultureInfo.InvariantCulture),
        TsStringValue s => "s:" + string.Concat(s.Value.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture))),
        TsBoolValue b => b.Value ? "true" : "false",
        TsVoid => "U",
        TsNull => "null",
        _ => throw new InvalidOperationException("Unexpected own-slot result: " + value?.GetType().Name),
    };
}
