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

public sealed class SetSignedZeroValueTests(ITestOutputHelper output)
{
    private const string Source = """
        function addPath(value: any): any[] {
            const set = new Set<any>();
            set.add(value);
            return [...set];
        }
        function constructorPath(value: any): any[] {
            const set = new Set<any>([value]);
            return [...set];
        }
        function addDuplicate(positive: any, finite: any, negative: any): any[] {
            const set = new Set<any>();
            set.add(positive);
            set.add(finite);
            set.add(negative);
            return [set.has(negative), [...set]];
        }
        function constructorDuplicate(positive: any, finite: any, negative: any): any[] {
            const set = new Set<any>([positive, finite, negative]);
            return [set.has(negative), [...set]];
        }
        """;

    [Fact]
    public void Add_CanonicalizesFloat64NegativeZero() =>
        CheckSign("addPath", Negative64(), TsValueType.Float64, "8000000000000000", "0000000000000000");

    [Fact]
    public void Add_CanonicalizesFloat32NegativeZero() =>
        CheckSign("addPath", Negative32(), TsValueType.Float32, "80000000", "00000000");

    [Fact]
    public void Constructor_CanonicalizesFloat64NegativeZero() =>
        CheckSign("constructorPath", Negative64(), TsValueType.Float64, "8000000000000000", "0000000000000000");

    [Fact]
    public void Constructor_CanonicalizesFloat32NegativeZero() =>
        CheckSign("constructorPath", Negative32(), TsValueType.Float32, "80000000", "00000000");

    [Fact]
    public void Add_PreservesOrdinaryValues() => CheckOrdinary("addPath");

    [Fact]
    public void Constructor_PreservesOrdinaryValues() => CheckOrdinary("constructorPath");

    [Fact]
    public void Add_DuplicateZeroPreservesMembershipAndOrder() => CheckDuplicate("addDuplicate");

    [Fact]
    public void Constructor_DuplicateZeroPreservesMembershipAndOrder() => CheckDuplicate("constructorDuplicate");

    private static TsFloat64Value Negative64() => new(BitConverter.Int64BitsToDouble(long.MinValue));
    private static TsFloat32Value Negative32() => new(BitConverter.Int32BitsToSingle(int.MinValue));

    private void CheckSign(string route, TsValue input, TsValueType tag, string negative, string positive)
    {
        var code = Compile(route, 1, false);
        Assert.Equal(tag, input.ValueType);
        Assert.Equal(negative, Bits(input));
        var before = Describe(input);
        TsValue[] args = [input];
        var spread = Dense(Execute(code, route, args), 1);
        var stored = spread.Get(0);
        Assert.Equal(input.GetType(), stored.GetType());
        Assert.Equal(tag, stored.ValueType);
        Assert.Same(input, args[0]);
        Assert.Equal(tag, input.ValueType);
        Assert.Equal(negative, Bits(input));
        var actual = Bits(stored);
        Log("focal-complete", new
        {
            route, completed = true, count = spread.Count, present = !spread.IsHole(0), inputBefore = before,
            inputAfter = Describe(args[0]), inputSame = ReferenceEquals(input, args[0]), stored = Describe(stored),
            expectedTag = tag.ToString(), expectedBits = positive, actualBits = actual
        });
        Assert.True(actual == positive, $"C142_SET_STORED_ZERO_SIGN {route} {tag} expected {positive}, actual {actual}");
    }

    private void CheckOrdinary(string route)
    {
        var code = Compile(route, 1, false);
        var payload = new TsInt32Value(17);
        var obj = new TsObject("Ordinary");
        obj.SetField("payload", payload);
        (TsValue Input, TsValueType Tag, string? Bits)[] cases =
        [
            (new TsFloat32Value(0f), TsValueType.Float32, "00000000"),
            (new TsFloat64Value(0d), TsValueType.Float64, "0000000000000000"),
            (new TsFloat32Value(-2.5f), TsValueType.Float32, "C0200000"),
            (new TsFloat64Value(-2.5d), TsValueType.Float64, "C004000000000000"),
            (new TsInt32Value(0), TsValueType.Int32, "00000000"),
            (new TsObjectValue(obj), TsValueType.Object, null)
        ];
        foreach (var (input, tag, bits) in cases)
        {
            Assert.Equal(tag, input.ValueType);
            Assert.Equal(bits, Bits(input));
            var before = Describe(input);
            TsValue[] args = [input];
            var spread = Dense(Execute(code, route, args), 1);
            var stored = spread.Get(0);
            Same(input, args[0], tag, bits);
            Same(input, stored, tag, bits);
            if (tag == TsValueType.Object)
            {
                Assert.Same(obj, Assert.IsType<TsObjectValue>(stored).Value);
                Assert.Same(payload, obj.GetField("payload"));
                Assert.Equal(17, payload.Value);
            }
            Log("ordinary-complete", new
            {
                route, completed = true, count = spread.Count, present = !spread.IsHole(0), inputBefore = before,
                inputAfter = Describe(args[0]), stored = Describe(stored), sameWrapper = ReferenceEquals(input, stored),
                objectIdentity = tag == TsValueType.Object ? ReferenceEquals(obj, stored.RawValue) : (bool?)null,
                payloadIdentity = tag == TsValueType.Object ? ReferenceEquals(payload, obj.GetField("payload")) : (bool?)null
            });
        }
    }

