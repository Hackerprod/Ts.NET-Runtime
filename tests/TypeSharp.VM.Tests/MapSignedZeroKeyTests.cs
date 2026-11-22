using System.Buffers.Binary;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class MapSignedZeroKeyTests(ITestOutputHelper output)
{
    private const string Source = """
        function make(): Map<any, any> { return new Map<any, any>(); }
        function put(m: Map<any, any>, k: any, v: any): Map<any, any> { return m.set(k, v); }
        function keys(m: Map<any, any>): any[] { return m.keys(); }
        function values(m: Map<any, any>): any[] { return m.values(); }
        """;

    [Fact]
    public void MapSet_CanonicalizesFloat64NegativeZeroKeyOnly() =>
        CheckNegativeZero(new TsFloat64Value(BitConverter.Int64BitsToDouble(long.MinValue)),
            TsValueType.Float64, "8000000000000000", "0000000000000000");

    [Fact]
    public void MapSet_CanonicalizesFloat32NegativeZeroKeyOnly() =>
        CheckNegativeZero(new TsFloat32Value(BitConverter.Int32BitsToSingle(int.MinValue)),
            TsValueType.Float32, "80000000", "00000000");

    [Fact]
    public void MapSet_PreservesOrdinaryKeysPayloadsAndZeroUpdateOrder()
    {
        var code = Compile();
        var p64 = new TsFloat64Value(0d);
        var n64 = new TsFloat64Value(BitConverter.Int64BitsToDouble(long.MinValue));
        var n32 = new TsFloat32Value(BitConverter.Int32BitsToSingle(int.MinValue));
        var finite = new TsFloat64Value(-2.5);
        var integerZero = new TsInt32Value(0);
        var map = Fresh(code);
        Put(code, map, p64, n64);
        var first = Read(code, map, "positive-zero", ["0000000000000000"], ["8000000000000000"]);
        Same(p64, first.Keys[0], TsValueType.Float64, "0000000000000000");
        Same(n64, first.Values[0], TsValueType.Float64, "8000000000000000");
        Put(code, map, finite, p64);
        var appended = Read(code, map, "finite-appended", ["0000000000000000", "C004000000000000"],
            ["8000000000000000", "0000000000000000"]);
        Same(p64, appended.Keys[0], TsValueType.Float64, "0000000000000000");
        Same(finite, appended.Keys[1], TsValueType.Float64, "C004000000000000");
        Same(n64, appended.Values[0], TsValueType.Float64, "8000000000000000");
        Same(p64, appended.Values[1], TsValueType.Float64, "0000000000000000");
        Put(code, map, n64, n32);
        var updated = Read(code, map, "negative-zero-update", ["0000000000000000", "C004000000000000"],
            ["80000000", "0000000000000000"]);
        Same(p64, updated.Keys[0], TsValueType.Float64, "0000000000000000");
        Same(finite, updated.Keys[1], TsValueType.Float64, "C004000000000000");
        Same(n32, updated.Values[0], TsValueType.Float32, "80000000");
        Same(p64, updated.Values[1], TsValueType.Float64, "0000000000000000");
        Check(n64, TsValueType.Float64, "8000000000000000");

        var intMap = Fresh(code);
        Assert.NotSame(map, intMap);
        Put(code, intMap, integerZero, n32);
        var integer = Read(code, intMap, "int32-zero", ["00000000"], ["80000000"]);
        Same(integerZero, integer.Keys[0], TsValueType.Int32, "00000000");
        Same(n32, integer.Values[0], TsValueType.Float32, "80000000");
        Log("control-complete", new { completed = true, exception = (string?)null, updateInput = Describe(n64) });
    }

    private readonly TypeSharp.VM.Interpreter.Interpreter vm = new();

    private void CheckNegativeZero(TsValue input, TsValueType tag, string negativeBits, string positiveBits)
    {
        var code = Compile();
        var map = Fresh(code);
        Check(input, tag, negativeBits);
        var before = Describe(input);
        Put(code, map, input, input);
        var observed = Read(code, map, "negative-zero-insert", [positiveBits], [negativeBits]);
        var key = observed.Keys[0];
        var payload = observed.Values[0];
        if (tag == TsValueType.Float64)
            Assert.IsType<TsFloat64Value>(key);
        else
            Assert.IsType<TsFloat32Value>(key);
        Assert.Equal(tag, key.ValueType);
        Same(input, payload, tag, negativeBits);
        Check(input, tag, negativeBits);
        var actualKeyBits = Bits(key);
        Log("focal-complete", new
        {
            inputBefore = before, inputAfter = Describe(input), key = Describe(key), payload = Describe(payload),
            payloadSameInput = ReferenceEquals(input, payload), expectedKeyBits = positiveBits,
            completed = true, exception = (string?)null
        });
        Assert.True(actualKeyBits == positiveBits,
            $"stored {tag} key bits expected {positiveBits}, actual {actualKeyBits}");
    }

    private TsMapValue Fresh(BytecodeModule code)
    {
        var map = Assert.IsType<TsMapValue>(Invoke(code, "make"));
        Assert.Equal(TsValueType.Map, map.ValueType);
        Log("fresh", new { wrapper = map.GetType().Name, tag = map.ValueType.ToString() });
        Read(code, map, "fresh", [], []);
        return map;
    }

    private void Put(BytecodeModule code, TsMapValue map, TsValue key, TsValue payload)
    {
        var keyBefore = Describe(key);
        var payloadBefore = Describe(payload);
        TsValue[] args = [map, key, payload];
        var result = Invoke(code, "put", args);
        Assert.Same(map, args[0]);
        Assert.Same(key, args[1]);
        Assert.Same(payload, args[2]);
        Assert.Same(map, result);
        Log("put", new
        {
            keyBefore, keyAfter = Describe(key), payloadBefore, payloadAfter = Describe(payload),
            resultWrapper = result.GetType().Name, resultTag = result.ValueType.ToString(),
            sameMap = ReferenceEquals(map, result), keyAndPayloadSame = ReferenceEquals(key, payload)
        });
    }

    private (TsValue[] Keys, TsValue[] Values) Read(BytecodeModule code, TsMapValue map, string step,
        string[] expectedKeys, string[] expectedValues)
    {
        var keys = Assert.IsType<TsArrayValue>(Invoke(code, "keys", map));
        var values = Assert.IsType<TsArrayValue>(Invoke(code, "values", map));
        Assert.Equal(TsValueType.Array, keys.ValueType);
        Assert.Equal(TsValueType.Array, values.ValueType);
        var keyItems = Enumerable.Range(0, keys.Value.Count).Select(keys.Value.Get).ToArray();
        var valueItems = Enumerable.Range(0, values.Value.Count).Select(values.Value.Get).ToArray();
        Log("arrays", new
        {
            step, keyWrapper = keys.GetType().Name, valueWrapper = values.GetType().Name,
            keyTag = keys.ValueType.ToString(), valueTag = values.ValueType.ToString(),
            keyCount = keyItems.Length, valueCount = valueItems.Length,
            expectedKeyCount = expectedKeys.Length, expectedValueCount = expectedValues.Length,
            keys = keyItems.Select(Describe).ToArray(), values = valueItems.Select(Describe).ToArray(),
            expectedKeys, expectedValues
        });
        Assert.Equal(expectedKeys.Length, keyItems.Length);
        Assert.Equal(expectedValues.Length, valueItems.Length);
        return (keyItems, valueItems);
    }

    private TsValue Invoke(BytecodeModule code, string function, params TsValue[] args)
    {
        try
        {
            var result = vm.Execute(code, function, args);
            Log("execute", new { function, completed = true, exception = (string?)null });
            return Assert.IsAssignableFrom<TsValue>(result);
        }
        catch (Exception error)
        {
            Log("execute", new { function, completed = false, exception = error.ToString() });
            throw;
        }
    }

    private BytecodeModule Compile()
    {
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(Source).Tokenize());
        var tree = parser.Parse();
        var binder = new Binder();
        var bound = binder.Bind(tree);
        Log("source", new
        {
            source = Source, parserCount = parser.Diagnostics.Count(), binderCount = binder.Diagnostics.All.Count(),
            parserDiagnostics = parser.Diagnostics.Select(d => d.ToString()).ToArray(),
            binderDiagnostics = binder.Diagnostics.All.Select(d => d.ToString()).ToArray()
        });
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);

        (string Name, string Target, int Argc)[] routes =
            [("make", "Map", 0), ("put", "Map::set", 3), ("keys", "Map::keys", 1), ("values", "Map::values", 1)];
        Assert.Equal(routes.Length, bound.Members.Count());
        var ir = new IRGenerator().Generate(bound);
        Assert.Equal(routes.Length, ir.Functions.Count());
        var code = BytecodeCompiler.Compile(ir);
        Assert.Equal(routes.Length, code.Functions.Length);
        foreach (var (name, target, argc) in routes)
        {
            var fn = Assert.Single(bound.Members.OfType<BoundFunctionDeclaration>(), f => f.Symbol.Name == name);
            var body = Assert.IsType<BoundBlockStatement>(fn.Body);
            var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
            if (name == "make")
            {
                var ctor = Assert.IsType<BoundNewExpression>(ret.Value);
                Assert.IsType<TsMapType>(ctor.Type);
                Assert.IsType<TsMapType>(fn.Symbol.Type);
                Log("bound", new { name, constructor = ctor.GetType().Name, returnType = ctor.Type.GetType().Name });
            }
            else
            {
                var call = Assert.IsType<BoundCallExpression>(ret.Value);
                var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
                var receiver = Assert.IsType<TsMapType>(member.Object.Type);
                var method = Assert.IsType<MethodSymbol>(member.Member);
                Assert.Equal("Map", method.DeclaringClassName);
                Assert.Equal(target[5..], method.Name);
                Assert.Equal(argc - 1, call.Arguments.Count);
                Assert.Equal(argc - 1, method.Parameters.Count);
                var returnType = name == "put" ? typeof(TsMapType) : typeof(TsArrayType);
                Assert.Equal(returnType, call.Type.GetType());
                Assert.Equal(returnType, method.Type.GetType());
                Assert.Equal(returnType, fn.Symbol.Type.GetType());
                Log("bound", new
                {
                    name, receiver = receiver.GetType().Name, owner = method.DeclaringClassName,
                    member = method.Name, returnType = call.Type.GetType().Name
                });
            }

            var irFn = Assert.Single(ir.Functions, f => f.Name == name);
            var ops = irFn.Blocks.SelectMany(block => block.Instructions).ToArray();
            Assert.DoesNotContain(ops, op => op.Opcode is Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
            var dispatch = Assert.Single(ops, op => op.Opcode is Opcode.NewObject or Opcode.Call);
            Assert.Equal(name == "make" ? Opcode.NewObject : Opcode.Call, dispatch.Opcode);
            Assert.Equal(target, Assert.IsType<string>(dispatch.OperandObject));
            Assert.Equal(argc, dispatch.Operand1);
            var dispatchBlock = Assert.Single(irFn.Blocks, block => block.Instructions.Contains(dispatch));
            var irArgs = dispatchBlock.Instructions.Take(dispatchBlock.Instructions.IndexOf(dispatch)).TakeLast(argc)
                .Select(op => { Assert.Equal(Opcode.LoadArg, op.Opcode); return op.Operand0; }).ToArray();
            Assert.Equal(Enumerable.Range(0, argc), irArgs);
            Log("ir", new { name, target, argc, opcode = dispatch.Opcode.ToString(), args = irArgs });

            var encoded = Assert.Single(code.Functions, f => f.Name == name);
            var decoded = Decode(encoded.Instructions).ToArray();
            Assert.DoesNotContain(decoded, op => op.Opcode is Opcodes.CallVirt or Opcodes.CallDynamic or Opcodes.CallDynamicArray);
            var native = Assert.Single(decoded, op => op.Opcode is Opcodes.NewObject or Opcodes.Call);
            Assert.Equal(name == "make" ? Opcodes.NewObject : Opcodes.Call, native.Opcode);
            Assert.InRange(native.A, 0, encoded.StringConstants.Length - 1);
            Assert.Equal(target, encoded.StringConstants[native.A]);
            Assert.Equal(argc, native.B);
            var nativeArgs = decoded.TakeWhile(op => op.Offset < native.Offset).TakeLast(argc)
                .Select(op => { Assert.Equal(Opcodes.LoadArg, op.Opcode); return op.A; }).ToArray();
            Assert.Equal(Enumerable.Range(0, argc), nativeArgs);
            Log("bytecode", new
            {
                name, bytes = Convert.ToHexString(encoded.Instructions), strings = encoded.StringConstants,
                instructions = decoded, target = encoded.StringConstants[native.A], targetIndex = native.A,
                argc = native.B, args = nativeArgs
            });
        }
        return code;
    }

    private sealed record Instruction(int Offset, byte Opcode, int A, int B);
    private static List<Instruction> Decode(byte[] bytes)
    {
        var instructions = new List<Instruction>();
        for (int offset = 0; offset < bytes.Length;)
        {
            byte opcode = bytes[offset];
            int next = offset + OpcodeFormats.InstructionSize(opcode);
            Assert.InRange(next, offset + 1, bytes.Length);
            int a = opcode is Opcodes.LoadArg or Opcodes.Call or Opcodes.NewObject
                ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)) : 0;
            int b = opcode is Opcodes.Call or Opcodes.NewObject
                ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4)) : 0;
            instructions.Add(new Instruction(offset, opcode, a, b));
            offset = next;
        }
        return instructions;
    }

    private static string Bits(TsValue value) => value switch
    {
        TsFloat64Value f => BitConverter.DoubleToInt64Bits(f.Value).ToString("X16"),
        TsFloat32Value f => BitConverter.SingleToInt32Bits(f.Value).ToString("X8"),
        TsInt32Value i => i.Value.ToString("X8"),
        _ => throw new InvalidOperationException($"Unexpected wrapper: {value.GetType().Name}")
    };

    private static object Describe(TsValue value) =>
        new { wrapper = value.GetType().Name, tag = value.ValueType.ToString(), bits = Bits(value) };

    private static void Check(TsValue value, TsValueType tag, string bits)
    {
        Assert.Equal(tag, value.ValueType);
        Assert.Equal(bits, Bits(value));
    }

    private static void Same(TsValue expected, TsValue actual, TsValueType tag, string bits)
    {
        Assert.Same(expected, actual);
        Check(actual, tag, bits);
    }

    private void Log(string phase, object data) => output.WriteLine($"C137 {phase} {JsonSerializer.Serialize(data)}");
}
