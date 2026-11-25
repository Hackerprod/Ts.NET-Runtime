using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class OptionalComputedDeleteTests(ITestOutputHelper output)
{
    private const string Source = """
        function takeReceiver(value: any, trace: int32[]): any {
            trace[0] = trace[0] + 1;
            trace[2] = trace[2] * 10 + 1;
            return value;
        }
        function takeKey(trace: int32[]): int32 {
            trace[1] = trace[1] + 1;
            trace[2] = trace[2] * 10 + 2;
            return 0;
        }
        function main(receiver: any, trace: int32[]): bool {
            return delete takeReceiver(receiver, trace)?.[takeKey(trace)];
        }
        """;

    [Fact]
    public void Delete_OptionalComputedShortCircuitsNullish()
    {
        var tokens = new Lexer(Source, "c196-optional-computed-delete.ts").Tokenize();
        int lexerCount = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerCount);

        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        int parserCount = parser.Diagnostics.Count;
        Assert.Empty(parser.Diagnostics);

        var binder = new Binder();
        var bound = binder.Bind(syntax);
        int binderCount = binder.Diagnostics.All.Count;
        Assert.Empty(binder.Diagnostics.All);

        var bytecode = BytecodeCompiler.Compile(new IRGenerator().Generate(bound));
        (string Name, TsValue Argument, int[] ExpectedTrace)[] rows =
        [
            ("explicit-null", TsValue.Null, [1, 0, 1]),
            ("explicit-undefined", TsValue.Void, [1, 0, 1]),
            ("populated-array", ArrayOf(7, 8), [1, 1, 12])
        ];

        var failures = new List<string>();
        foreach (var row in rows)
        {
            var live = row.Argument as TsArrayValue;
            var backing = live?.Value;
            var liveBefore = backing is null ? null : Snapshot(backing);
            var trace = ArrayOf(0, 0, 0);
            var traceBefore = Snapshot(trace.Value);

            var vm = new TypeSharp.VM.Interpreter.Interpreter();
            TsValue? result = null;
            Exception? escaped = null;
            try
            {
                result = vm.Execute(bytecode, "main", [row.Argument, trace]);
            }
            catch (Exception exception)
            {
                escaped = exception;
            }

            var traceAfter = Snapshot(trace.Value);
            var liveAfter = backing is null ? null : Snapshot(backing);
            bool? sameBacking = live is null ? null : ReferenceEquals(backing, live.Value);
            bool? slotZeroReadsVoid = backing is null ? null : ReferenceEquals(backing.Get(0), TsValue.Void);
            var resultBool = (result as TsBoolValue)?.Value;

            var mismatches = new List<string>();
            if (escaped is not null)
                mismatches.Add("execute.exception");
            if (result is not TsBoolValue || result.ValueType != TsValueType.Bool)
                mismatches.Add("result.bool-type");
            if (resultBool != true)
                mismatches.Add("result.bool-value");
            if (traceBefore.Length != 3 || traceBefore.Any(slot => !IsInt32(slot, 0)))
                mismatches.Add("trace.before");
            if (traceAfter.Length != 3)
                mismatches.Add("trace.length");
            string[] labels = ["trace.receiver-calls", "trace.key-calls", "trace.order"];
            for (int i = 0; i < labels.Length; i++)
            {
                if (i >= traceAfter.Length || !IsInt32(traceAfter[i], row.ExpectedTrace[i]))
                    mismatches.Add(labels[i]);
            }

            if (liveBefore is not null && liveAfter is not null)
            {
                if (liveBefore.Length != 2 || !IsInt32(liveBefore[0], 7) || !IsInt32(liveBefore[1], 8))
                    mismatches.Add("live.before");
                if (sameBacking != true)
                    mismatches.Add("live.backing");
                if (liveAfter.Length != 2)
                    mismatches.Add("live.length");
                if (liveAfter.Length == 0 || !liveAfter[0].Hole || slotZeroReadsVoid != true)
                    mismatches.Add("live.slot-zero-hole");
                if (liveAfter.Length < 2 || !IsInt32(liveAfter[1], 8))
                    mismatches.Add("live.slot-one");
            }

            output.WriteLine("C196_ROW " + JsonSerializer.Serialize(new
            {
                row = row.Name,
                argumentWrapper = row.Argument.GetType().Name,
                argumentTag = row.Argument.ValueType.ToString(),
                lexerCount, parserCount, binderCount,
                expectedBoolWrapper = nameof(TsBoolValue),
                expectedBoolTag = nameof(TsValueType.Bool),
                expectedBoolValue = true,
                actualWrapper = result?.GetType().Name,
                actualTag = result?.ValueType.ToString(),
                actualBoolValue = resultBool,
                expectedTrace = row.ExpectedTrace,
                traceBefore,
                traceAfter,
                traceLength = traceAfter.Length,
                actualReceiverCalls = traceAfter.ElementAtOrDefault(0)?.Int32,
                actualKeyCalls = traceAfter.ElementAtOrDefault(1)?.Int32,
                actualOrder = traceAfter.ElementAtOrDefault(2)?.Int32,
                expectedLiveBefore = live is null ? null : "[7,8]",
                expectedLiveAfter = live is null ? null : "[H,8]",
                liveBefore,
                liveAfter,
                liveLength = liveAfter?.Length,
                slotZeroReadsVoid,
                sameBacking,
                exceptionType = escaped?.GetType().FullName,
                exceptionMessage = escaped?.Message,
                verdict = mismatches.Count == 0 ? "PASS" : "FAIL",
                mismatches
            }));

            if (mismatches.Count != 0)
                failures.Add(row.Name + ": " + string.Join(", ", mismatches));
        }

        Assert.True(failures.Count == 0, "C196_OPTIONAL_COMPUTED_DELETE " + string.Join("; ", failures));
    }

    private static TsArrayValue ArrayOf(params int[] items)
    {
        var backing = new TsArray(items.Length);
        foreach (var item in items)
            backing.Add(new TsInt32Value(item));
        return new TsArrayValue(backing);
    }

    private static Slot[] Snapshot(TsArray backing)
    {
        var slots = new Slot[backing.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            bool hole = backing.IsHole(i);
            TsValue value = backing.Get(i);
            slots[i] = new(hole, value.GetType().Name, value.ValueType.ToString(), (value as TsInt32Value)?.Value);
        }
        return slots;
    }

    private static bool IsInt32(Slot slot, int expected) =>
        !slot.Hole && slot.Wrapper == nameof(TsInt32Value) &&
        slot.Tag == nameof(TsValueType.Int32) && slot.Int32 == expected;

    private sealed record Slot(bool Hole, string Wrapper, string Tag, int? Int32);
}
