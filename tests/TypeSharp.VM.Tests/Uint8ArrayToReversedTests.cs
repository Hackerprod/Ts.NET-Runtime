using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class Uint8ArrayToReversedTests(ITestOutputHelper output)
{
    private const string Prefix = "C155 Uint8Array.toReversed ";

    [Fact]
    public void ToReversed_ReturnsIndependentReversedBytesWithoutMutatingReceiver()
    {
        const string source = "function main(bytes: Uint8Array): any { return bytes.toReversed(); }\n";
        var parser = new TypeSharp.Syntax.Parser.Parser(
            new TypeSharp.Syntax.Lexer(source, "c155-uint8array-to-reversed.ts").Tokenize());
        var syntax = parser.Parse();
        output.WriteLine($"{Prefix}stage=parser; diagnostics={parser.Diagnostics.Count}");
        foreach (var diagnostic in parser.Diagnostics)
            output.WriteLine($"{Prefix}severity={diagnostic.Severity}; code={diagnostic.Code}; numericCode={(int)diagnostic.Code}; "
                + $"source={diagnostic.Location.Source}; line={diagnostic.Location.Line}; column={diagnostic.Location.Column}; "
                + $"offset={diagnostic.Location.Offset}; message={diagnostic.Message}");
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        var diagnostics = binder.Diagnostics.All;
        output.WriteLine($"{Prefix}stage=binder; diagnostics={diagnostics.Count}");
        foreach (var diagnostic in diagnostics)
            output.WriteLine($"{Prefix}severity={diagnostic.Severity}; code={diagnostic.Code}; numericCode={(int)diagnostic.Code}; "
                + $"source={diagnostic.Location.Source}; line={diagnostic.Location.Line}; column={diagnostic.Location.Column}; "
                + $"offset={diagnostic.Location.Offset}; message={diagnostic.Message}");
        Assert.True(diagnostics.Count == 0, "C155 Uint8Array.toReversed expected clean binding.");

        var function = Assert.Single(bound.Members.OfType<BoundFunctionDeclaration>(), f => f.Symbol.Name == "main");
        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var boundCall = Assert.IsType<BoundCallExpression>(Assert.Single(body.Statements.OfType<BoundReturnStatement>()).Value);
        var member = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        var receiverType = Assert.IsType<TsClassType>(member.Object.Type);
        var methodResult = Assert.IsType<TsClassType>(method.Type);
        var callResult = Assert.IsType<TsClassType>(boundCall.Type);
        Assert.Equal("Uint8Array", receiverType.Name);
        Assert.Equal("Uint8Array", method.DeclaringClassName);
        Assert.Equal("toReversed", method.Name);
        Assert.False(method.IsStatic);
        Assert.Empty(method.Parameters);
        Assert.Empty(boundCall.Arguments);
        Assert.Equal("Uint8Array", methodResult.Name);
        Assert.Equal("Uint8Array", callResult.Name);
        output.WriteLine($"{Prefix}stage=bound; receiver={receiverType.Name}; owner={method.DeclaringClassName}; method={method.Name}; "
            + $"static={method.IsStatic}; explicitParameters={method.Parameters.Count}; explicitArgs={boundCall.Arguments.Count}; "
            + $"methodResult={methodResult.Name}; callResult={callResult.Name}");

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, f => f.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Opcode is Opcode.Call or Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        Assert.Equal(Opcode.Call, call.Opcode);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Uint8Array::toReversed", target);
        Assert.Equal(1, call.Operand1);
        output.WriteLine($"{Prefix}stage=ir; target={target}; argc={call.Operand1}");

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var original = new TsUint8ArrayValue([9, 47, 128]);
        var originalBacking = original.Value;
        var first = CheckRow("first", original, [9, 47, 128], [128, 47, 9]);
        var firstBacking = first.Value;
        Assert.Same(originalBacking, original.Value);
        Assert.Equal(new byte[] { 9, 47, 128 }, original.Value);

        var second = CheckRow("second", first, [128, 47, 9], [9, 47, 128]);
        Assert.NotSame(original, second);
        Assert.NotSame(originalBacking, second.Value);
        Assert.Same(originalBacking, original.Value);
        Assert.Same(firstBacking, first.Value);
        Assert.Equal(3, original.Length);
        Assert.Equal(3, first.Length);
        Assert.Equal(new byte[] { 9, 47, 128 }, original.Value);
        Assert.Equal(new byte[] { 128, 47, 9 }, first.Value);

        TsUint8ArrayValue CheckRow(string row, TsUint8ArrayValue receiver, byte[] expectedBefore, byte[] expectedAfter)
        {
            var backing = receiver.Value;
            var before = backing.ToArray();
            var result = Assert.IsType<TsUint8ArrayValue>(vm.Execute(bytecode, "main", [receiver]));
            var inputAfter = receiver.Value.ToArray();
            var resultAfter = result.Value.ToArray();
            var sameWrapper = ReferenceEquals(receiver, result);
            var sameBacking = ReferenceEquals(backing, result.Value);
            output.WriteLine($"{Prefix}row={row}; before={Format(before)}; input={Format(inputAfter)}; result={Format(resultAfter)}; "
                + $"length={result.Length}; sameWrapper={sameWrapper}; sameBacking={sameBacking}");
            Assert.NotSame(receiver, result);
            Assert.Same(backing, receiver.Value);
            Assert.NotSame(backing, result.Value);
            Assert.Equal(3, receiver.Length);
            Assert.Equal(3, result.Length);
            Assert.Equal(expectedBefore, before);
            Assert.Equal(expectedBefore, inputAfter);
            Assert.Equal(expectedAfter, resultAfter);
            return result;
        }
    }

    private static string Format(byte[] bytes) => "[" + string.Join(",", bytes) + "]";
}
