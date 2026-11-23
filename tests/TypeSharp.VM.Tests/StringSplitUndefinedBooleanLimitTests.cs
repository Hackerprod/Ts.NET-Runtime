using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class StringSplitUndefinedBooleanLimitTests(ITestOutputHelper output)
{
    [Fact]
    public void Split_HonorsBooleanLimitWithUndefinedSeparator()
    {
        const string Whole = "aundefinedb";
        var rows = new (string Id, string Text, string Expression, string? Separator, string[] Expected)[]
        {
            ("false-nonempty", Whole, "undefined, false", null, []),
            ("false-empty", "", "undefined, false", null, []),
            ("true-nonempty", Whole, "undefined, true", null, [Whole]),
            ("true-empty", "", "undefined, true", null, [""]),
            ("zero", Whole, "undefined, 0", null, []),
            ("one", Whole, "undefined, 1", null, [Whole]),
            ("omitted-limit", Whole, "undefined", null, [Whole]),
            ("undefined-limit", Whole, "undefined, undefined", null, [Whole]),
            ("present-comma-false", "a,b", "separator, false", ",", [])
        };
        var invalid = new List<string>();
        var mismatches = new List<string>();

        foreach (var row in rows)
        {
            string parameters = row.Separator is null ? "text: string" : "text: string, separator: string";
            string source = $"function main({parameters}): string[] {{ return text.split({row.Expression}); }}";
            TsValue[] inputs = row.Separator is null
                ? [TsValue.FromString(row.Text)]
                : [TsValue.FromString(row.Text), TsValue.FromString(row.Separator)];
            var inputTypes = inputs.Select(value => value.GetType().Name).ToArray();
            var tokens = new Lexer(source).Tokenize();
            int lexerCount = tokens.Count(token => token.Kind == TokenKind.Error);
            var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
            var binder = new TypeSharp.Semantics.Binder.Binder();
            var bound = binder.Bind(parser.Parse());
            int parserCount = parser.Diagnostics.Count();
            int binderCount = binder.Diagnostics.All.Count();
            var operations = Array.Empty<Instruction>();
            bool validRoute = false;
            TsValue? result = null;
            Exception? exception = Record.Exception(() =>
            {
                if (lexerCount + parserCount + binderCount != 0)
                    return;
                var ir = new IRGenerator().Generate(bound);
                var main = Assert.Single(ir.Functions, f => f.Name == "main");
                operations = Assert.Single(main.Blocks).Instructions.ToArray();
                var call = Assert.Single(operations, op => op.Opcode == Opcode.Call);
                int argc = row.Id == "omitted-limit" ? 2 : 3;
                var expected = new List<Instruction>
                {
                    new(Opcode.LoadArg, 0),
                    row.Separator is null ? new(Opcode.LoadConst_Void) : new(Opcode.LoadArg, 1)
                };
                switch (row.Id)
                {
                    case "omitted-limit": break;
                    case "undefined-limit": expected.Add(new(Opcode.LoadConst_Void)); break;
                    case "zero":
                    case "one":
                        int value = row.Id == "zero" ? 0 : 1;
                        expected.Add(operations.Length > 2 && operations[2].Opcode == Opcode.LoadConst_I32
                            ? new(Opcode.LoadConst_I32, value)
                            : new(Opcode.LoadConst_F64, operandObject: (double)value));
                        break;
                    default:
                        expected.Add(new(Opcode.LoadConst_Bool, row.Id is "true-nonempty" or "true-empty" ? 1 : 0));
                        break;
                }
                expected.Add(new(Opcode.Call, 0, argc, "String::split"));
                expected.Add(new(Opcode.Return));
                validRoute = main.Parameters.Select(p => p.Name).SequenceEqual(
                    row.Separator is null ? new[] { "text" } : new[] { "text", "separator" })
                    && operations.Length == expected.Count
                    && operations.Zip(expected).All(pair =>
                        pair.First.Opcode == pair.Second.Opcode
                        && pair.First.Operand0 == pair.Second.Operand0
                        && pair.First.Operand1 == pair.Second.Operand1
                        && Equals(pair.First.OperandObject, pair.Second.OperandObject))
                    && call.OperandObject is string;
                if (validRoute)
                    result = new TypeSharp.VM.Interpreter.Interpreter().Execute(BytecodeCompiler.Compile(ir), "main", inputs);
            });

            int? count = null;
            bool dense = false;
            var actual = new List<string?>();
            var itemTypes = new List<string?>();
            if (result is TsArrayValue array)
            {
                count = array.Value.Count;
                dense = true;
                for (int i = 0; i < count; i++)
                {
                    bool hole = array.Value.IsHole(i);
                    dense &= !hole;
                    var item = hole ? null : array.Value.Get(i);
                    itemTypes.Add(item?.GetType().Name);
                    actual.Add((item as TsStringValue)?.Value);
                }
            }
            var calls = operations.Where(op => op.Opcode == Opcode.Call).ToArray();
            output.WriteLine("C179 String.split " + JsonSerializer.Serialize(new
            {
                row = row.Id, row.Expression, row.Text, row.Separator, inputTypes,
                lexerCount, parserCount, binderCount, validRoute,
                operations = operations.Select(op => new { opcode = op.Opcode.ToString(), op.Operand0, op.Operand1, op.OperandObject }),
                target = calls.Length == 1 ? calls[0].OperandObject : null,
                argc = calls.Length == 1 ? (int?)calls[0].Operand1 : null,
                returned = result is not null, returnType = result?.GetType().Name,
                dense, count, itemTypes, actual, expected = row.Expected,
                exception = exception?.GetType().FullName
            }));

            if (!validRoute || lexerCount + parserCount + binderCount != 0 || exception is not null
                || inputTypes.Any(type => type != nameof(TsStringValue)) || result is not TsArrayValue
                || !dense || itemTypes.Any(type => type != nameof(TsStringValue)))
                invalid.Add(row.Id);
            else if (count != row.Expected.Length || !row.Expected.SequenceEqual(actual, StringComparer.Ordinal))
                mismatches.Add(row.Id);
        }

        Assert.True(invalid.Count == 0, "C179_INVALID_SHAPES " + string.Join(",", invalid));
        Assert.True(mismatches.Count == 0, "C179_SEMANTIC_MISMATCHES " + string.Join(",", mismatches));
    }
}