    private void CheckDuplicate(string route)
    {
        var code = Compile(route, 3, true);
        var positive = new TsFloat64Value(0d);
        var finite = new TsFloat64Value(-2.5d);
        var negative = Negative32();
        TsValue[] args = [positive, finite, negative];
        var before = args.Select(Describe).ToArray();
        var result = Dense(Execute(code, route, args), 2);
        var membership = Assert.IsType<TsBoolValue>(result.Get(0));
        Assert.Equal(TsValueType.Bool, membership.ValueType);
        Assert.True(membership.Value);
        var spread = Dense(result.Get(1), 2);
        Same(positive, spread.Get(0), TsValueType.Float64, "0000000000000000");
        Same(finite, spread.Get(1), TsValueType.Float64, "C004000000000000");
        Same(positive, args[0], TsValueType.Float64, "0000000000000000");
        Same(finite, args[1], TsValueType.Float64, "C004000000000000");
        Same(negative, args[2], TsValueType.Float32, "80000000");
        Log("duplicate-complete", new
        {
            route, completed = true, has = membership.Value, count = spread.Count,
            present = new[] { !spread.IsHole(0), !spread.IsHole(1) },
            inputBefore = before, inputAfter = args.Select(Describe).ToArray(),
            stored = new[] { Describe(spread.Get(0)), Describe(spread.Get(1)) },
            sameWrappers = new[] { ReferenceEquals(positive, spread.Get(0)), ReferenceEquals(finite, spread.Get(1)) }
        });
    }

    private TsValue Execute(BytecodeModule code, string route, TsValue[] args)
    {
        var vm = new TypeSharp.VM.Interpreter.Interpreter();
        var value = vm.Execute(code, route, args);
        Log("execute", new { route, completed = true, tag = value?.ValueType.ToString() });
        return Assert.IsAssignableFrom<TsValue>(value);
    }

    private BytecodeModule Compile(string route, int inputCount, bool duplicate)
    {
        var constructor = route.StartsWith("constructor", StringComparison.Ordinal);
        int newArgc = constructor ? 1 : 0;
        int addCount = constructor ? 0 : inputCount;
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(Source).Tokenize());
        var syntax = parser.Parse();
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Log("diagnostics", new
        {
            route, parser = parser.Diagnostics.Select(d => d.ToString()).ToArray(),
            binder = binder.Diagnostics.All.Select(d => d.ToString()).ToArray()
        });
        Assert.Empty(parser.Diagnostics);
        Assert.Empty(binder.Diagnostics.All);
        var fn = Assert.Single(bound.Members.OfType<BoundFunctionDeclaration>(), f => f.Symbol.Name == route);
        var body = Assert.IsType<BoundBlockStatement>(fn.Body);
        Assert.Equal(addCount + 2, body.Statements.Count);
        var declaration = Assert.IsType<BoundVariableDeclaration>(body.Statements[0]);
        var set = Assert.IsType<BoundNewExpression>(declaration.Initializer);
        Assert.IsType<TsSetType>(declaration.Symbol.Type);
        Assert.IsType<TsSetType>(set.ConstructedType);
        Assert.Equal(newArgc, set.Arguments.Count);
        if (constructor)
        {
            var inputArray = Assert.IsType<BoundArrayLiteralExpression>(Assert.Single(set.Arguments));
            Assert.IsType<TsArrayType>(inputArray.Type);
            Assert.Equal(inputCount, inputArray.Elements.Count);
            Assert.All(inputArray.Elements, input => Assert.IsType<BoundVariableExpression>(input));
        }
        foreach (var statement in body.Statements.Skip(1).Take(addCount))
            SetCall(Assert.IsType<BoundExpressionStatement>(statement).Expression, "add");
        var result = Assert.IsType<BoundArrayLiteralExpression>(Assert.IsType<BoundReturnStatement>(body.Statements[^1]).Value);
        if (duplicate)
        {
            Assert.Equal(2, result.Elements.Count);
            SetCall(result.Elements[0], "has");
            result = Assert.IsType<BoundArrayLiteralExpression>(result.Elements[1]);
        }
        Assert.IsType<TsSetType>(Assert.IsType<BoundSpreadExpression>(Assert.Single(result.Elements)).Expression.Type);
        Log("bound", new { route, constructed = set.ConstructedType.GetType().Name, newArgc, addCount, duplicate });

