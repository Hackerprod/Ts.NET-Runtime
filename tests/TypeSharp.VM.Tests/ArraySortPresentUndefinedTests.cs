using System.Buffers.Binary;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArraySortPresentUndefinedTests(ITestOutputHelper output)
{
    [Fact]
    public void Sort_ExplicitComparatorSkipsPresentUndefined()
    {
        const string source = """
            function main(values: any[], seen: string[]): any[] {
                return values.sort((left: any, right: any): number => {
                    seen[0] = "called";
                    return left === undefined ? -1 : right === undefined ? 1 : 0;
                });
            }
            """;
        var tokens = new Lexer(source, "c203-array-sort-present-undefined.ts").Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var tree = parser.Parse();
        var binder = new Binder();
        var bound = binder.Bind(tree);
        var diagnostics = new
        {
            lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error),
            parser = parser.Diagnostics.Count,
            binder = binder.Diagnostics.All.Count
        };
        Assert.Equal(0, diagnostics.lexerErrors);
        Assert.Equal(0, diagnostics.parser);
        Assert.Equal(0, diagnostics.binder);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var comparator = Assert.Single(ir.Functions, function => function.Name != main.Name);
        Assert.Equal(2, main.Parameters.Count);
        Assert.Equal(2, comparator.Parameters.Count);
        var mainOps = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        var call = Assert.Single(mainOps, instruction => instruction.Opcode == Opcode.Call);
        var closure = Assert.Single(mainOps, instruction => instruction.Opcode == Opcode.MakeClosure);
        Assert.DoesNotContain(mainOps, instruction => instruction.Opcode is
            Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        var callTarget = Assert.IsType<string>(call.OperandObject);
        var comparatorTarget = Assert.IsType<string>(closure.OperandObject);
        Assert.Equal("Array::sort", callTarget);
        Assert.Equal(2, call.Operand1);
        Assert.Equal(comparator.Name, comparatorTarget);
        Assert.Equal(1, closure.Operand1);
        Assert.Equal("seen", Assert.Single(comparator.CapturedVariables));
        Assert.True(Array.IndexOf(mainOps, closure) < Array.IndexOf(mainOps, call));
        Assert.Single(comparator.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Opcode == Opcode.StoreElement);
        var irRoute = new
        {
            callTarget,
            callArgc = call.Operand1,
            comparatorTarget,
            captureCount = closure.Operand1,
            captured = comparator.CapturedVariables.ToArray()
        };

        var compiled = BytecodeCompiler.Compile(ir);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        Assert.Equal(compiled.Name, restored.Name);
        Assert.Equal(compiled.Functions.Select(function => function.Name).ToArray(),
            restored.Functions.Select(function => function.Name).ToArray());
        var restoredMain = CheckRoundTrip(compiled, restored, main.Name);
        var restoredComparator = CheckRoundTrip(compiled, restored, comparatorTarget);
        var routes = ReadRoutes(restoredMain);
        var closureRoute = Assert.Single(routes, route => route.Opcode == Opcodes.MakeClosure);
        var callRoute = Assert.Single(routes, route => route.Opcode == Opcodes.Call);
        Assert.True(closureRoute.Offset < callRoute.Offset);
        Assert.Equal(comparatorTarget, closureRoute.Target);
        Assert.Equal(irRoute.captureCount, closureRoute.Count);
        Assert.Equal(callTarget, callRoute.Target);
        Assert.Equal(irRoute.callArgc, callRoute.Count);

        var originalVoid = TsValue.Void;
        var originalText = TsValue.FromString("a");
        var originalWitness = new TsStringValue("untouched");
        var receiverArray = new TsArray();
        receiverArray.Add(originalVoid);
        receiverArray.Add(originalText);
        var receiver = new TsArrayValue(receiverArray);
        var witnessArray = new TsArray();
        witnessArray.Add(originalWitness);
        var witness = new TsArrayValue(witnessArray);

        ArrayState? Snapshot(TsArray? array)
        {
            if (array is null)
                return null;
            var slots = Enumerable.Range(0, array.Count).Select(index =>
            {
                bool present = !array.IsHole(index);
                var value = array.Get(index);
                return new SlotState(present, value.GetType().FullName, value.ValueType.ToString(),
                    (value as TsStringValue)?.Value, ReferenceEquals(value, originalVoid),
                    ReferenceEquals(value, originalText), ReferenceEquals(value, originalWitness));
            }).ToArray();
            return new ArrayState(array.Count, new string(slots.Select(slot => slot.Present ? 'P' : 'H').ToArray()), slots);
        }

        var beforeReceiver = Snapshot(receiver.Value)!;
        var beforeWitness = Snapshot(witness.Value)!;
        Assert.True(MatchesReceiver(beforeReceiver, undefinedIndex: 0));
        Assert.True(MatchesWitness(beforeWitness));
        Assert.NotSame(receiver.Value, witness.Value);

        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var result = vm.Execute(restored, "main", [receiver, witness]);
        var returnedArray = (result as TsArrayValue)?.Value;
        var sameWrapper = ReferenceEquals(result, receiver);
        var sameBacking = ReferenceEquals(returnedArray, receiver.Value);
        var afterResult = Snapshot(returnedArray);
        var afterReceiver = Snapshot(receiver.Value)!;
        var afterWitness = Snapshot(witness.Value)!;
        var resultRecord = JsonSerializer.Serialize(new
        {
            source,
            diagnostics,
            ir = irRoute,
            bytecode = new
            {
                module = restored.Name,
                main = DescribeFunction(restoredMain),
                comparator = DescribeFunction(restoredComparator),
                closure = closureRoute,
                call = callRoute
            },
            before = new { receiver = beforeReceiver, witness = beforeWitness },
            returned = true,
            result = new { wrapper = result?.GetType().FullName, tag = result?.ValueType.ToString(), sameWrapper, sameBacking, state = afterResult },
            receiver = afterReceiver,
            witness = afterWitness
        });
        output.WriteLine("C203_ARRAY_SORT_PRESENT_UNDEFINED " + resultRecord);

        Assert.True(result is TsArrayValue { ValueType: TsValueType.Array }
            && sameWrapper && sameBacking && afterResult is not null
            && MatchesReceiver(afterResult, undefinedIndex: 1)
            && MatchesReceiver(afterReceiver, undefinedIndex: 1)
            && MatchesWitness(afterWitness),
            "C203_PRESENT_UNDEFINED_MISMATCH: expected the original receiver with present [string a, undefined] and untouched witness");
    }

    private static BytecodeFunction CheckRoundTrip(BytecodeModule compiled, BytecodeModule restored, string name)
    {
        var original = Assert.Single(compiled.Functions, function => function.Name == name);
        var copy = Assert.Single(restored.Functions, function => function.Name == name);
        Assert.Same(copy, restored.Functions[restored.FunctionIndex[name]]);
        Assert.Equal(2, original.ParameterCount);
        Assert.Equal(original.ParameterCount, copy.ParameterCount);
        Assert.Equal(original.Instructions, copy.Instructions);
        // TSBC stores its strings per function, not in a separate module-wide table.
        Assert.Equal(original.StringConstants, copy.StringConstants);
        return copy;
    }

    private static List<Route> ReadRoutes(BytecodeFunction function)
    {
        var bytes = function.Instructions;
        var routes = new List<Route>();
        for (int offset = 0; offset < bytes.Length;)
        {
            byte opcode = bytes[offset];
            Assert.True(OpcodeFormats.TryGet(opcode, out var format));
            var nextOffset = offset + 1 + format.OperandBytes;
            Assert.InRange(nextOffset, offset + 1, bytes.Length);
            if (opcode is Opcodes.MakeClosure or Opcodes.Call)
            {
                Assert.Equal(8, format.OperandBytes);
                int index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4));
                int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4));
                Assert.InRange(index, 0, function.StringConstants.Length - 1);
                routes.Add(new Route(opcode, offset, index, function.StringConstants[index], count));
            }
            Assert.False(opcode is Opcodes.CallVirt or Opcodes.CallHost or Opcodes.CallDynamic or Opcodes.CallDynamicArray);
            offset = nextOffset;
        }
        return routes;
    }

    private static object DescribeFunction(BytecodeFunction function) => new
    {
        function.Name,
        function.ParameterCount,
        instructions = Convert.ToHexString(function.Instructions),
        stringPool = function.StringConstants
    };

    private static bool MatchesReceiver(ArrayState state, int undefinedIndex) =>
        state.Length == 2 && state.Mask == "PP" &&
        state.Slots[undefinedIndex] is { IsOriginalVoid: true, Payload: null } undefined &&
        undefined.Wrapper == typeof(TsVoid).FullName && undefined.Tag == nameof(TsValueType.Void) &&
        state.Slots[1 - undefinedIndex] is { IsOriginalText: true, Payload: "a" } text &&
        text.Wrapper == typeof(TsStringValue).FullName && text.Tag == nameof(TsValueType.String);

    private static bool MatchesWitness(ArrayState state) =>
        state.Length == 1 && state.Mask == "P" &&
        state.Slots[0] is { IsOriginalWitness: true, Payload: "untouched" } witness &&
        witness.Wrapper == typeof(TsStringValue).FullName && witness.Tag == nameof(TsValueType.String);

    private sealed record Route(byte Opcode, int Offset, int PoolIndex, string Target, int Count);
    private sealed record ArrayState(int Length, string Mask, SlotState[] Slots);
    private sealed record SlotState(bool Present, string? Wrapper, string Tag, string? Payload,
        bool IsOriginalVoid, bool IsOriginalText, bool IsOriginalWitness);
}
