using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class JsonStringifyUndefinedRootTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("omitted", "", null, 0, null)]
    [InlineData("literal", "undefined", null, 1, null)]
    [InlineData("forwarded", "value", null, 1, null)]
    [InlineData("null-control", "null", null, 1, "null")]
    [InlineData("ordinary-control", "value", 7, 1, "7")]
    public void JsonStringify_DistinguishesRootUndefinedFromNull(
        string row, string expression, int? argument, int expectedArity, string? expected)
    {
        var source = $"function main(value: number | undefined): any {{ return JSON.stringify({expression}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        var parserDiagnostics = parser.Diagnostics.ToArray();
        var binderDiagnostics = binder.Diagnostics.All.ToArray();
        Assert.Empty(parserDiagnostics);
        Assert.Empty(binderDiagnostics);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("JSON::stringify", target);
        Assert.Equal(expectedArity, call.Operand1);

        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var actual = vm.Execute(BytecodeCompiler.Compile(ir), "main",
            [argument is int value ? TsValue.FromInt32(value) : TsValue.Void]);

        var expectedResult = expected is null ? "TsVoid" : $"TsStringValue(\"{expected}\")";
        var actualResult = actual is TsStringValue actualString
            ? $"TsStringValue(\"{actualString.Value}\")"
            : actual?.GetType().Name ?? "host null";
        output.WriteLine($"JSON.stringify row={row}; parser/binder={parserDiagnostics.Length}/{binderDiagnostics.Length}; target={target}; argc={call.Operand1}; expected={expectedResult}; actual={actualResult}");

        if (expected is null)
            Assert.Same(TsValue.Void, actual);
        else
            Assert.True(actual is TsStringValue result && string.Equals(expected, result.Value, StringComparison.Ordinal),
                $"JSON.stringify root result mismatch: expected={expectedResult}; actual={actualResult}");
    }
}
