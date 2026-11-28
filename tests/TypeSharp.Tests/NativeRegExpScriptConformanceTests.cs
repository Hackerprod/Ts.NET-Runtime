using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TypeSharp.Hosting;
using TypeSharp.VM.Memory;
using Xunit;

namespace TypeSharp.Tests;

public sealed class NativeRegExpScriptConformanceTests
{
    // Frozen original TS source and independent oracle per row. Parser reds are
    // ordinary valid sources: success requires both compile and invoke, not a
    // catch or replacement for the previously missing value.
    [Theory]
    [InlineData("ts337-scout/flags-order", "d4242fd2e4ea708f8010bda0eca2f2fa7dd66af534c27281723ed082dcb918ad", "function main() { return [String(/a/yigs)==='/a/gisy',String(/a/ig)==='/a/gi']; }\n", "[true,true]")]
    [InlineData("ts337-scout/flags-match", "87eed9891100c227abc3543d3103b347e31355661b780f24167bd2bbccf99564", "function main() { return [/a/i.test('A'),/^a$/m.test('b\\na\\nb'),/^.$/s.test('\\n'),/a/u.test('a'),/a/g.test('ba'),/a/y.test('ab')]; }\n", "[true,true,true,true,true,true]")]
    [InlineData("ts337-scout/line-anchor", "86dcbcd84525b8e66dd1e20681cdf4b689c144be7a543ad27bb4ecf52963fe1d", "function main() { return [/^a$/.test('a\\n'),/^a$/m.test('a\\rb'),/^a$/m.test('b\\u2028a'),/^a$/m.test('ab')]; }\n", "[false,true,true,false]")]
    [InlineData("ts337-scout/line-dot", "dfa58448c6e1db9ebb400c5db9951765a07e23ac40f8f2621a4b119baf5dff64", "function main() { return [/^.$/.test('\\r'),/^.$/.test('\\n'),/^.$/.test('\\u2028'),/^.$/.test('\\u2029'),/^.$/s.test('\\r')]; }\n", "[false,false,false,false,true]")]
    [InlineData("ts337-scout/char-classes", "7f7e01daf295ef166e6e929f54b0f80b2b63a15d474a0b1bcdde18e8e0a6cf9d", "function main() { return [/^\\d$/.test('\u0662'),/^\\w$/.test('\u00e9'),/^\\s$/.test('\\ufeff'),/^\\s$/.test('\\u0085'),/^\\d$/.test('2'),/^\\w$/.test('_'),/^\\s$/.test('\\t')]; }\n", "[false,false,true,false,true,true,true]")]
    [InlineData("ts337-scout/word-boundary", "50634edffc7eb3f10fab001842e8f758545a03f7c34f37b2b52f55c6c411e39b", "function main() { return [/\\ba/.test('\u00e9a'),/\\b\u00e9/.test('\u00e9'),/\\ba/.test(' a')]; }\n", "[true,false,true]")]
    [InlineData("ts337-scout/coerce-cursor-array", "13d1ca1518d0d90e907af7bd3e669ecb26ca85e26d2b66d5ed904ea303032687", "function main() { let r=/b/y; r.lastIndex=[1]; let a=r.test('ab'); let b=r.lastIndex; r.lastIndex=[[1]]; let c=r.test('ab'); let d=r.lastIndex; r.lastIndex=[]; return [a,b,c,d,r.test('ba'),r.lastIndex]; }\n", "[true,2,true,2,true,1]")]
    [InlineData("ts337-scout/cursor-global-sticky", "c2562bf5ddda00ed40d46f7affc9f40e342027597e341456eeff240b0a1bdaeb", "function main() { let r=/a/g; let a=r.test('\ud83d\ude00a'); let b=r.lastIndex; let c=r.test('\ud83d\ude00a'); let d=r.lastIndex; let y=/a/gy; y.lastIndex=1; let e=y.test('ba'); let f=y.lastIndex; y.lastIndex=0; let g=y.test('ba'); let h=y.lastIndex; let z=/b*/g; z.lastIndex=1; let i=z.test('ac'); let j=z.lastIndex; return [a,b,c,d,e,f,g,h,i,j,z.test('ac'),z.lastIndex]; }\n", "[true,3,false,0,true,2,false,0,true,1,true,1]")]
    [InlineData("ts337-scout/cursor-nonglobal", "41f6ade6e4b5541973590ee11e9aa1f0f3c2542b8c52ea0097b0dbef8ccab161", "function main() { let r=/a/; r.lastIndex=7; let a=r.test('a'); let b=r.lastIndex; return [a,b,r.test('b'),r.lastIndex]; }\n", "[true,7,false,7]")]
    [InlineData("ts337-scout/cursor-receivers", "af9181f9eb8c38e95345265eb8bd0807735d0e63f3ea8eb399aa3f1577a19221", "function main() { function make(){return /a/g;} let a=make(); let b=make(); let c=a; return [a===c,a!==b,a.test('a'),c.lastIndex,b.lastIndex,a.test===b.test]; }\n", "[true,true,true,1,0,true]")]
    [InlineData("ts337-scout/member-computed", "8d0cb748584c9a9956ba48fe48b3b641c6f81b71d6d1f46e1fa759adbb35afaa", "function main() { let r=/a/y; let key='lastIndex'; r[key]=1; let a=r['test']('ba'); let b=r[key]; r.lastIndex=0; return [a,b,r?.['test']('ab'),r.lastIndex,r['test']===r.test]; }\n", "[true,2,true,1,true]")]
    [InlineData("ts337-scout/property-in", "09ed79e42a00cc5f4602b691c2c60e0b9c9d4b4c88d062f704ec6896dc0ddca2", "function main() { let r=/x/; return ['lastIndex' in r,'test' in r,'missing' in r]; }\n", "[true,true,false]")]
    [InlineData("ts337-scout/property-key-coercion", "b999b9b7624d874efe5e1afb4feabcf821223e051c93e0c9bae461e0d4c2578b", "function main() { let r=/x/y; let k=['lastIndex']; r[k]=1; let a=r[k]; return [a,r.test('ax'),r.lastIndex]; }\n", "[1,true,2]")]
    [InlineData("ts337-scout/literal-text", "0c4717c3d6b337a0461edef4117e004578d1960d09d36d10cd4146c9ed8ba293", "function main() { return [String(/ a /i)==='/ a /i',/^[a b]$/.test(' '),`v:${/^a  b$/i.test('A  B')}`==='v:true',12/3===4]; }\n", "[true,true,true,true]")]
    [InlineData("ts337-scout/literal-escaped-slash", "83d314bb44208b64b0c841ca03a02a1d8c5392ce065d171b5c41982d3f51f3f6", "function main() { return [/a\\/b/.test('a/b'), String(/a\\/b/i)==='/a\\\\/b/i']; }\n", "[true,true]")]
    [InlineData("ts337-scout/member-dot-order", "4accd3336687854032d10311841ebbe6dcfa7dc6bafdaeac05681a581cd2954d", "function main() {\n    let state={s:''}; let r=/a/g;\n    function recv(){state.s=state.s+'r'; return r;}\n    function arg(){state.s=state.s+'a'; return 'ba';}\n    return [recv().test(arg()),state.s==='ra',r.lastIndex];\n}\n", "[true,true,2]")]
    [InlineData("ts337-scout-R2/input-scalars", "e1f76ff4ea083772e36c61f8ad76c264f624674768eed5da41ff7180c4815c39", "function main() { let r=/^undefined$/; return [r.test(),r.test(undefined),/^null$/.test(null),/^false$/.test(false),/^12$/.test(12n),/^42$/.test(42),/^NaN$/.test(Number('NaN')), /^Infinity$/.test(Number('Infinity'))]; }\n", "[true,true,true,true,true,true,true,true]")]
    [InlineData("ts337-scout-R2/input-number-text", "9f8f8ac837bd4e476113ea6698283dc6eb66bcece0141859dc38724489eb5213", "function main() { return [/^0$/.test(-0),/^[1]e-7$/.test(0.0000001),/^[1]e[+]21$/.test(1e21)]; }\n", "[true,true,true]")]
    [InlineData("ts337-scout-R2/input-array-object", "09db5933a6cbccb636c7dfb7693e0f1b7149a5a38a50d914bd204d11d36093f2", "function main() { let items:any[]=[1,null,2]; return [/^[1],,2$/.test(items),/^\\[object Object\\]$/.test({a:1}),/^$/.test([]),/^3,4$/.test(new Uint8Array([3,4])),/^1,2$/.test([1,2])]; }\n", "[true,true,true,true,true]")]
    [InlineData("ts337-scout-R2/input-own-methods", "17977bc7d0153df198cddedf083049af981637d5e5f8754ac8fc009fc1de27c9", "function main() { let r=/b/g; let x={toString:()=> 'ab'}; let y={toString:()=> ({}),valueOf:()=> 'b'}; return [/^ab$/.test(x),/^b$/.test(y),r['test'](x),r.lastIndex]; }\n", "[true,true,true,2]")]
    [InlineData("ts337-scout-R2/input-before-cursor", "cb348633a3c6c878a4933fdb03b15305c2bfa7c87e613fb0fc4d280af443311e", "function main() { let r=/b/y; function asText(){r.lastIndex=1;return 'ab';} let x={toString:asText}; return [r.test(x),r.lastIndex]; }\n", "[true,2]")]
    [InlineData("ts337-scout-R2/cursor-own-methods", "b14e9a54c525138e76ab46b7c41a8411aa632eeaaec1867166613d1a0c2878ea", "function main() { let r=/b/y; r.lastIndex={valueOf:()=>1}; let a=r.test('ab'); let b=r.lastIndex; r.lastIndex={valueOf:()=>({}),toString:()=> '1'}; return [a,b,r.test('ab'),r.lastIndex]; }\n", "[true,2,true,2]")]
    [InlineData("ts337-scout-R2/cursor-bigint", "8fb8781a92b3544f5211b47acc773bdd487e47bb5301fca6bc301644e170a4ed", "function main() { let g=/a/g; let y=/a/y; let n=/a/; g.lastIndex=1n; y.lastIndex=1n; n.lastIndex=1n; let a=false; let b=false; let c=false; try {g.test('aa');} catch(e) {a=(e as any).name==='TypeError';} try {y.test('aa');} catch(e) {b=(e as any).name==='TypeError';} try {n.test('a');} catch(e) {c=(e as any).name==='TypeError';} return [a,b,c,g.lastIndex===1n&&y.lastIndex===1n&&n.lastIndex===1n]; }\n", "[true,true,true,true]")]
    [InlineData("ts337-scout-R2/cursor-nonglobal-method", "4fc1601905dea027ed24c6a5e5aae314c88cd070b8ebb4238f82ca68ec351460", "function main() { let r=/a/; let n=0; function asNumber(){n=n+1;return 9;} let x={valueOf:asNumber}; r.lastIndex=x; return [r.test('a'),r.lastIndex===x,n===1]; }\n", "[true,true,true]")]
    [InlineData("ts337-scout-R2/cursor-simple", "8d01d39cc658eff76139d3afc8db8020f9d4e9ed4519c95076c8924267e38814", "function main() { let r=/b/y; r.lastIndex=' 1.9 '; let a=r.test('ab'); let b=r.lastIndex; r.lastIndex=true; let c=r.test('ab'); let d=r.lastIndex; r.lastIndex=null; let e=r.test('ba'); let f=r.lastIndex; r.lastIndex=Number('Infinity'); let g=r.test('ba'); let h=r.lastIndex; r.lastIndex=2; let i=r.test('ba'); let j=r.lastIndex; r.lastIndex=-1; return [a,b,c,d,e,f,g,h,i,j,r.test('ba'),r.lastIndex]; }\n", "[true,2,true,2,true,1,false,0,false,0,true,1]")]
    [InlineData("ts337-scout-R2/cursor-input-before-cursor", "230b13b8c1b9156014c16d26f452eb71d8c4a2ce0bad34ba831d424e3584d39e", "function main() { let s=''; let r=/a/g; function asNumber(){s=s+'c';return 1;} function asText(){s=s+'a';return 'ba';} r.lastIndex={valueOf:asNumber}; let arg={toString:asText}; return [r.test(arg),s==='ac',r.lastIndex]; }\n", "[true,true,2]")]
    [InlineData("ts337-scout-R2/cursor-other-native", "89e0097004a704b5f9d23d4f5850afd3ab78c2d47915f73cce6548e7068a6cf7", "function main() { let r=/b/y; r.lastIndex=new Uint8Array([1]); let a=r.test('ab'); let b=r.lastIndex; r.lastIndex=new Date(1); let c=r.test('ab'); let d=r.lastIndex; r.lastIndex={a:1}; return [a,b,c,d,r.test('ba'),r.lastIndex]; }\n", "[true,2,true,2,true,1]")]
    [InlineData("ts337-scout-R2/member-optional-nonnull", "07048eab6b757e17dc68c40c4ea735da4a7dcea3296866e00a33f81fede25446", "function main() { let n=0; function key(){n=n+1;return 'test';} let r:any=/a/g; return [r?.[key()]('a'),n===1,r.lastIndex]; }\n", "[true,true,1]")]
    [InlineData("ts337-scout-R2/member-detached", "78eb92898cde20e6e123b696cf4e190a5738d64ceb7a2b9ea2010f8b585840ed", "function main() { let r:any=/a/g; let f=r.test; let typeError=false; try { f(r,'a'); } catch(e) {typeError=(e as any).name==='TypeError';} return [typeError,f===r['test'],r.lastIndex]; }\n", "[true,true,0]")]
    [InlineData("ts337-scout-R2/member-mixed-routing", "5f6c8052a580d90638dfe6edce5f45c2e4b0fd15a0dddc57a10530153cdaec6d", "function main() { class Probe { test(x:any):boolean { return false; } } function check(x:any){return x.test('a');} let object={test:()=>false}; let managed=new Probe(); let r=/a/; return [check(managed),check(r),check(object),check(r),check(managed)]; }\n", "[false,true,false,true,false]")]
    [InlineData("ts337-scout-R2/member-spread", "e4ef3e4cb71e1f2b4cc114edc7d17cc6323e3069f57f259896abbdac30b63f89", "function main() { let r:any=/b/g; let a=['ab']; let b=r.test(...a); r.lastIndex=0; return [b,r['test'](...a),r.lastIndex]; }\n", "[true,true,2]")]
    [InlineData("ts337-scout-R2/member-two-optional", "62f2d91eb539445b8f5a33fb9e8cc04b2fa70a075d16f8079b683a776c09ebcf", "function main() { let r:any=/b/y; let k='test'; r.lastIndex=1; let a=r.test?.('ab'); r.lastIndex=1; return [a,r[k]?.('ab'),r.lastIndex]; }\n", "[true,true,2]")]
    [InlineData("ts337-scout-R2/member-typed", "fdf58b19c6d529b5baf49733474c6dc6f5a25a0cab9f5f7f0fe9ae36663b0f32", "interface RegExp { lastIndex:number; test(input:any):boolean; }\nfunction main() { let r:RegExp=/b/y; let dyn:any=r; r.lastIndex=1; let a=r.test('ab'); let b=r.lastIndex; r.lastIndex=1; let c=r['test']('ab'); return [a,b,c,r['lastIndex'],r.test===dyn.test,r===dyn]; }\n", "[true,2,true,2,true,true]")]
    [InlineData("ts337-scout-R2/property-computed-reference", "7f0f96e8cbee85bcbde7accabdfaa0e703f0be69045a9ccb1f98b91d2052aeb8", "function main() { let r:any=/b/y; let k=['test']; r.lastIndex=1; let same=r[k]===r.test; return [same,r[k]('ab'),r.lastIndex]; }\n", "[true,true,2]")]
    [InlineData("ts337-scout-R2/literal-slash-class", "15505a376715c5ba81902b7ca1075c34a70ef4afb7d8cd9af19c0dc10d335b5b", "function main() { return [/[/]/.test('/'),String(/[/]/)==='/[/]/']; }\n", "[true,true]")]
    [InlineData("ts337-scout-R2/literal-escaped-metachar", "a6e64d6779c8e687c67fa580f2f367e75efe06551ffc84f1ed570435a3ee8530", "function main() { return [/^\\.$/.test('.'),/^\\.$/.test('a'),/^\\[$/.test('[')]; }\n", "[true,false,true]")]
    [InlineData("ts337-scout-R2/literal-empty-match", "8591203eac2205b81d1df2401650c3176f8d13b058bb3650df7d831101cd8d22", "function main() { let r=/(?:)/gy; r.lastIndex=2; return [r.test('ab'),r.lastIndex,r.test('ab'),r.lastIndex]; }\n", "[true,2,true,2]")]
    [InlineData("supplement/parser-neighbors", "d9978530852a183f3f831333fb181f64798f5d88fb7c461fbec090bb3c1524b4", "function main() { return [/a/ig.test('A'), /^[a/b]$/.test('/'), /^[\\]]$/.test(']'), /\\\\/.test('\\\\'), /^ a $/.test(' a '), 12 / 3===4, `v:${/a/i.test('A')}`==='v:true']; }\n", "[true,true,true,true,true,true,true]")]
    [InlineData("supplement/wire-control", "d3d55d795106069556fc547ea23822649ba5ff07d10ac169263d3cc0cdf0ee1e", "interface RegExp { lastIndex:number; test(input:any):boolean; }\nfunction main() { let r:RegExp=/a/i; let v:any=r; let rf=r.test; return [r.test('ba'),v['test']('ba'),v.lastIndex,rf===v.test]; }\n", "[true,true,0,true]")]
    [InlineData("supplement/wire-regexp-delta", "da31081b8a90357c4ddbac7e79e01bb37d80aea4e12b202d9799bbbbafdb21e3", "interface RegExp { lastIndex:number; test(input:any):boolean; }\nfunction main() { let r:RegExp=/^a$/i; let v:any=r; let rf=r.test; return [r.test('A\\n'),v['test']('A'),v.lastIndex,rf===v.test]; }\n", "[false,true,0,true]")]
    [InlineData("supplement/own-receiver", "594454951aba806e665f29969194dc5888f0d240351e53bc817276f443457970", "function main() {\n    let n=0; let rx=/^ab$/g;\n    let input:any={ tag:'ab', toString:function(){ n=n+1; return (this as any).tag; } };\n    let result=rx.test(input);\n    return [result,n,rx.lastIndex];\n}\n", "[true,1,2]")]
    [InlineData("supplement/own-shadow-no-write", "757baf5f6fd9c309d2966506339d6aa79b72ce8db944ec4642778ed2de2c89c7", "function main() {\n    let r=/a/g; r.lastIndex=1;\n    let shadow:any={toString:0,valueOf:()=>({})}; let caught=false;\n    try { r.test(shadow); } catch(e) { let x:any=e; caught=x.name==='TypeError'; }\n    return [caught,r.lastIndex];\n}\n", "[true,1]")]
    [InlineData("supplement/scope-display", "16f27bc5e01646fa67ac442ae7bbeb5169fd96f2caf85758e63b0c57cf6f3a2e", "function main() {\n    let r=/a/ig;\n    return [/gi$/.test(r),String(r)==='/a/gi'];\n}\n", "[true,true]")]
    [InlineData("supplement/projected-template-typed", "9c99d552f5db1c1e0654764db4c14cbc43dd1cbeec9f009a9e6293d093b91bd4", "interface RegExp {\n    lastIndex: number;\n    test(input?: unknown): boolean;\n}\nfunction main() {\n    let r:RegExp=/^undefined$/i;\n    let alias=r.test;\n    let dyn:any=r;\n    return [r.test(),dyn['test']('undefined'),alias===dyn.test];\n}\n", "[true,true,true]")]
    public async Task ReturnsFrozenScriptValue(string id, string sha256, string source, string expected)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var folder = Path.Combine(Path.GetTempPath(), "typesharp-regexp-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var sourceFile = Path.Combine(folder, id[(id.IndexOf('/') + 1)..] + ".ts");
            await File.WriteAllBytesAsync(sourceFile, bytes);
            await using var runtime = await new TypeSharpRuntimeBuilder().AddSourceFile(sourceFile).BuildAsync();
            Assert.Equal(expected, Display(runtime.Invoke("main")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static string Display(TsValue? result) => result switch
    {
        TsArrayValue array => "[" + string.Join(",", Enumerable.Range(0, array.Value.Count)
            .Select(i => array.Value.IsHole(i) ? "H" : Display(array.Value.Get(i)))) + "]",
        TsBoolValue flag => flag.Value ? "true" : "false",
        TsInt32Value number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsInt64Value number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsUInt64Value number => number.Value.ToString(CultureInfo.InvariantCulture),
        TsFloat64Value number => number.Value.ToString("R", CultureInfo.InvariantCulture),
        TsFloat32Value number => number.Value.ToString("R", CultureInfo.InvariantCulture),
        TsStringValue text => "s:" + string.Concat(text.Value.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture))),
        TsVoid => "U",
        TsNull => "null",
        _ => throw new InvalidOperationException("Unexpected returned carrier: " + result?.GetType().Name),
    };
}