        var expected = new List<(string Target, int Arity)> { ("Set", newArgc) };
        expected.AddRange(Enumerable.Repeat(("Set::add", 2), addCount));
        if (duplicate)
            expected.Add(("Set::has", 2));
        var ir = new IRGenerator().Generate(bound);
        var ops = Assert.Single(ir.Functions, f => f.Name == route).Blocks.SelectMany(b => b.Instructions).ToArray();
        Assert.DoesNotContain(ops, op => op.Opcode is Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        Assert.Single(ops, op => op.Opcode == Opcode.ArrayAppendSpread);
        var dispatch = ops.Where(op => op.Opcode is Opcode.NewObject or Opcode.Call).ToArray();
        Assert.Equal(expected.Count, dispatch.Length);
        for (int i = 0; i < dispatch.Length; i++)
        {
            Assert.Equal(i == 0 ? Opcode.NewObject : Opcode.Call, dispatch[i].Opcode);
            Assert.Equal(expected[i], (Assert.IsType<string>(dispatch[i].OperandObject), dispatch[i].Operand1));
        }
        if (constructor)
        {
            Assert.Equal(Opcode.NewArray, ops[Array.IndexOf(ops, dispatch[0]) - 1].Opcode);
            Assert.Equal(inputCount, ops[Array.IndexOf(ops, dispatch[0]) - 1].Operand0);
        }
        Assert.Equal(constructor ? 1 : 0, ops.Count(op => op.Opcode == Opcode.NewArray && op.Operand0 == inputCount));
        Log("ir", new
        {
            route, instructions = ops.Select(o => new { op = o.Opcode.ToString(), a = o.Operand0, b = o.Operand1, target = o.OperandObject as string })
        });

        var code = BytecodeCompiler.Compile(ir);
        var encoded = Assert.Single(code.Functions, f => f.Name == route);
        var native = Decode(encoded.Instructions).ToArray();
        Assert.DoesNotContain(native, op => op.Opcode is Opcodes.CallVirt or Opcodes.CallDynamic or Opcodes.CallDynamicArray);
        Assert.Single(native, op => op.Opcode == Opcodes.ArrayAppendSpread);
        var calls = native.Where(op => op.Opcode is Opcodes.NewObject or Opcodes.Call).ToArray();
        Assert.Equal(expected.Count, calls.Length);
        for (int i = 0; i < calls.Length; i++)
        {
            Assert.Equal(i == 0 ? Opcodes.NewObject : Opcodes.Call, calls[i].Opcode);
            Assert.InRange(calls[i].A, 0, encoded.StringConstants.Length - 1);
            Assert.Equal(expected[i], (encoded.StringConstants[calls[i].A], calls[i].B));
        }
        if (constructor)
        {
            var array = native[Array.IndexOf(native, calls[0]) - 1];
            Assert.Equal(Opcodes.NewArray, array.Opcode);
            Assert.Equal(inputCount, array.A);
        }
        Log("bytecode", new { route, bytes = Convert.ToHexString(encoded.Instructions), strings = encoded.StringConstants, instructions = native });
        return code;
    }

    private void SetCall(BoundNode expression, string name)
    {
        var call = Assert.IsType<BoundCallExpression>(expression);
        var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        var receiver = Assert.IsType<TsSetType>(member.Object.Type);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal("Set", method.DeclaringClassName);
        Assert.Equal(name, method.Name);
        Assert.Single(call.Arguments);
        Assert.Single(method.Parameters);
        Log("bound-member", new { receiver = receiver.GetType().Name, owner = method.DeclaringClassName, member = method.Name });
    }

    private sealed record Instruction(int Offset, byte Opcode, int A, int B);

    private static IEnumerable<Instruction> Decode(byte[] bytes)
    {
        for (int offset = 0; offset < bytes.Length;)
        {
            var op = bytes[offset];
            var next = offset + OpcodeFormats.InstructionSize(op);
            Assert.InRange(next, offset + 1, bytes.Length);
            var a = op is Opcodes.NewArray or Opcodes.NewObject or Opcodes.Call
                ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)) : 0;
            var b = op is Opcodes.NewObject or Opcodes.Call
                ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4)) : 0;
            yield return new Instruction(offset, op, a, b);
            offset = next;
        }
    }

    private static TsArray Dense(TsValue result, int count)
    {
        var array = Assert.IsType<TsArrayValue>(result);
        Assert.Equal(TsValueType.Array, array.ValueType);
        Assert.Equal(count, array.Value.Count);
        for (int i = 0; i < count; i++)
            Assert.False(array.Value.IsHole(i));
        return array.Value;
    }

    private static string? Bits(TsValue value) => value switch
    {
        TsFloat64Value f => BitConverter.DoubleToInt64Bits(f.Value).ToString("X16"),
        TsFloat32Value f => BitConverter.SingleToInt32Bits(f.Value).ToString("X8"),
        TsInt32Value i => i.Value.ToString("X8"),
        TsObjectValue => null,
        _ => throw new InvalidOperationException($"Unexpected wrapper: {value.GetType().Name}")
    };

    private static object Describe(TsValue value) =>
        new { wrapper = value.GetType().Name, tag = value.ValueType.ToString(), bits = Bits(value) };

    private static void Same(TsValue expected, TsValue actual, TsValueType tag, string? bits)
    {
        Assert.Same(expected, actual);
        Assert.Equal(tag, actual.ValueType);
        Assert.Equal(bits, Bits(actual));
    }

    private void Log(string phase, object data) => output.WriteLine($"C142 {phase} {JsonSerializer.Serialize(data)}");
}
