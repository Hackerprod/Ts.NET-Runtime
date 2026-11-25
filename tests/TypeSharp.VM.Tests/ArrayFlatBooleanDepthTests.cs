using System.Buffers.Binary;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFlatBooleanDepthTests(ITestOutputHelper output)
{
    private const string Source = "function main(a: any[], depth: any): any[] { return a.flat(depth); }";
    private const string Target = "Array::flat";
    private const string StockError = "Array.flat depth must be a finite number or undefined";

    [Fact]
    public void Flat_ExplicitBooleanDepthUsesZeroOrOne()
    {
        var route = Compile();
        Slot[] expectedInput = [new("hole"), Int(1), ArraySlot(Int(2), ArraySlot(Int(3))), Int(4)];
        var mismatches = new List<string>();
        foreach (bool expectedDepth in new[] { true, false })
        {
            var row = expectedDepth ? "boolean-true" : "boolean-false";
            var one = TsValue.FromInt32(1);
            var two = TsValue.FromInt32(2);
            var three = TsValue.FromInt32(3);
            var four = TsValue.FromInt32(4);
            var inner = ArrayOf(three);
            var innerBacking = inner.Value;
            var outer = ArrayOf(two, inner);
            var outerBacking = outer.Value;
            var backing = new TsArray();
            backing.SetHole(0);
            backing.Add(one);
            backing.Add(outer);
            backing.Add(four);
            var receiver = new TsArrayValue(backing);

            var before = Snapshot(receiver.Value);
            Require(SameSlots(expectedInput, before), row + " initial receiver");
            TsValue[] args = [receiver, TsValue.FromBool(expectedDepth)];
            var depthType = args[1].GetType();
            bool? depthValue = args[1] is TsBoolValue boolean ? boolean.Value : null;
            Require(args.Length == 2 && ReferenceEquals(args[0], receiver)
                && depthType == typeof(TsBoolValue) && depthValue == expectedDepth, row + " host arguments");

            var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
            TsValue? actual = null;
            Exception? escaped = null;
            bool returned = false;
            try
            {
                actual = interpreter.Execute(route.Module, "main", args);
                returned = true;
            }
            catch (Exception ex)
            {
                escaped = ex;
            }

            var after = Snapshot(receiver.Value);
            var actualOuter = PresentAt(receiver.Value, 2) as TsArrayValue;
            var actualInner = actualOuter is null ? null : PresentAt(actualOuter.Value, 1) as TsArrayValue;
            bool rootHolePreserved = receiver.Value.Count > 0 && receiver.Value.IsHole(0);
            bool rootBackingPreserved = ReferenceEquals(backing, receiver.Value);
            bool outerWrapperPreserved = ReferenceEquals(outer, actualOuter);
            bool outerBackingPreserved = ReferenceEquals(outerBacking, actualOuter?.Value);
            bool innerWrapperPreserved = ReferenceEquals(inner, actualInner);
            bool innerBackingPreserved = ReferenceEquals(innerBacking, actualInner?.Value);
            bool inputIntSlotsPreserved = ReferenceEquals(one, PresentAt(receiver.Value, 1))
                && ReferenceEquals(four, PresentAt(receiver.Value, 3))
                && actualOuter is not null && ReferenceEquals(two, PresentAt(actualOuter.Value, 0))
                && actualInner is not null && ReferenceEquals(three, PresentAt(actualInner.Value, 0));
            bool inputUnchanged = SameSlots(before, after) && rootHolePreserved && rootBackingPreserved
                && outerWrapperPreserved && outerBackingPreserved && innerWrapperPreserved && innerBackingPreserved
                && inputIntSlotsPreserved;

            Slot[] expected = expectedDepth
                ? [Int(1), Int(2), ArraySlot(Int(3)), Int(4)]
                : [Int(1), ArraySlot(Int(2), ArraySlot(Int(3))), Int(4)];
            int survivorIndex = expectedDepth ? 2 : 1;
            var expectedSurvivor = expectedDepth ? inner : outer;
            var expectedSurvivorBacking = expectedDepth ? innerBacking : outerBacking;
            var result = actual as TsArrayValue;
            var observed = result is null ? null : Snapshot(result.Value);
            int? resultLength = result?.Value.Count;
            bool? dense = result is null ? null
                : Enumerable.Range(0, result.Value.Count).All(i => !result.Value.IsHole(i));
            bool? freshWrapper = result is null ? null
                : !ReferenceEquals(receiver, result) && !ReferenceEquals(outer, result)
                    && !ReferenceEquals(inner, result);
            bool? freshBacking = result is null ? null
                : !ReferenceEquals(backing, result.Value) && !ReferenceEquals(outerBacking, result.Value)
                    && !ReferenceEquals(innerBacking, result.Value);
            var survivor = result is null ? null : PresentAt(result.Value, survivorIndex) as TsArrayValue;
            bool? survivorWrapper = result is null ? null : ReferenceEquals(expectedSurvivor, survivor);
            bool? survivorBacking = result is null ? null : ReferenceEquals(expectedSurvivorBacking, survivor?.Value);

            var stack = escaped?.StackTrace;
            bool stockRejection = !returned && actual is null && inputUnchanged
                && escaped?.GetType() == typeof(InvalidOperationException) && escaped.Message == StockError
                && stack is not null
                && stack.Contains("TypeSharp.VM.Interpreter.Builtins", StringComparison.Ordinal)
                && stack.Contains("TypeSharp.VM.Interpreter.Interpreter.Execute(", StringComparison.Ordinal);
            bool passed = returned && escaped is null && inputUnchanged
                && actual?.GetType() == typeof(TsArrayValue) && observed is not null && SameSlots(expected, observed)
                && resultLength == expected.Length && dense == true && freshWrapper == true && freshBacking == true
                && survivorWrapper == true && survivorBacking == true;

            output.WriteLine("C201_ARRAY_FLAT_BOOL " + JsonSerializer.Serialize(new
            {
                row, source = Source, route.LexerErrors, route.ParserErrors, route.BinderErrors,
                route.Parameters, route.IrOps, route.IrArgIndexes, route.IrTarget, route.IrArgc,
                route.CompiledHex, route.RestoredHex, route.CompiledStrings, route.RestoredStrings,
                route.SerializedLength, route.Offsets, route.RestoredArgIndexes, route.CallPoolIndex,
                route.RestoredTarget, route.RestoredArgc, hostArgc = args.Length, depthType = depthType.FullName,
                depthValue, before, after, rootHolePreserved, rootBackingPreserved, outerWrapperPreserved,
                outerBackingPreserved, innerWrapperPreserved, innerBackingPreserved, inputIntSlotsPreserved,
                inputUnchanged, returned, expectedResultType = typeof(TsArrayValue).FullName,
                resultType = actual?.GetType().FullName, expected, observed, resultLength, dense, freshWrapper,
                freshBacking, survivorIndex, survivorType = survivor?.GetType().FullName, survivorWrapper,
                survivorBacking, exceptionType = escaped?.GetType().FullName, exceptionMessage = escaped?.Message,
                exceptionStack = stack, stockRejection, passed
            }));
            if (!passed)
                mismatches.Add(row + (stockRejection ? ":stock-depth-rejection" : ":unexpected-observation"));
        }

        Assert.True(mismatches.Count == 0, "C201_BOOLEAN_DEPTH_MISMATCH " + string.Join(",", mismatches));
    }

    private static Route Compile()
    {
        var tokens = new Lexer(Source).Tokenize();
        int lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        int parserErrors = parser.Diagnostics.Count;
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(syntax);
        int binderErrors = binder.Diagnostics.All.Count;
        Require(lexerErrors == 0 && parserErrors == 0 && binderErrors == 0,
            $"diagnostics: lexer={lexerErrors}, parser={parserErrors}, binder={binderErrors}");

        var ir = new IRGenerator().Generate(bound);
        var main = ir.Functions.Single(function => function.Name == "main");
        var parameters = main.Parameters.Select(parameter => parameter.Name).ToArray();
        Require(parameters.SequenceEqual(new[] { "a", "depth" }), "IR main parameters");
        var instructions = main.Blocks.Single().Instructions.ToArray();
        Require(instructions.Select(op => op.Opcode).SequenceEqual(
            new[] { Opcode.LoadArg, Opcode.LoadArg, Opcode.Call, Opcode.Return }), "IR main opcodes");
        var irOps = instructions.Select(op => op.Opcode.ToString()).ToArray();
        int[] irArgIndexes = [instructions[0].Operand0, instructions[1].Operand0];
        var irTarget = instructions[2].OperandObject as string;
        int irArgc = instructions[2].Operand1;
        Require(irArgIndexes.SequenceEqual(new[] { 0, 1 }) && irTarget == Target && irArgc == 2,
            "IR main argument/call operands");

        var compiledModule = BytecodeCompiler.Compile(ir);
        var compiled = compiledModule.Functions.Single(function => function.Name == "main");
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiledModule);
        long serializedLength = stream.Length;
        stream.Position = 0;
        var restoredModule = BytecodeSerializer.Deserialize(stream);
        var restored = restoredModule.Functions.Single(function => function.Name == "main");
        Require(compiled.ParameterCount == 2 && restored.ParameterCount == 2, "bytecode main parameter counts");
        Require(compiled.Instructions.SequenceEqual(restored.Instructions)
            && compiled.StringConstants.SequenceEqual(restored.StringConstants), "compiled/restored bytecode equality");
        var code = restored.Instructions;
        int[] offsets = [0, 5, 10, 19];
        Require(code.Length == 20 && code[offsets[0]] == Opcodes.LoadArg && code[offsets[1]] == Opcodes.LoadArg
            && code[offsets[2]] == Opcodes.Call && code[offsets[3]] == Opcodes.Return, "restored main opcode layout");
        int ReadOperand(int offset) => BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(offset, 4));
        int[] restoredArgIndexes = [ReadOperand(offsets[0] + 1), ReadOperand(offsets[1] + 1)];
        int callPoolIndex = ReadOperand(offsets[2] + 1);
        int restoredArgc = ReadOperand(offsets[2] + 5);
        Require(callPoolIndex >= 0 && callPoolIndex < restored.StringConstants.Length, "restored call pool index");
        var restoredTarget = restored.StringConstants[callPoolIndex];
        Require(restoredArgIndexes.SequenceEqual(irArgIndexes) && restoredTarget == irTarget && restoredArgc == irArgc,
            "restored main argument/call operands");
        return new Route(restoredModule, lexerErrors, parserErrors, binderErrors, parameters, irOps, irArgIndexes,
            irTarget!, irArgc, Convert.ToHexString(compiled.Instructions), Convert.ToHexString(code),
            compiled.StringConstants, restored.StringConstants, serializedLength, offsets, restoredArgIndexes,
            callPoolIndex, restoredTarget, restoredArgc);
    }

    private static void Require(bool condition, string detail)
    {
        if (!condition)
            throw new InvalidOperationException("C201_FIXTURE_SETUP " + detail);
    }

    private sealed record Route(BytecodeModule Module, int LexerErrors, int ParserErrors, int BinderErrors,
        string[] Parameters, string[] IrOps, int[] IrArgIndexes, string IrTarget, int IrArgc, string CompiledHex,
        string RestoredHex, string[] CompiledStrings, string[] RestoredStrings, long SerializedLength, int[] Offsets,
        int[] RestoredArgIndexes, int CallPoolIndex, string RestoredTarget, int RestoredArgc);

    private sealed record Slot(string Kind, int? Int32 = null, Slot[]? Items = null);

    private static Slot Int(int value) => new(typeof(TsInt32Value).FullName!, value);
    private static Slot ArraySlot(params Slot[] slots) => new(typeof(TsArrayValue).FullName!, Items: slots);

    private static bool SameSlots(Slot[] expected, Slot[] observed) =>
        expected.Length == observed.Length && expected.Zip(observed).All(pair =>
            pair.First.Kind == pair.Second.Kind && pair.First.Int32 == pair.Second.Int32
            && (pair.First.Items is { } nested
                ? pair.Second.Items is { } other && SameSlots(nested, other)
                : pair.Second.Items is null));

    private static Slot[] Snapshot(TsArray array, int level = 0) =>
        Enumerable.Range(0, array.Count).Select(index => array.IsHole(index) ? new Slot("hole")
            : array.Get(index) switch
            {
                TsInt32Value integer => Int(integer.Value),
                TsArrayValue nested when level < 3 => ArraySlot(Snapshot(nested.Value, level + 1)),
                var value => new Slot(value.GetType().FullName!)
            }).ToArray();

    private static TsValue? PresentAt(TsArray array, int index) =>
        index < array.Count && !array.IsHole(index) ? array.Get(index) : null;

    private static TsArrayValue ArrayOf(params TsValue[] items)
    {
        var array = new TsArray();
        foreach (var item in items)
            array.Add(item);
        return new TsArrayValue(array);
    }
}
