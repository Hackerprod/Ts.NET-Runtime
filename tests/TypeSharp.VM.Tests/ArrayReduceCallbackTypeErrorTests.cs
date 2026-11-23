using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayReduceCallbackTypeErrorTests(ITestOutputHelper output)
{
    private const string Prefix = "C176 Array.reduce ";
    private const string ErrorName = "TypeError";
    private const string ErrorMessage = "Array.reduce callback is not callable";

    [Fact]
    public void Reduce_ThrowsCatchableTypeErrorForNonCallableCallback()
    {
        var seeded = Compile("seeded", ", 10", 3);
        var omitted = Compile("omitted", "", 2);
        Row[] rows =
        [
            new("empty-seeded", seeded, 10, ""),
            new("all-holes-seeded", seeded, 10, "HHH"),
            new("singleton-unseeded", omitted, null, "P"),
            new("singleton-seeded", seeded, 10, "P"),
            new("empty-unseeded", omitted, null, ""),
        ];

        var failures = new List<string>();
        foreach (var row in rows)
            Measure(row, failures);
        Assert.True(failures.Count == 0, "Array.reduce result mismatches: " + string.Join("; ", failures));
    }

    private BytecodeModule Compile(string seed, string seedArgument, int expectedArgc)
    {
        var source = $$"""
            function main(a: number[], cb: any): any {
                try { return a.reduce(cb{{seedArgument}}); } catch (err) { return err; }
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
        Assert.DoesNotContain(block.Instructions.Skip(tryIndex + 1).Take(callIndex - tryIndex - 1),
            instruction => instruction.Opcode == Opcode.LeaveTry);
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
        for (int i = 0; i < row.Holes.Length; i++)
        {
            if (row.Holes[i] == 'H')
                array.SetHole(i);
            else
                array.Set(i, TsValue.FromInt32(2));
        }

        int length = array.Count;
        Assert.Equal(row.Holes.Length, length);
        var observedHoles = new char[length];
        var present = new List<int>();
        for (int i = 0; i < length; i++)
        {
            bool hole = array.IsHole(i);
            Assert.Equal(row.Holes[i] == 'H', hole);
            observedHoles[i] = hole ? 'H' : 'P';
            if (!hole)
            {
                var value = Assert.IsType<TsInt32Value>(array.Get(i)).Value;
                Assert.Equal(2, value);
                present.Add(value);
            }
        }
        var receiver = new TsArrayValue(array);
        var callback = TsValue.FromInt32(7);
        int callbackNumber = Assert.IsType<TsInt32Value>(callback).Value;
        Assert.Equal(7, callbackNumber);

        TsValue? result = null;
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var escaped = Record.Exception(() => { result = vm.Execute(row.Code, "main", [receiver, callback]); });
        var actualObject = (result as TsObjectValue)?.Value;
        var name = actualObject?.Fields.GetValueOrDefault("name");
        var message = actualObject?.Fields.GetValueOrDefault("message");
        bool resultOk = escaped is null &&
            actualObject?.TypeName == ErrorName && name is TsStringValue { Value: ErrorName } &&
            message is TsStringValue { Value: ErrorMessage };
        Write("row", new
        {
            id = row.Id, seed = row.Seed, length, holes = new string(observedHoles), present,
            callbackType = callback.GetType().Name, callbackNumber,
            resultType = result?.GetType().Name, resultNumber = Number(result), resultOk,
            objectType = actualObject?.TypeName, nameType = name?.GetType().Name, name = (name as TsStringValue)?.Value,
            messageType = message?.GetType().Name, message = (message as TsStringValue)?.Value,
            escapedType = escaped?.GetType().FullName, escapedMessage = escaped?.Message,
        });
        if (escaped is not null)
            Write("escape-stack", new { id = row.Id, stack = escaped.StackTrace, innerType = escaped.InnerException?.GetType().FullName });
        if (!resultOk)
        {
            failures.Add(escaped is null
                ? $"{row.Id} returned an unexpected value"
                : $"{row.Id} escaped {escaped.GetType().FullName}: {escaped.Message}");
        }
    }

    private void Write(string label, object data) => output.WriteLine(Prefix + label + " " + JsonSerializer.Serialize(data));

    private static double? Number(TsValue? value) => value switch
    {
        TsInt32Value number => number.Value,
        TsFloat64Value { Value: var number } when double.IsFinite(number) => number,
        _ => null,
    };

    private sealed record Row(string Id, BytecodeModule Code, int? Seed, string Holes);
}
