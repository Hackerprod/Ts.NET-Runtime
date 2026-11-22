using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class Uint8ArrayReverseTests(ITestOutputHelper output)
{
    private const string Prefix = "C154 Uint8Array.reverse ";

    [Fact]
    public void Reverse_ReordersOrdinaryBytesInPlaceAndReturnsReceiver()
    {
        const string source = "function main(bytes: Uint8Array): any { return bytes.reverse(); }\n";
        var parser = new TypeSharp.Syntax.Parser.Parser(
            new TypeSharp.Syntax.Lexer(source, "c154-uint8array-reverse.ts").Tokenize());
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
        Assert.True(diagnostics.Count == 0, "C154 Uint8Array.reverse expected clean binding.");

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
        Assert.Equal("reverse", method.Name);
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
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Uint8Array::reverse", target);
        Assert.Equal(1, call.Operand1);
        output.WriteLine($"{Prefix}stage=ir; target={target}; argc={call.Operand1}");

        var bytecode = BytecodeCompiler.Compile(ir);
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var receiver = new TsUint8ArrayValue([9, 47, 128]);
        var backing = receiver.Value;
        CheckRow("first", [9, 47, 128], [128, 47, 9]);
        CheckRow("second", [128, 47, 9], [9, 47, 128]);

        void CheckRow(string row, byte[] expectedBefore, byte[] expectedAfter)
        {
            var before = receiver.Value.ToArray();
            var result = Assert.IsType<TsUint8ArrayValue>(vm.Execute(bytecode, "main", [receiver]));
            var inputAfter = receiver.Value.ToArray();
            var resultAfter = result.Value.ToArray();
            var sameWrapper = ReferenceEquals(receiver, result);
            var sameBacking = ReferenceEquals(backing, receiver.Value) && ReferenceEquals(backing, result.Value);
            output.WriteLine($"{Prefix}row={row}; before={Format(before)}; input={Format(inputAfter)}; result={Format(resultAfter)}; "
                + $"length={receiver.Length}; sameWrapper={sameWrapper}; sameBacking={sameBacking}");
            Assert.Same(receiver, result);
            Assert.Same(backing, receiver.Value);
            Assert.Same(backing, result.Value);
            Assert.Equal(3, receiver.Length);
            Assert.Equal(3, result.Length);
            Assert.Equal(expectedBefore, before);
            Assert.Equal(expectedAfter, inputAfter);
            Assert.Equal(expectedAfter, resultAfter);
        }
    }

    private static string Format(byte[] bytes) => "[" + string.Join(",", bytes) + "]";
}
