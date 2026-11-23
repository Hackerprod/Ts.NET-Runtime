using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayReduceEmptyTypeErrorTests(ITestOutputHelper output)
{
    private const string Prefix = "C175 Array.reduce ";
    private const string ErrorName = "TypeError";
    private const string ErrorMessage = "Reduce of empty array with no initial value";

    [Fact]
    public void Reduce_ThrowsCatchableTypeErrorWhenNoAccumulator()
    {
        var omitted = Compile("omitted", "", 2);
        var seeded = Compile("seeded", ", 10", 3);
        var undefined = Compile("undefined", ", undefined", 3);

        Row[] rows =
        [
            new("dense-empty", "omitted", omitted, "", null, 0, ExpectsError: true),
            new("all-holes", "omitted", omitted, "HHH", null, 0, ExpectsError: true),
            new("dense-empty-seeded", "seeded", seeded, "", 10, 0),
            new("all-holes-seeded", "seeded", seeded, "HHH", 10, 0),
            new("dense-empty-undefined", "undefined", undefined, "", null, 0),
            new("all-holes-undefined", "undefined", undefined, "HHH", null, 0),
            new("sole-present", "omitted", omitted, "HPH", 2, 0),
            new("sole-present-undefined", "omitted", omitted, "HPH", null, 0),
            new("sparse-normal", "omitted", omitted, "HPHP", 8, 1),
        ];

        var failures = new List<string>();
        foreach (var row in rows)
            Measure(row, failures);
        Assert.True(failures.Count == 0, "Array.reduce result mismatches: " + string.Join("; ", failures));
    }

    private BytecodeModule Compile(string seed, string seedArgument, int expectedArgc)
    {
        var source = $$"""
            function main(a: number[], calls: number[]): any {
                try {
                    return a.reduce((acc: number, value: number): number => {
                        calls[0] = calls[0] + 1;
                        return acc + value;
                    }{{seedArgument}});
                } catch (err) {
                    return err;
                }
            }
            """;
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        Write("diagnostics", new { seed, parser = parser.Diagnostics.Count(), binder = binder.Diagnostics.All.Count() });
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var instructions = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.Call);
        var target = Assert.IsType<string>(call.OperandObject);
        Assert.Equal("Array::reduce", target);
        Assert.Equal(expectedArgc, call.Operand1);
        var block = Assert.Single(main.Blocks, candidate => candidate.Instructions.Contains(call));
        var enterTry = Assert.Single(instructions, instruction => instruction.Opcode == Opcode.EnterTry);
        int tryIndex = block.Instructions.IndexOf(enterTry);
        int callIndex = block.Instructions.IndexOf(call);
        Assert.True(tryIndex >= 0 && tryIndex < callIndex);
        var handler = Assert.Single(main.Blocks, candidate => candidate.Id == enterTry.Operand0);
        var handlerOps = handler.Instructions.Select(instruction => instruction.Opcode).ToArray();
        Assert.Equal(new[] { Opcode.StoreLocal, Opcode.LoadLocal, Opcode.Return }, handlerOps);
        int catchStore = handler.Instructions[0].Operand0;
        int catchLoad = handler.Instructions[1].Operand0;
        Assert.Equal(catchStore, catchLoad);
        Write("route", new
        {
            seed, target, argc = call.Operand1, block = block.Id, tryIndex, callIndex,
            handler = handler.Id, handlerOps = handlerOps.Select(op => op.ToString()), catchStore, catchLoad,
        });
        return BytecodeCompiler.Compile(ir);
    }

    private void Measure(Row row, List<string> failures)
    {
        var array = new TsArray();
        if (row.Holes.Length > 0)
            array.SetHole(row.Holes.Length - 1);
        TsValue? present = row.Id switch
        {
            "sole-present" or "sparse-normal" => TsValue.FromInt32(2),
            "sole-present-undefined" => TsValue.Void,
            _ => null,
        };
        if (present is not null)
            array.Set(1, present);
        var six = TsValue.FromInt32(6);
        if (row.Id == "sparse-normal")
            array.Set(3, six);

        var calls = new TsArray();
        calls.Set(0, TsValue.FromInt32(0));
        Assert.Equal(row.Holes.Length, array.Count);
        var observedHoles = new char[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            bool hole = array.IsHole(i);
            Assert.Equal(row.Holes[i] == 'H', hole);
            observedHoles[i] = hole ? 'H' : 'P';
        }
        if (present is not null)
            Assert.Same(present, array.Get(1));
        if (row.Id == "sparse-normal")
            Assert.Same(six, array.Get(3));
        Assert.Equal(1, calls.Count);
        Assert.False(calls.IsHole(0));
        Assert.Equal(0, Assert.IsType<TsInt32Value>(calls.Get(0)).Value);
        int length = array.Count;

        TsValue? result = null;
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var receiver = new TsArrayValue(array);
        var counter = new TsArrayValue(calls);
        var escaped = Record.Exception(() => { result = vm.Execute(row.Code, "main", [receiver, counter]); });

        var actualCounter = calls.Get(0);
        var actualObject = (result as TsObjectValue)?.Value;
        var name = actualObject?.Fields.GetValueOrDefault("name");
        var message = actualObject?.Fields.GetValueOrDefault("message");
        var counterNumber = Number(actualCounter);
        var resultNumber = Number(result);
        bool resultOk = row.ExpectsError
            ? actualObject?.TypeName == ErrorName && name is TsStringValue { Value: ErrorName } &&
                message is TsStringValue { Value: ErrorMessage }
            : row.ExpectedNumber is int expected ? resultNumber == expected : ReferenceEquals(result, TsValue.Void);
        bool counterOk = calls.Count == 1 && !calls.IsHole(0) && counterNumber == row.ExpectedCalls;
        Write("row", new
        {
            id = row.Id, seed = row.Seed, length, holes = new string(observedHoles),
            counterType = actualCounter.GetType().Name, counter = counterNumber,
            expectedCounter = row.ExpectedCalls, counterOk, resultType = result?.GetType().Name,
            resultNumber, resultIsVoid = ReferenceEquals(result, TsValue.Void),
            expectedResult = row.ExpectsError ? "TypeError" : row.ExpectedNumber is null ? "undefined" : "number",
            row.ExpectedNumber, resultOk, objectType = actualObject?.TypeName,
            nameType = name?.GetType().Name, name = (name as TsStringValue)?.Value,
            messageType = message?.GetType().Name, message = (message as TsStringValue)?.Value,
            escapedType = escaped?.GetType().FullName, escapedMessage = escaped?.Message,
        });
        if (escaped is not null)
        {
            Write("escape-stack", new { id = row.Id, stack = escaped.StackTrace, innerType = escaped.InnerException?.GetType().FullName });
            failures.Add($"{row.Id} escaped {escaped.GetType().FullName}: {escaped.Message}");
        }
        else if (!resultOk)
        {
            failures.Add($"{row.Id} returned an unexpected value");
        }
        if (!counterOk)
            failures.Add($"{row.Id} callback counter changed unexpectedly");
    }

    private void Write(string label, object data) => output.WriteLine(Prefix + label + " " + JsonSerializer.Serialize(data));

    private static double? Number(TsValue? value) => value switch
    {
        TsInt32Value number => number.Value,
        TsFloat64Value { Value: var number } when double.IsFinite(number) => number,
        _ => null,
    };

    private sealed record Row(string Id, string Seed, BytecodeModule Code, string Holes, int? ExpectedNumber,
        int ExpectedCalls, bool ExpectsError = false);
}
