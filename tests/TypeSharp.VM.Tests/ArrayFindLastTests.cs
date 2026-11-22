using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFindLastTests(ITestOutputHelper output)
{
    private const string GateSource = "export function main(): any { const a: string[] = ['one', 'two', 'three']; return a.findLast((v: string): boolean => v !== 'two'); }";
    private const string DescendingSource = "export function main(): string { const a: number[] = [2, 3, 4, 5]; let trace: string = ''; const found: any = a.findLast((v: number, i: number, source: number[]): boolean => { if (source === a) trace = trace + 'same:'; else trace = trace + 'other:'; trace = trace + i + ':' + v + '|'; return v === 2 || v === 4; }); return trace + found + '|' + a.join(','); }";

    [Fact]
    public void FindLast_OrdinaryNativeArrays()
    {
        const string target = "Array::findLast";
        AssertString("gate", GateSource, target, "three", elementType: TsType.String);
        AssertString("descending", DescendingSource, target, "same:3:5|same:2:4|4|2,3,4,5");
        AssertString("live", "export function main(): string { const a: any[] = [2, 4, 6, 8]; delete a[2]; let trace: string = ''; const found: any = a.findLast((v: any, i: number, source: any[]): boolean => { if (i === 3) { source[1] = 40; source.push(10); } if (v === undefined) trace = trace + i + ':undefined|'; else trace = trace + i + ':' + v + '|'; return v === 10; }); return trace + (found === undefined) + '|' + a.join(','); }",
            target, "3:8|2:undefined|1:40|0:2|true|2,40,,8,10");
        AssertString("truthiness", "export function main(): string { const a: string[] = ['old', 'skip']; let trace: string = ''; const found: any = a.findLast((v: string, i: number, source: string[]): string => { trace = trace + v + '|'; if (v === 'skip') return ''; source[i] = 'new'; return 'yes'; }); return trace + found + '|' + a.join(','); }",
            target, "skip|old|old|new,skip");

        var missing = Run("readonly", "export function main(): any { const a: ReadonlyArray<number> = [2, 4]; return a.findLast((v: number): boolean => v === 99); }",
            target, Opcode.Call, TsType.Number, readOnly: true);
        Assert.IsType<TsVoid>(missing);
        Assert.Same(TsValue.Void, missing);

        AssertString("virtual", "export function main(): any { const a: any = ['one', 'two', 'three']; return a.findLast((v: string): boolean => v !== 'two'); }",
            "findLast", "three", Opcode.CallVirt);
    }

    [Fact]
    public void ExistingFind_PreservesForwardTraversal() =>
        AssertString("find-control", DescendingSource.Replace(".findLast(", ".find(", StringComparison.Ordinal),
            "Array::find", "same:0:2|2|2,3,4,5");

    private void AssertString(string row, string source, string target, string expected,
        Opcode opcode = Opcode.Call, TsType? elementType = null) =>
        Assert.Equal(expected, Assert.IsType<TsStringValue>(Run(row, source, target, opcode, elementType)).Value);

    private TsValue Run(string row, string source, string expectedTarget, Opcode opcode,
        TsType? elementType, bool readOnly = false)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(
            new TypeSharp.Syntax.Lexer(source, $"c132-find-last-{row}.ts").Tokenize());
        var syntax = parser.Parse();
        output.WriteLine($"C132 row={row}; stage=parser; diagnostics={parser.Diagnostics.Count}");
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        var diagnostics = binder.Diagnostics.All;
        var details = string.Join("\n", diagnostics.Select(d =>
            $"severity={d.Severity}; code={d.Code}; numericCode={(int)d.Code}; source={d.Location.Source}; " +
            $"line={d.Location.Line}; column={d.Location.Column}; offset={d.Location.Offset}; message={d.Message}"));
        var receipt = $"C132_FINDLAST_BINDER_DIAGNOSTICS row={row}; stage=binder; diagnostics={diagnostics.Count}";
        if (diagnostics.Count != 0)
            receipt += "\n" + details;
        output.WriteLine(receipt);
        Assert.True(diagnostics.Count == 0, receipt);

        if (elementType is not null)
        {
            var main = Assert.Single(bound.Members.OfType<BoundFunctionDeclaration>(), f => f.Symbol.Name == "main");
            var body = Assert.IsType<BoundBlockStatement>(main.Body);
            var expression = Assert.Single(body.Statements.OfType<BoundReturnStatement>()).Value;
            var boundCall = Assert.IsType<BoundCallExpression>(expression);
            var member = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
            var method = Assert.IsType<MethodSymbol>(member.Member);
            Assert.Equal("Array", method.DeclaringClassName);
            Assert.Equal("findLast", method.Name);
            var receiver = Assert.IsType<TsArrayType>(member.Object.Type);
            Assert.Equal(elementType, receiver.ElementType);
            Assert.Equal(readOnly, receiver.IsReadonly);
            var resultType = Assert.IsType<TsNullableType>(boundCall.Type);
            Assert.Equal(elementType, resultType.ElementType);
            output.WriteLine($"C132 row={row}; stage=bound-call; member={method.DeclaringClassName}::{method.Name}; " +
                $"receiverElement={receiver.ElementType}; readonly={receiver.IsReadonly}; result={resultType}");
        }

        var ir = new IRGenerator().Generate(bound);
        var mainIr = Assert.Single(ir.Functions, f => f.Name == "main");
        var call = Assert.Single(mainIr.Blocks.SelectMany(b => b.Instructions), i =>
            (i.Opcode is Opcode.Call or Opcode.CallVirt) && i.OperandObject is string name && name == expectedTarget);
        var target = Assert.IsType<string>(call.OperandObject);
        output.WriteLine($"C132 row={row}; stage=ir; opcode={call.Opcode}; target={target}; argc={call.Operand1}");
        Assert.Equal(opcode, call.Opcode);
        Assert.Equal(expectedTarget, target);
        Assert.Equal(2, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        var value = new TypeSharp.VM.Interpreter.Interpreter().Execute(bytecode, "main", []);
        output.WriteLine($"C132 row={row}; stage=vm; runtimeType={value.GetType().Name}; actual={value switch
        {
            TsStringValue text => text.Value,
            TsVoid => "undefined",
            _ => value.ToString()
        }}");
        return value;
    }
}
