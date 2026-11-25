using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFlatNullDepthTests(ITestOutputHelper output)
{
    [Fact]
    public void Flat_ExplicitNullDepthUsesZero()
    {
        var literal = Compile("literal-null", "function main(a: any[]): any[] { return a.flat(null); }");
        var omitted = Compile("omitted", "function main(a: any[]): any[] { return a.flat(); }");
        var supplied = Compile("supplied", "function main(a: any[], depth: any): any[] { return a.flat(depth); }");

        const string input = "[H,1,[2,[3]],4]";
        const string zero = "[1,[2,[3]],4]";
        const string one = "[1,2,[3],4]";
        const string two = "[1,2,3,4]";
        var mismatches = new List<string>();
        foreach (var (row, route, depth, declaredKind, declaredValue, expected, survivorIndex) in
            new (string, Route, TsValue?, string, string?, string, int?)[]
            {
                ("explicit-null", literal, null, nameof(TsNull), "null", zero, 1),
                ("omitted", omitted, null, "omitted", null, one, 2),
                ("explicit-undefined", supplied, TsValue.Void, nameof(TsVoid), "undefined", one, 2),
                ("numeric-zero", supplied, TsValue.FromInt32(0), nameof(TsInt32Value), "0", zero, 1),
                ("numeric-nan", supplied, TsValue.FromFloat64(double.NaN), nameof(TsFloat64Value), "NaN", zero, 1),
                ("numeric-two", supplied, TsValue.FromInt32(2), nameof(TsInt32Value), "2", two, null)
            })
        {
            var inner = ArrayOf(TsValue.FromInt32(3));
            var innerBacking = inner.Value;
            var outer = ArrayOf(TsValue.FromInt32(2), inner);
            var outerBacking = outer.Value;
            var backing = new TsArray();
            backing.SetHole(0);
            backing.Add(TsValue.FromInt32(1));
            backing.Add(outer);
            backing.Add(TsValue.FromInt32(4));
            var receiver = new TsArrayValue(backing);
            var before = Snapshot(backing);
            Assert.Equal(input, before);
            Assert.True(backing.IsHole(0));

            TsValue[] args = depth is null ? [receiver] : [receiver, depth];
            // Literal depth is observed in verified bytecode, not injected as a host argument.
            var observedDepth = route.DepthOpcode == Opcodes.LoadConstNull ? TsValue.Null
                : args.Length == 2 ? args[1] : null;
            var actualKind = observedDepth?.GetType().Name ?? "omitted";
            var actualValue = observedDepth switch
            {
                null => null,
                TsNull => "null",
                TsVoid => "undefined",
                TsInt32Value value => value.Value.ToString(CultureInfo.InvariantCulture),
                TsFloat64Value value when double.IsNaN(value.Value) => "NaN",
                _ => "unexpected"
            };
            Assert.Equal(declaredKind, actualKind);
            Assert.Equal(declaredValue, actualValue);

            var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
            TsValue? actual = null;
            Exception? escaped = null;
            try
            {
                actual = interpreter.Execute(route.Module, "main", args);
            }
            catch (Exception ex)
            {
                escaped = ex;
            }

            var after = Snapshot(receiver.Value);
            var inputHolePreserved = receiver.Value.Count > 0 && receiver.Value.IsHole(0);
            var inputReceiverBacking = ReferenceEquals(backing, receiver.Value);
            var actualOuter = NestedAt(receiver.Value, 2);
            var actualInner = actualOuter is null ? null : NestedAt(actualOuter.Value, 1);
            var inputOuterWrapper = ReferenceEquals(outer, actualOuter);
            var inputOuterBacking = ReferenceEquals(outerBacking, actualOuter?.Value);
            var inputInnerWrapper = ReferenceEquals(inner, actualInner);
            var inputInnerBacking = ReferenceEquals(innerBacking, actualInner?.Value);
            var result = actual as TsArrayValue;
            var observed = result is null ? null : Snapshot(result.Value);
            bool? dense = result is null ? null
                : Enumerable.Range(0, result.Value.Count).All(i => !result.Value.IsHole(i));
            bool? freshWrapper = result is null ? null : !ReferenceEquals(receiver, result);
            bool? freshBacking = result is null ? null : !ReferenceEquals(backing, result.Value);
            var expectedSurvivor = survivorIndex == 1 ? outer : survivorIndex == 2 ? inner : null;
            var expectedSurvivorBacking = survivorIndex == 1 ? outerBacking : survivorIndex == 2 ? innerBacking : null;
            var survivor = result is not null && survivorIndex is int index ? NestedAt(result.Value, index) : null;
            bool? survivorWrapper = result is null || expectedSurvivor is null ? null
                : ReferenceEquals(expectedSurvivor, survivor);
            bool? survivorBacking = result is null || expectedSurvivor is null ? null
                : ReferenceEquals(expectedSurvivorBacking, survivor?.Value);
            bool? noNested = result is null || expectedSurvivor is not null ? null
                : Enumerable.Range(0, result.Value.Count).All(i => NestedAt(result.Value, i) is null);

            var passed = escaped is null && after == before && inputHolePreserved && inputReceiverBacking
                && inputOuterWrapper && inputOuterBacking && inputInnerWrapper && inputInnerBacking
                && actual?.GetType() == typeof(TsArrayValue) && observed == expected
                && dense == true && freshWrapper == true && freshBacking == true
                && (expectedSurvivor is null ? noNested == true : survivorWrapper == true && survivorBacking == true);
            output.WriteLine("C195 Array.flat null depth " + JsonSerializer.Serialize(new
            {
                row, sourceForm = route.Form, source = route.Source, ops = route.Ops, bytecode = route.BytecodeHex,
                lexerErrors = route.LexerErrors, parserErrors = route.ParserErrors, binderErrors = route.BinderErrors,
                target = route.Target, argc = route.Argc, hostArgc = args.Length,
                declaredKind, declaredValue, actualKind, actualValue, before, after, inputHolePreserved,
                inputReceiverBacking, inputOuterWrapper, inputOuterBacking, inputInnerWrapper, inputInnerBacking,
                expectedType = nameof(TsArrayValue), observedType = actual?.GetType().Name, expected, observed,
                dense, freshWrapper, freshBacking, survivorIndex, survivorWrapper, survivorBacking, noNested,
                exceptionType = escaped?.GetType().FullName, exceptionMessage = escaped?.Message, passed
            }));
            if (!passed)
                mismatches.Add(row);
        }

        Assert.True(mismatches.Count == 0, "C195_SEMANTIC_MISMATCHES " + string.Join(",", mismatches));
    }

    private static Route Compile(string form, string source)
    {
        var tokens = new Lexer(source).Tokenize();
        int lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var binder = new TypeSharp.Semantics.Binder.Binder();
        var bound = binder.Bind(parser.Parse());
        int parserErrors = parser.Diagnostics.Count;
        int binderErrors = binder.Diagnostics.All.Count;
        Assert.Equal(0, lexerErrors);
        Assert.Equal(0, parserErrors);
        Assert.Equal(0, binderErrors);

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        Assert.Equal(form == "supplied" ? new[] { "a", "depth" } : new[] { "a" },
            main.Parameters.Select(p => p.Name));
        var instructions = Assert.Single(main.Blocks).Instructions.ToArray();
        int argc = form == "omitted" ? 1 : 2;
        Assert.Equal(argc + 2, instructions.Length);
        Assert.Equal(Opcode.LoadArg, instructions[0].Opcode);
        Assert.Equal(0, instructions[0].Operand0);
        if (form == "literal-null")
            Assert.Equal(Opcode.LoadConst_Null, instructions[1].Opcode);
        else if (form == "supplied")
        {
            Assert.Equal(Opcode.LoadArg, instructions[1].Opcode);
            Assert.Equal(1, instructions[1].Operand0);
        }

        var call = instructions[argc];
        Assert.Equal(Opcode.Call, call.Opcode);
        Assert.Equal("Array::flat", Assert.IsType<string>(call.OperandObject));
        Assert.Equal(argc, call.Operand1);
        Assert.Equal(Opcode.Return, instructions[argc + 1].Opcode);
        var ops = instructions.Select(instruction => instruction.Opcode.ToString()).ToArray();

        var module = BytecodeCompiler.Compile(ir);
        var compiled = Assert.Single(module.Functions, function => function.Name == "main");
        Assert.Equal(form == "supplied" ? 2 : 1, compiled.ParameterCount);
        var code = compiled.Instructions;
        Assert.Equal(form == "supplied" ? 20 : form == "literal-null" ? 16 : 15, code.Length);
        Assert.Equal(Opcodes.LoadArg, code[0]);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(1, 4)));
        int callOffset = 5;
        byte? depthOpcode = null;
        if (form == "literal-null")
        {
            depthOpcode = code[5];
            Assert.Equal(Opcodes.LoadConstNull, code[5]);
            callOffset += 1;
        }
        else if (form == "supplied")
        {
            depthOpcode = code[5];
            Assert.Equal(Opcodes.LoadArg, code[5]);
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(6, 4)));
            callOffset += 5;
        }

        Assert.Equal(Opcodes.Call, code[callOffset]);
        var target = compiled.StringConstants[BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(callOffset + 1, 4))];
        Assert.Equal("Array::flat", target);
        Assert.Equal(argc, BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(callOffset + 5, 4)));
        Assert.Equal(Opcodes.Return, code[callOffset + 9]);
        return new Route(form, source, module, ops, Convert.ToHexString(code), lexerErrors,
            parserErrors, binderErrors, target, argc, depthOpcode);
    }

    private sealed record Route(string Form, string Source, BytecodeModule Module, string[] Ops,
        string BytecodeHex, int LexerErrors, int ParserErrors, int BinderErrors, string Target, int Argc,
        byte? DepthOpcode);

    private static TsArrayValue ArrayOf(params TsValue[] values)
    {
        var array = new TsArray();
        foreach (var value in values)
            array.Add(value);
        return new TsArrayValue(array);
    }

    private static TsArrayValue? NestedAt(TsArray array, int index) =>
        index < array.Count && !array.IsHole(index) ? array.Get(index) as TsArrayValue : null;

    private static string Snapshot(TsArray array) => "[" + string.Join(",",
        Enumerable.Range(0, array.Count).Select(i => array.IsHole(i) ? "H" : array.Get(i) switch
        {
            TsArrayValue nested => Snapshot(nested.Value),
            TsInt32Value value => value.Value.ToString(CultureInfo.InvariantCulture),
            TsNull => "null",
            TsVoid => "U",
            var value => "type:" + value.GetType().Name
        })) + "]";
}
