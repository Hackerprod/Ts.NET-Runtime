using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ForInOrdinaryObjectArrayIndexOrderTests
{
    private readonly ITestOutputHelper _output;

    public ForInOrdinaryObjectArrayIndexOrderTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ForIn_OrdersOrdinaryObjectArrayIndexKeys()
    {
        const string source = """
            class KeptOrder {}
            function mixed(): string {
                let seen: string = "";
                for (let key in { label: 7, "10": 10, "2": 2, "02": 3, "4294967295": 5, apple: 1, "4294967294": 4, "0": 0, "-0": 6 }) { seen = seen + key + "|"; }
                return seen;
            }
            function names(): string {
                let seen: string = "";
                for (let key in { zebra: 1, "02": 2, alpha: 3 }) { seen = seen + key + "|"; }
                return seen;
            }
            function ordered(): string {
                let seen: string = "";
                for (let key in { "2": 2, "10": 10, label: 7 }) { seen = seen + key + "|"; }
                return seen;
            }
            function classControl(): string {
                let instance: any = new KeptOrder();
                instance["label"] = 7;
                instance["10"] = 10;
                instance["2"] = 2;
                let seen: string = "";
                for (let key in instance) { seen = seen + key + "|"; }
                return seen;
            }
            """;

        var tokens = new Lexer(source, "c185-for-in-object-order.ts").Tokenize();
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var tree = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(tree);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var rows = new (string Row, string Function, string ReceiverTag, string[] Fields, int ElementStores, string Expected)[]
        {
            ("mixed-boundary", "mixed", "Object",
                new[] { "label", "10", "2", "02", "4294967295", "apple", "4294967294", "0", "-0" }, 0,
                "0|2|10|4294967294|label|02|4294967295|apple|-0|"),
            ("names-only", "names", "Object", new[] { "zebra", "02", "alpha" }, 0, "zebra|02|alpha|"),
            ("already-ordered", "ordered", "Object", new[] { "2", "10", "label" }, 0, "2|10|label|"),
            ("distinct-class-tag", "classControl", "KeptOrder", Array.Empty<string>(), 3, "label|10|2|")
        };
        var routes = rows.Select(row => CheckRoute(Assert.Single(ir.Functions, function => function.Name == row.Function),
            row.ReceiverTag, row.Fields, row.ElementStores)).ToArray();

        var module = BytecodeCompiler.Compile(ir);
        var mismatches = new List<string>();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (!Measure(module, row.Function, row.Row, routes[i], row.Expected))
                mismatches.Add(row.Row);
        }

        if (mismatches.Count != 0)
            Assert.Fail($"C185_SEMANTIC_MISMATCHES {string.Join(",", mismatches)}");
    }

    private static (string ReceiverTag, int EnumerateCount) CheckRoute(FunctionIR function,
        string expectedReceiverTag, string[] expectedFields, int expectedElementStores)
    {
        Assert.Empty(function.Parameters);
        var instructions = function.Blocks.SelectMany(block => block.Instructions).ToArray();
        var enumerations = instructions.Where(instruction => instruction.Opcode == Opcode.EnumerateKeys).ToArray();
        var enumerate = Assert.Single(enumerations);
        var create = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.NewObject);
        var receiverTag = Assert.IsType<string>(create.OperandObject);
        Assert.Equal(expectedReceiverTag, receiverTag);
        Assert.Equal(0, create.Operand1);
        var createIndex = Array.IndexOf(instructions, create);
        var enumerateIndex = Array.IndexOf(instructions, enumerate);
        Assert.True(createIndex < enumerateIndex);

        var fieldStores = instructions.Where(instruction => instruction.Opcode == Opcode.StoreField).ToArray();
        Assert.Equal(expectedFields, fieldStores.Select(instruction => Assert.IsType<string>(instruction.OperandObject)).ToArray());
        var elementStores = instructions.Where(instruction => instruction.Opcode == Opcode.StoreElement).ToArray();
        Assert.Equal(expectedElementStores, elementStores.Length);
        Assert.All(fieldStores.Concat(elementStores), instruction =>
            Assert.InRange(Array.IndexOf(instructions, instruction), createIndex + 1, enumerateIndex - 1));
        return (receiverTag, enumerations.Length);
    }

    private bool Measure(BytecodeModule module, string function, string row,
        (string ReceiverTag, int EnumerateCount) route, string expected)
    {
        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? value = null;
        Exception? error = null;
        try
        {
            value = interpreter.Execute(module, function, Array.Empty<TsValue>());
        }
        catch (Exception exception)
        {
            error = exception;
        }

        var actual = (value as TsStringValue)?.Value;
        var ok = error is null && value is TsStringValue { ValueType: TsValueType.String }
            && string.Equals(expected, actual, StringComparison.Ordinal);
        _output.WriteLine("C185 for-in " + JsonSerializer.Serialize(new
        {
            row,
            receiverTag = route.ReceiverTag,
            enumerateCount = route.EnumerateCount,
            wrapper = value?.GetType().Name,
            tag = value?.ValueType.ToString(),
            expected,
            actual,
            exception = error?.GetType().FullName,
            ok
        }));
        return ok;
    }
}
