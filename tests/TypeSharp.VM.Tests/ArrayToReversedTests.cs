using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayToReversedTests(ITestOutputHelper output)
{
    private const string DenseSource = "function main(a: int32[]): any { return a.toReversed(); }";

    [Fact]
    public void ToReversed_OrdinaryNativeArrays()
    {
        const string target = "Array::toReversed";
        var dense = ArrayOf(7, 8, 9);
        var result = Run("dense", DenseSource, target, dense, checkBinding: true);
        AssertCopy("dense", dense, result, "[7,8,9]", "[9,8,7]");

        var token = ArrayOf(4);
        var sparse = new TsArrayValue(new TsArray());
        sparse.Value.Add(token);
        sparse.Value.SetHole(sparse.Value.Count);
        sparse.Value.Add(TsValue.Void);
        Assert.Equal("[[4],H,U]", Snapshot(sparse.Value));
        result = Run("sparse", "function main(a: any[]): any { return a.toReversed(); }", target, sparse);
        AssertCopy("sparse", sparse, result, "[[4],H,U]", "[U,U,[4]]");
        Assert.Same(token, sparse.Value.Get(0));
        Assert.Same(token, result.Value.Get(2));

        var readOnly = ArrayOf(7, 8);
        result = Run("readonly",
            "function main(a: ReadonlyArray<int32>): int32[] { const copy = a.toReversed(); copy[0] = 6; return copy; }",
            target, readOnly, checkBinding: true, readOnlyReceiver: true);
        AssertCopy("readonly", readOnly, result, "[7,8]", "[6,7]");

        var empty = ArrayOf();
        result = Run("empty", DenseSource, target, empty);
        AssertCopy("empty", empty, result, "[]", "[]");

        var singleton = ArrayOf(5);
        result = Run("singleton", DenseSource, target, singleton);
        AssertCopy("singleton", singleton, result, "[5]", "[5]");
    }

    [Fact]
    public void Reverse_MutatesInputAndMirrorsHoles()
    {
        var receiver = ArrayOf(7);
        receiver.Value.SetHole(receiver.Value.Count);
        receiver.Value.Add(TsValue.Void);
        receiver.Value.Add(TsValue.FromInt32(9));
        Assert.Equal("[7,H,U,9]", Snapshot(receiver.Value));

        var result = Run("reverse-control", "function main(a: any[]): any[] { return a.reverse(); }",
            "Array::reverse", receiver);
        LogSlots("reverse-control", receiver, result);
        Assert.Same(receiver, result);
        Assert.Same(receiver.Value, result.Value);
        Assert.Equal(4, result.Value.Count);
        Assert.Equal("[9,U,H,7]", Snapshot(result.Value));
        Assert.Equal("[9,U,H,7]", Snapshot(receiver.Value));
    }

    private TsArrayValue Run(string row, string source, string expectedTarget, TsArrayValue receiver,
        bool checkBinding = false, bool readOnlyReceiver = false)
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(
            new TypeSharp.Syntax.Lexer(source, $"c130-{row}.ts").Tokenize());
        var syntax = parser.Parse();
        output.WriteLine($"C130 row={row}; stage=parser; diagnostics={parser.Diagnostics.Count}");
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        var diagnostics = binder.Diagnostics.All;
        var details = string.Join("\n", diagnostics.Select(d =>
            $"severity={d.Severity}; code={d.Code}; numericCode={(int)d.Code}; source={d.Location.Source}; " +
            $"line={d.Location.Line}; column={d.Location.Column}; offset={d.Location.Offset}; message={d.Message}"));
        var receipt = $"C130_TOREVERSED_BINDER_DIAGNOSTICS row={row}; stage=binder; diagnostics={diagnostics.Count}\n{details}";
        output.WriteLine(receipt);
        Assert.True(diagnostics.Count == 0, receipt);

        if (checkBinding)
        {
            var main = Assert.Single(bound.Members.OfType<BoundFunctionDeclaration>(), f => f.Symbol.Name == "main");
            var body = Assert.IsType<BoundBlockStatement>(main.Body);
            var expression = readOnlyReceiver
                ? Assert.Single(body.Statements.OfType<BoundVariableDeclaration>(), d => d.Symbol.Name == "copy").Initializer
                : Assert.Single(body.Statements.OfType<BoundReturnStatement>()).Value;
            var boundCall = Assert.IsType<BoundCallExpression>(expression);
            var member = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
            var method = Assert.IsType<MethodSymbol>(member.Member);
            Assert.Equal("Array", method.DeclaringClassName);
            Assert.Equal("toReversed", method.Name);
            var inputType = Assert.IsType<TsArrayType>(member.Object.Type);
            Assert.Equal(TsType.Int32, inputType.ElementType);
            Assert.Equal(readOnlyReceiver, inputType.IsReadonly);
            var returnType = Assert.IsType<TsArrayType>(boundCall.Type);
            Assert.Equal(TsType.Int32, returnType.ElementType);
            Assert.False(returnType.IsReadonly);
            output.WriteLine($"C130 row={row}; stage=bound-call; member={method.DeclaringClassName}::{method.Name}; " +
                $"input={inputType}, readonly={inputType.IsReadonly}; result={returnType}, readonly={returnType.IsReadonly}");
        }

        var ir = new IRGenerator().Generate(bound);
        var mainIr = Assert.Single(ir.Functions, f => f.Name == "main");
        var call = Assert.Single(mainIr.Blocks.SelectMany(b => b.Instructions), i => i.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        output.WriteLine($"C130 row={row}; stage=ir; target={target}; argc={call.Operand1}");
        Assert.Equal(expectedTarget, target);
        Assert.Equal(1, call.Operand1);

        var bytecode = BytecodeCompiler.Compile(ir);
        output.WriteLine($"C130 row={row}; stage=vm");
        return Assert.IsType<TsArrayValue>(
            new TypeSharp.VM.Interpreter.Interpreter().Execute(bytecode, "main", [receiver]));
    }

    private void AssertCopy(string row, TsArrayValue receiver, TsArrayValue result, string input, string expected)
    {
        LogSlots(row, receiver, result);
        Assert.NotSame(receiver, result);
        Assert.NotSame(receiver.Value, result.Value);
        Assert.Equal(receiver.Value.Count, result.Value.Count);
        Assert.Equal(input, Snapshot(receiver.Value));
        Assert.Equal(expected, Snapshot(result.Value));
    }

    private void LogSlots(string row, TsArrayValue receiver, TsArrayValue result) =>
        output.WriteLine($"C130 row={row}; stage=slots; inputLength={receiver.Value.Count}; input={Snapshot(receiver.Value)}; " +
            $"resultLength={result.Value.Count}; result={Snapshot(result.Value)}; " +
            $"sameWrapper={ReferenceEquals(receiver, result)}; sameBacking={ReferenceEquals(receiver.Value, result.Value)}");

    private static TsArrayValue ArrayOf(params int[] values)
    {
        var array = new TsArray(values.Length);
        foreach (int value in values)
            array.Add(TsValue.FromInt32(value));
        return new TsArrayValue(array);
    }

    private static string Snapshot(TsArray array) => "[" + string.Join(",",
        Enumerable.Range(0, array.Count).Select(i => array.IsHole(i) ? "H" : array.Get(i) switch
        {
            TsArrayValue value => Snapshot(value.Value),
            _ when ReferenceEquals(array.Get(i), TsValue.Void) => "U",
            TsInt32Value value => value.Value.ToString(),
            var value => $"<{value.GetType().Name}>"
        })) + "]";
}
