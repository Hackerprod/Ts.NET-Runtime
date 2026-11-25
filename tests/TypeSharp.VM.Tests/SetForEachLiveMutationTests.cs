using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class SetForEachLiveMutationTests(ITestOutputHelper output)
{
    [Fact]
    public void SetForEach_LiveMutationPreservesVisitOrder()
    {
        const string source = """
            function visitInner(values: Set<int32>, visits: any[], flags: any[]): boolean {
                let result: any = values.forEach((value: any, repeated: any, receiver: any) => {
                    visits.push("I");
                    visits.push(value);
                    visits.push(repeated);
                    visits.push(receiver);
                    if (value === 4) { flags.push(values.delete(2)); }
                    return 7;
                });
                if (result === undefined) {
                    if (result === null) return false;
                    return true;
                }
                return false;
            }

            function main(values: Set<int32>, visits: any[], flags: any[], mode: string): int32 {
                let result: any = values.forEach((value: any, repeated: any, receiver: any) => {
                    visits.push("O");
                    visits.push(value);
                    visits.push(repeated);
                    visits.push(receiver);
                    if (value === 2) {
                        if (mode === "delete-current") { flags.push(values.delete(2)); }
                        if (mode === "clear-and-append") {
                            values.clear();
                            values.add(8);
                            values.add(9);
                        }
                        if (mode === "delete-next") {
                            flags.push(values.delete(4));
                            flags.push(values.delete(99));
                        }
                        if (mode === "append-only") {
                            values.add(4);
                            values.add(8);
                        }
                        if (mode === "reinsert") {
                            flags.push(values.delete(4));
                            values.add(4);
                        }
                    }
                    if (value === 4) {
                        if (mode === "nested") { flags.push(visitInner(values, visits, flags)); }
                    }
                    return 7;
                });
                if (result === undefined) {
                    if (result === null) return 3;
                    return 1;
                }
                if (result === null) return 2;
                return 3;
            }
            """;

        var tokens = new Lexer(source).Tokenize();
        Assert.DoesNotContain(tokens, token => token.Kind == TokenKind.Error);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var calls = ir.Functions.SelectMany(function => function.Blocks.SelectMany(block => block.Instructions)
            .Where(instruction => instruction.Opcode == Opcode.Call && instruction.OperandObject is "Set::forEach")
            .Select(instruction => (Function: function.Name, Argc: instruction.Operand1))).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Contains(("main", 2), calls);
        Assert.Contains(("visitInner", 2), calls);
        var bytecode = BytecodeCompiler.Compile(ir);

        (string Mode, (string Tag, int Value)[] Trace, int[] Entries, bool[] Deletes, bool? InnerReturn)[] rows =
        [
            ("delete-current", [("O", 2), ("O", 4), ("O", 6)], [4, 6], [true], null),
            ("clear-and-append", [("O", 2), ("O", 8), ("O", 9)], [8, 9], [], null),
            ("nested", [("O", 2), ("O", 4), ("I", 2), ("I", 4), ("I", 6), ("O", 6)], [4, 6], [true], true),
            ("delete-next", [("O", 2), ("O", 6)], [2, 6], [true, false], null),
            ("append-only", [("O", 2), ("O", 4), ("O", 6), ("O", 8)], [2, 4, 6, 8], [], null),
            ("reinsert", [("O", 2), ("O", 6), ("O", 4)], [2, 6, 4], [true], null)
        ];

        var mismatches = new List<string>();
        foreach (var row in rows)
        {
            var values = new TsSet();
            foreach (int seed in new[] { 2, 4, 6 })
                values.Add(TsValue.FromInt32(seed));
            var receiver = new TsSetValue(values);
            var visits = new TsArray();
            var flags = new TsArray();
            var vm = new TypeSharp.VM.Interpreter.Interpreter();
            TsValue? result = null;
            Exception? escaped = null;
            try
            {
                result = vm.Execute(bytecode, "main",
                    [receiver, new TsArrayValue(visits), new TsArrayValue(flags), TsValue.FromString(row.Mode)]);
            }
            catch (Exception error)
            {
                escaped = error;
            }

            var rawVisits = Snapshot(visits);
            var actualTrace = new List<(string Tag, int Value)>();
            var callbackAbiOk = rawVisits.Length % 4 == 0;
            for (var i = 0; i + 3 < rawVisits.Length; i += 4)
            {
                if (rawVisits[i] is TsStringValue tag && rawVisits[i + 1] is TsInt32Value value)
                {
                    actualTrace.Add((tag.Value, value.Value));
                    callbackAbiOk &= (tag.Value is "O" or "I") &&
                        rawVisits[i + 2] is TsInt32Value repeated && value.Value == repeated.Value &&
                        ReferenceEquals(receiver, rawVisits[i + 3]);
                }
                else
                {
                    callbackAbiOk = false;
                }
            }

            var rawFlags = Snapshot(flags);
            var deleteFlagsOk = row.Deletes.Select((expected, index) =>
                index < rawFlags.Length && rawFlags[index] is TsBoolValue flag && flag.Value == expected).All(matches => matches);
            var flagCountOk = rawFlags.Length == row.Deletes.Length + (row.InnerReturn.HasValue ? 1 : 0);
            var innerReturnOk = row.InnerReturn is not bool expectedInner ||
                (row.Deletes.Length < rawFlags.Length && rawFlags[row.Deletes.Length] is TsBoolValue inner && inner.Value == expectedInner);
            var returnOk = result is TsInt32Value { Value: 1 } && innerReturnOk;
            var rawEntries = values.Entries.ToArray();
            var entriesOk = values.Count == row.Entries.Length && rawEntries.Length == row.Entries.Length &&
                rawEntries.Select((entry, index) => entry is TsInt32Value number && number.Value == row.Entries[index]).All(matches => matches);
            var traceOk = actualTrace.SequenceEqual(row.Trace);

            var problems = new List<string>();
            if (!traceOk) problems.Add("visit-trace");
            if (!callbackAbiOk) problems.Add("callback-abi");
            if (!entriesOk) problems.Add("set-entries");
            if (!deleteFlagsOk || !flagCountOk) problems.Add("flags");
            if (!returnOk) problems.Add("return");
            if (escaped is not null) problems.Add("execute-exception");

            var observation = JsonSerializer.Serialize(new
            {
                mode = row.Mode,
                verdict = problems.Count == 0 ? "PASS" : "MISMATCH",
                problems,
                diagnostics = new { lexer = 0, parser = 0, binder = 0 },
                expectedVisits = row.Trace.SelectMany(item => new[]
                {
                    "string:" + item.Tag, "int32:" + item.Value, "int32:" + item.Value, "receiver:same"
                }),
                observedVisits = rawVisits.Select(item => Describe(item, receiver)),
                expectedVisitCount = row.Trace.Length * 4,
                observedVisitCount = visits.Count,
                callbackAbiOk,
                expectedEntries = row.Entries.Select(item => "int32:" + item),
                observedEntries = rawEntries.Select(item => Describe(item, receiver)),
                expectedSetCount = row.Entries.Length,
                observedSetCount = values.Count,
                expectedDeleteFlags = row.Deletes.Select(item => item ? "bool:true" : "bool:false"),
                expectedFlagCount = row.Deletes.Length + (row.InnerReturn.HasValue ? 1 : 0),
                observedFlags = rawFlags.Select(item => Describe(item, receiver)),
                observedFlagCount = flags.Count,
                expectedOuterReturn = "int32:1",
                observedOuterReturn = Describe(result, receiver),
                expectedInnerReturn = row.InnerReturn,
                observedInnerReturn = row.InnerReturn.HasValue && row.Deletes.Length < rawFlags.Length
                    ? Describe(rawFlags[row.Deletes.Length], receiver) : null,
                returnOk,
                escaped = escaped is null ? null : new { type = escaped.GetType().FullName, message = escaped.Message }
            });
            output.WriteLine("C197_SET_FOREACH_ROW " + observation);
            if (problems.Count != 0)
                mismatches.Add(row.Mode + ":" + string.Join(",", problems));
        }

        Assert.True(mismatches.Count == 0, "C197 Set.forEach semantic mismatches: " + string.Join("; ", mismatches));
    }

    private static TsValue[] Snapshot(TsArray array) =>
        Enumerable.Range(0, array.Count).Select(array.Get).ToArray();

    private static string Describe(TsValue? value, TsSetValue receiver) => value switch
    {
        TsStringValue str => "string:" + str.Value,
        TsInt32Value number => "int32:" + number.Value,
        TsBoolValue flag => flag.Value ? "bool:true" : "bool:false",
        TsSetValue => ReferenceEquals(receiver, value) ? "receiver:same" : "receiver:different",
        null => "<no-result>",
        _ => "<" + value.GetType().Name + ">"
    };
}
