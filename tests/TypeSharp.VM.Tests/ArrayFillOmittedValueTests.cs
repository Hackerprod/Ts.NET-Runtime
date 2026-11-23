using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFillOmittedValueTests(ITestOutputHelper output)
{
    [Fact]
    public void ArrayFill_MaterializesUndefinedWhenValueIsOmitted()
    {
        var rows = new (string Id, int?[] Initial, bool ExplicitUndefined)[]
        {
            ("omitted-sparse", [17, null, 23], false),
            ("omitted-dense", [17, 19, 23], false),
            ("omitted-empty", [], false),
            ("explicit-undefined-sparse", [17, null, 23], true)
        };
        var invalid = new List<string>();
        var mismatches = new List<string>();

        foreach (var row in rows)
        {
            var array = new TsArray();
            TsValue?[] originals = row.Initial.Select<int?, TsValue?>(
                value => value.HasValue ? TsValue.FromInt32(value.Value) : null).ToArray();
            foreach (var value in originals)
            {
                if (value is null) array.SetHole(array.Count);
                else array.Add(value);
            }
            var wrapper = new TsArrayValue(array);
            var before = Snapshot(array, originals);
            var expression = row.ExplicitUndefined ? "undefined" : "";
            var source = $"function main(a: any[]): any[] {{ return a.fill({expression}); }}";
            var tokens = new Lexer(source).Tokenize();
            int lexer = tokens.Count(token => token.Kind == TokenKind.Error);
            var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
            var tree = parser.Parse();
            var binder = new TypeSharp.Semantics.Binder.Binder();
            var bound = binder.Bind(tree);
            int parserCount = parser.Diagnostics.Count();
            int binderCount = binder.Diagnostics.All.Count();

            Instruction[] operations = [];
            bool validRoute = false;
            TsValue? result = null;
            Exception? error = null;
            if (lexer == 0 && parserCount == 0 && binderCount == 0)
            {
                var ir = new IRGenerator().Generate(bound);
                var mains = ir.Functions.Where(f => f.Name == "main").ToArray();
                if (mains.Length == 1)
                {
                    operations = mains[0].Blocks.SelectMany(block => block.Instructions).ToArray();
                    Instruction[] expected = row.ExplicitUndefined
                        ? [new(Opcode.LoadArg), new(Opcode.LoadConst_Void), new(Opcode.Call, 0, 2, "Array::fill"), new(Opcode.Return)]
                        : [new(Opcode.LoadArg), new(Opcode.Call, 0, 1, "Array::fill"), new(Opcode.Return)];
                    validRoute = mains[0].Parameters.Select(p => p.Name).SequenceEqual(new[] { "a" })
                        && operations.Length == expected.Length
                        && operations.Zip(expected).All(pair =>
                            pair.First.Opcode == pair.Second.Opcode
                            && pair.First.Operand0 == pair.Second.Operand0
                            && pair.First.Operand1 == pair.Second.Operand1
                            && Equals(pair.First.OperandObject, pair.Second.OperandObject));
                    if (validRoute)
                    {
                        var bytecode = BytecodeCompiler.Compile(ir);
                        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
                        TsValue[] args = [wrapper];
                        error = Record.Exception(() => { result = interpreter.Execute(bytecode, "main", args); });
                    }
                }
            }

            var after = Snapshot(array, originals);
            bool initialExact = before.Length == originals.Length && before.Select((slot, i) =>
                originals[i] is null
                    ? !slot.Present && slot.VoidIdentity && !slot.OriginalIdentity
                    : slot.Present && !slot.VoidIdentity && slot.OriginalIdentity).All(matches => matches);
            bool sameWrapper = ReferenceEquals(wrapper, result);
            bool expectedOutcome = error is null && result is TsArrayValue && sameWrapper
                && after.Length == originals.Length && after.All(slot => slot.Present && slot.VoidIdentity);
            bool originalFailure = !row.ExplicitUndefined && originals.Length != 0
                && error?.GetType() == typeof(IndexOutOfRangeException) && result is null
                && before.SequenceEqual(after);
            output.WriteLine("C180 Array.fill " + JsonSerializer.Serialize(new
            {
                row = row.Id, source, lexer, parser = parserCount, binder = binderCount, validRoute,
                operations = operations.Select(op => new { opcode = op.Opcode.ToString(), op.Operand0, op.Operand1, op.OperandObject }),
                beforeLength = before.Length, before, afterLength = after.Length, after,
                resultType = result?.GetType().FullName ?? "none", sameWrapper,
                exception = error?.GetType().FullName ?? "none",
                expected = new { length = originals.Length, sameWrapper = true, allPresent = true, allVoidIdentity = true, exception = "none" }
            }));
            if (!validRoute || !initialExact || (!expectedOutcome && !originalFailure))
                invalid.Add(row.Id);
            else if (!expectedOutcome)
                mismatches.Add(row.Id);
        }

        Assert.True(invalid.Count == 0, "C180_INVALID_SHAPES " + string.Join(",", invalid));
        Assert.True(mismatches.Count == 0, "C180_SEMANTIC_MISMATCHES " + string.Join(",", mismatches));
    }

    private sealed record Slot(bool Present, string Type, int? Value, bool OriginalIdentity, bool VoidIdentity);

    private static Slot[] Snapshot(TsArray array, TsValue?[] originals) =>
        Enumerable.Range(0, array.Count).Select(i =>
        {
            var value = array.Get(i);
            return new Slot(!array.IsHole(i), value.GetType().FullName!, (value as TsInt32Value)?.Value,
                i < originals.Length && ReferenceEquals(originals[i], value), ReferenceEquals(TsValue.Void, value));
        }).ToArray();
}
