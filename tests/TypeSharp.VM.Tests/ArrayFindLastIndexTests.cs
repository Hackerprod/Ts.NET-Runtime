using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFindLastIndexTests(ITestOutputHelper output)
{
    private const string TraceSource = """
        export function main(): any {
          const a: number[] = [2, 3, 4, 5];
          let trace: string = '';
          const found: any = a.METHOD((v: number, i: number, source: number[]): boolean => {
            if (source === a) trace = trace + 'same:';
            else trace = trace + 'other:';
            trace = trace + i + ':' + v + '|';
            return v === 2 || v === 4;
          });
          return [trace, found];
        }
        """;

    [Fact]
    public void TypedGate_ReturnsLastMatchingIndex() =>
        AssertInt(2, Run("gate", "export function main(): any { const jobs: string[] = ['ready', 'busy', 'ready', 'busy']; return jobs.findLastIndex((job: string): boolean => job === 'ready'); }",
            TsType.String));

    [Fact]
    public void DescendsWithOriginalReceiverAndStopsEarly() =>
        AssertInt(2, CheckTrace("descending", "findLastIndex", "same:3:5|same:2:4|"));

    [Fact]
    public void EmptyReturnsMinusOneWithoutCallingPredicate()
    {
        var pair = Pair(Run("empty", """
            export function main(): any {
              const a: number[] = [];
              let called: number = 0;
              const found: any = a.findLastIndex((v: number): boolean => {
                called = called + 1;
                return true;
              });
              return [called, found];
            }
            """, TsType.Number));
        AssertInt(0, pair.Get(0));
        AssertInt(-1, pair.Get(1));
    }

    [Fact]
    public void ReadonlyMissReturnsMinusOne() =>
        AssertInt(-1, Run("readonly", """
            export function main(): any {
              const a: ReadonlyArray<number> = [2, 4];
              return a.findLastIndex((v: number): boolean => v === 99);
            }
            """, TsType.Number, readOnly: true));

    [Fact]
    public void VisitsHolesAndLiveValuesWithinInitialLength()
    {
        var pair = Pair(Run("live", """
            export function main(): any {
              const a: any[] = [2, 4, 6, 8];
              delete a[2];
              let trace: string = '';
              const found: any = a.findLastIndex((v: any, i: number, source: any[]): boolean => {
                if (i === 3) { source[1] = 40; source.push(10); }
                if (v === undefined) trace = trace + i + ':undefined|';
                else trace = trace + i + ':' + v + '|';
                return v === 10;
              });
              return [trace, found, a.join(',')];
            }
            """, TsType.Any), 3);
        Assert.Equal("3:8|2:undefined|1:40|0:2|", Assert.IsType<TsStringValue>(pair.Get(0)).Value);
        AssertInt(-1, pair.Get(1));
        Assert.Equal("2,40,,8,10", Assert.IsType<TsStringValue>(pair.Get(2)).Value);
    }

    [Fact]
    public void AnyArrayUsesNativeVirtualRouteAndTruthiness()
    {
        var pair = Pair(Run("virtual", """
            export function main(): any {
              const a: any = ['skip', 'ready', 'skip'];
              let trace: string = '';
              const found: any = a.findLastIndex((v: string): string => {
                trace = trace + v + '|';
                if (v === 'skip') return '';
                return 'yes';
              });
              return [trace, found];
            }
            """, null));
        Assert.Equal("skip|ready|", Assert.IsType<TsStringValue>(pair.Get(0)).Value);
        AssertInt(1, pair.Get(1));
    }

    [Fact]
    public void ExistingFindIndexPreservesForwardIndex() =>
        AssertInt(0, CheckTrace("find-index-control", "findIndex", "same:0:2|"));

    [Fact]
    public void ExistingFindLastPreservesDescendingElement() =>
        AssertInt(4, CheckTrace("find-last-control", "findLast", "same:3:5|same:2:4|"));

    private TsValue CheckTrace(string row, string method, string expected)
    {
        var pair = Pair(Run(row, TraceSource.Replace("METHOD", method, StringComparison.Ordinal),
            TsType.Number, method));
        Assert.Equal(expected, Assert.IsType<TsStringValue>(pair.Get(0)).Value);
        return pair.Get(1);
    }

    private static TsArray Pair(TsValue value, int length = 2)
    {
        var arr = Assert.IsType<TsArrayValue>(value).Value;
        Assert.Equal(length, arr.Count);
        return arr;
    }

    private static void AssertInt(int expected, TsValue actual) =>
        Assert.Equal(expected, Assert.IsType<TsInt32Value>(actual).Value);

    private TsValue Run(string row, string source, TsType? elementType, string methodName = "findLastIndex",
        bool readOnly = false)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(
            new TypeSharp.Syntax.Lexer(source, $"c143-find-last-index-{row}.ts").Tokenize());
        var syntax = parser.Parse();
        output.WriteLine($"C143 row={row}; stage=parser; diagnostics={parser.Diagnostics.Count}");
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        var diagnostics = binder.Diagnostics.All;
        var receipt = $"C143_FINDLASTINDEX_BINDER_DIAGNOSTICS row={row}; stage=binder; diagnostics={diagnostics.Count}";
        foreach (var d in diagnostics)
            receipt += $"\nseverity={d.Severity}; code={d.Code}; numericCode={(int)d.Code}; source={d.Location.Source}; " +
                $"line={d.Location.Line}; column={d.Location.Column}; offset={d.Location.Offset}; message={d.Message}";
        output.WriteLine(receipt);
        Assert.True(diagnostics.Count == 0, receipt);

        var main = Assert.Single(bound.Members.OfType<BoundFunctionDeclaration>(), f => f.Symbol.Name == "main");
        var body = Assert.IsType<BoundBlockStatement>(main.Body);
        var focal = row is "gate" or "readonly"
            ? Assert.Single(body.Statements.OfType<BoundReturnStatement>()).Value
            : Assert.Single(body.Statements.OfType<BoundVariableDeclaration>(), v => v.Symbol.Name == "found").Initializer;
        var boundCall = Assert.IsType<BoundCallExpression>(focal);
        var member = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
        Assert.Equal(methodName, member.Member.Name);
        if (elementType is null)
        {
            Assert.Equal(TsType.Any, member.Object.Type);
            Assert.IsType<PropertySymbol>(member.Member);
            Assert.Equal(TsType.Any, member.Member.Type);
            Assert.Equal(TsType.Any, boundCall.Type);
        }
        else
        {
            var method = Assert.IsType<MethodSymbol>(member.Member);
            Assert.Equal("Array", method.DeclaringClassName);
            var receiver = Assert.IsType<TsArrayType>(member.Object.Type);
            Assert.Equal(elementType, receiver.ElementType);
            Assert.Equal(readOnly, receiver.IsReadonly);
            if (methodName == "findLast")
                Assert.Equal(elementType, Assert.IsType<TsNullableType>(boundCall.Type).ElementType);
            else
                Assert.Equal(TsType.Int32, boundCall.Type);
        }
        output.WriteLine($"C143 row={row}; stage=bound-call; member={member.Member.GetType().Name}:{member.Member.Name}; " +
            $"receiver={member.Object.Type}; readonly={readOnly}; result={boundCall.Type}");

        var ir = new IRGenerator().Generate(bound);
        var mainIr = Assert.Single(ir.Functions, f => f.Name == "main");
        var expectedTarget = elementType is null ? methodName : $"Array::{methodName}";
        var call = Assert.Single(mainIr.Blocks.SelectMany(b => b.Instructions), i =>
            (i.Opcode is Opcode.Call or Opcode.CallVirt) && i.OperandObject is string name && name == expectedTarget);
        output.WriteLine($"C143 row={row}; stage=ir; opcode={call.Opcode}; target={call.OperandObject}; argc={call.Operand1}");
        Assert.Equal(elementType is null ? Opcode.CallVirt : Opcode.Call, call.Opcode);
        Assert.Equal(2, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        output.WriteLine($"C143 row={row}; stage=bytecode; compiled=true");
        var value = new TypeSharp.VM.Interpreter.Interpreter().Execute(bytecode, "main", []);
        output.WriteLine($"C143 row={row}; stage=vm; actual={Describe(value)}");
        return value;
    }

    private static string Describe(TsValue value) => value switch
    {
        TsArrayValue a => $"TsArrayValue[{string.Join(", ", Enumerable.Range(0, a.Value.Count).Select(i => Describe(a.Value.Get(i))))}]",
        _ => $"{value.GetType().Name}({value.RawValue})"
    };
}
