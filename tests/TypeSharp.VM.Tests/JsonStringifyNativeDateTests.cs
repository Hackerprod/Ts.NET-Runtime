using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class JsonStringifyNativeDateTests(ITestOutputHelper output)
{
    private sealed record Row(string Name, string Expression, (string Type, int Arity)[] Constructors,
        string Baseline, string Expected);

    [Fact]
    public void JsonStringify_UsesUtcIsoStringForSupportedNativeDate()
    {
        Row[] rows =
        [
            new("epoch-root", "new Date(0)", [("Date", 1)],
                "{\"__timestampMs\":0}", "\"1970-01-01T00:00:00.000Z\""),
            new("fractional-second-nested", "{ at: new Date(1234) }", [("Object", 0), ("Date", 1)],
                "{\"at\":{\"__timestampMs\":1234}}", "{\"at\":\"1970-01-01T00:00:01.234Z\"}"),
            new("impostor", "{ __timestampMs: 0 }", [("Object", 0)],
                "{\"__timestampMs\":0}", "{\"__timestampMs\":0}"),
            new("c316-invalid-leak", "new Date(\"not-a-date\")", [("Date", 1)],
                "{\"__timestampMs\":null}", "null"),
            new("c316-expanded-year-leak", "new Date(253402300800000)", [("Date", 1)],
                "{\"__timestampMs\":253402300800000}", "\"+010000-01-01T00:00:00.000Z\""),
        ];

        var observed = rows.Select(row => (Row: row, Result: Execute(row))).ToArray();
        var values = observed.Select(item =>
        {
            var result = Assert.IsType<TsStringValue>(item.Result);
            Assert.Equal(TsValueType.String, result.ValueType);
            return (item.Row, Actual: result.Value);
        }).ToArray();

        // The first two Baseline values record the old C218 red behavior.
        // The last two Baseline values record the separate C316 red behavior.
        // The ordinary object is a control in both cycles.
        foreach (var (row, actual) in values)
            Assert.True(string.Equals(actual, row.Expected, StringComparison.Ordinal),
                $"C218/C317 {row.Name}: baseline={row.Baseline}; expected={row.Expected}; actual={actual}");
    }

    private TsValue Execute(Row row)
    {
        var source = $"function main(): string {{ return JSON.stringify({row.Expression}); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        int parserCount = parser.Diagnostics.Count;
        int binderCount = binder.Diagnostics.All.Count;
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(instructions, instruction =>
            instruction.Opcode is Opcode.Call or Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        Assert.Equal(Opcode.Call, call.Opcode);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("JSON::stringify", target);
        Assert.Equal(1, call.Operand1);

        var constructors = instructions.Where(instruction => instruction.Opcode == Opcode.NewObject)
            .Select(instruction => (Type: Assert.IsType<string>(instruction.OperandObject), Arity: instruction.Operand1))
            .ToArray();
        Assert.Equal(row.Constructors, constructors);

        var bytecode = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(bytecode);
        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(bytecode, "main", []);
        output.WriteLine("C218_JSON_STRINGIFY_NATIVE_DATE " + JsonSerializer.Serialize(new
        {
            row = row.Name, expression = row.Expression, source, parserCount, binderCount,
            call = call.Opcode.ToString(), target, argc = call.Operand1,
            constructors = constructors.Select(constructor => new { type = constructor.Type, arity = constructor.Arity }).ToArray(),
            actualType = result.GetType().Name, actualTag = result.ValueType.ToString(),
            actual = (result as TsStringValue)?.Value, baseline = row.Baseline, expected = row.Expected
        }));
        return result;
    }
}
