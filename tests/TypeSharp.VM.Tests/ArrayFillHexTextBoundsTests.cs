using System.Buffers.Binary;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class ArrayFillHexTextBoundsTests(ITestOutputHelper output)
{
    private const string Target = "Array::fill";
    private const string Prefix = "C240_ARRAY_FILL_HEX_TEXT_BOUNDS";

    [Fact]
    public void Fill_UsesUnsignedHexTextBounds()
    {
        var both = CompileRoute("both", "c240-fill-both.ts",
            "function main(a: any[], v: any, s: any, e: any): any[] { return a.fill(v, s, e); }",
            ["a", "v", "s", "e"], "120000000012010000001202000000120300000073000000000400000075");
        var start = CompileRoute("start", "c240-fill-start.ts",
            "function main(a: any[], v: any, s: any): any[] { return a.fill(v, s); }",
            ["a", "v", "s"], "12000000001201000000120200000073000000000300000075");

        (string Id, Route Route, bool Sparse, TsValue[] Bounds, string Expected)[] rows =
        [
            ("both-hex", both, false, [TsValue.FromString("0x1"), TsValue.FromString("0X3")], "1994"),
            ("hex-start-only-path", both, false, [TsValue.FromString("0x1"), TsValue.FromInt32(3)], "1994"),
            ("hex-end-only-path", both, false, [TsValue.FromInt32(1), TsValue.FromString("0X3")], "1994"),
            ("hex-sparse", both, true, [TsValue.FromString("0x1"), TsValue.FromString("0X3")], "h994"),
            ("hex-trim-and-clamp", both, false, [TsValue.FromString("\u00A00X2\u2028"), TsValue.FromString("0x10")], "1299"),
            ("numeric", both, false, [TsValue.FromInt32(1), TsValue.FromInt32(3)], "1994"),
            ("decimal", both, false, [TsValue.FromString("1"), TsValue.FromString("3")], "1994"),
            ("omitted-end", start, false, [TsValue.FromInt32(2)], "1299"),
            ("explicit-void-end", both, false, [TsValue.FromInt32(2), TsValue.Void], "1299"),
            ("explicit-null-end", both, false, [TsValue.FromInt32(1), TsValue.Null], "1234"),
            ("nan-end", both, false, [TsValue.FromInt32(1), TsValue.FromFloat64(double.NaN)], "1234"),
            ("negative-fractions", both, false, [TsValue.FromFloat64(-3.8), TsValue.FromFloat64(-1.2)], "1994"),
            ("signed-and-malformed", both, false, [TsValue.FromString("-0x1"), TsValue.FromString("0x3g")], "1234")
        ];
        var observed = rows.Select(row => Measure(row.Id, row.Route, row.Sparse, row.Bounds)).ToArray();
        const int witnessCount = 5;
        for (int i = witnessCount; i < rows.Length; i++)
            Assert.True(string.Equals(rows[i].Expected, observed[i], StringComparison.Ordinal),
                $"Control {rows[i].Id}: expected {rows[i].Expected}, observed {observed[i]}");

        var expectedWitness = rows.Take(witnessCount).Select(row => row.Expected).ToArray();
        var observedWitness = observed.Take(witnessCount).ToArray();
        Assert.True(expectedWitness.SequenceEqual(observedWitness, StringComparer.Ordinal),
            $"{Prefix}_RESULT expected {JsonSerializer.Serialize(expectedWitness)}, observed {JsonSerializer.Serialize(observedWitness)}");
    }

    private Route CompileRoute(string route, string fileName, string source, string[] names, string expectedHex)
    {
        var tokens = new Lexer(source, fileName).Tokenize();
        int lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        Assert.Empty(parser.Diagnostics);
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        Assert.Empty(binder.Diagnostics.All);

        var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", function.Symbol.Name);
        CheckMutableArray(function.Symbol.Type);
        var parameters = function.Symbol.Parameters;
        Assert.Equal(names, parameters.Select(parameter => parameter.Name));
        CheckMutableArray(parameters[0].Type);
        for (int i = 1; i < names.Length; i++)
            Assert.Same(TsType.Any, parameters[i].Type);

        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        CheckMutableArray(call.Type);
        var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        Assert.False(member.IsNullConditional);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal("Array", method.DeclaringClassName);
        Assert.Equal("fill", method.Name);
        Assert.Equal(Target, $"{method.DeclaringClassName}::{method.RuntimeName ?? method.Name}");
        CheckMutableArray(method.Type);
        CheckParameterUse(member.Object, parameters[0]);
        Assert.Equal(names.Length - 1, call.Arguments.Count);
        for (int i = 1; i < names.Length; i++)
            CheckParameterUse(call.Arguments[i - 1], parameters[i]);

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions);
        Assert.Equal(function.Symbol.Name, irMain.Name);
        CheckMutableArray(irMain.ReturnType);
        Assert.Equal(names, irMain.Parameters.Select(parameter => parameter.Name));
        CheckMutableArray(irMain.Parameters[0].Type);
        for (int i = 1; i < names.Length; i++)
            Assert.Same(TsType.Any, irMain.Parameters[i].Type);
        var instructions = Assert.Single(irMain.Blocks).Instructions;
        Assert.Equal(names.Length + 2, instructions.Count);
        for (int i = 0; i < names.Length; i++)
            CheckInstruction(instructions[i], Opcode.LoadArg, i);
        CheckInstruction(instructions[^2], Opcode.Call, argc: names.Length, target: Target);
        CheckInstruction(instructions[^1], Opcode.Return);

        var compiled = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(compiled);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        Assert.Equal(stream.Length, stream.Position);
        BytecodeVerifier.Verify(restored);

        var compiledMain = Assert.Single(compiled.Functions);
        var restoredMain = Assert.Single(restored.Functions);
        Assert.Equal(irMain.Name, compiledMain.Name);
        Assert.Equal(irMain.Name, restoredMain.Name);
        Assert.Equal(names.Length, compiledMain.ParameterCount);
        Assert.Equal(names.Length, restoredMain.ParameterCount);
        var compiledHex = Convert.ToHexString(compiledMain.Instructions);
        var restoredHex = Convert.ToHexString(restoredMain.Instructions);
        Assert.Equal(expectedHex, compiledHex);
        Assert.Equal(expectedHex, restoredHex);
        Assert.Equal(Target, Assert.Single(compiledMain.StringConstants));
        Assert.Equal(Target, Assert.Single(restoredMain.StringConstants));

        var bytes = restoredMain.Instructions;
        int offset = 0;
        for (int i = 0; i < names.Length; i++)
        {
            Assert.Equal(Opcodes.LoadArg, bytes[offset++]);
            Assert.Equal(i, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)));
            offset += 4;
        }
        Assert.Equal(Opcodes.Call, bytes[offset++]);
        int targetIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        Assert.Equal(0, targetIndex);
        string target = restoredMain.StringConstants[targetIndex];
        Assert.Equal(Target, target);
        int argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
        Assert.Equal(names.Length, argc);
        offset += 8;
        Assert.Equal(Opcodes.Return, bytes[offset++]);
        Assert.Equal(bytes.Length, offset);

        Write(new
        {
            route,
            lexerErrors,
            parserDiagnostics = parser.Diagnostics.Count,
            binderDiagnostics = binder.Diagnostics.All.Count(),
            main = restoredMain.Name,
            parameterCount = restoredMain.ParameterCount,
            target,
            argc,
            compiledHex,
            restoredHex
        });
        return new Route(route, restored, restoredMain.Name, restoredMain.ParameterCount);
    }

    private string Measure(string row, Route route, bool sparse, TsValue[] explicitBounds)
    {
        var array = new TsArray();
        var originals = new TsValue?[4];
        for (int i = 0; i < originals.Length; i++)
        {
            if (sparse && i % 2 == 0)
                array.SetHole(i);
            else
            {
                var original = TsValue.FromInt32(i + 1);
                originals[i] = original;
                array.Add(original);
            }
        }
        var wrapper = new TsArrayValue(array);
        var fill = Assert.IsType<TsInt32Value>(TsValue.FromInt32(9));
        Assert.Equal(TsValueType.Int32, fill.ValueType);
        Assert.Equal(9, fill.Value);
        var before = Snapshot(array, originals, fill);
        Assert.Equal(sparse ? "h2h4" : "1234", before);
        TsValue[] arguments = [wrapper, fill, .. explicitBounds];
        Assert.Equal(route.ParameterCount, arguments.Length);
        var bounds = arguments.Skip(2).Select(ObserveBound).ToArray();

        TsValue? returned = null;
        var error = Record.Exception(() =>
        {
            returned = new TypeSharp.VM.Interpreter.Interpreter().Execute(route.Module, route.Main, arguments);
        });
        Assert.Null(error);
        var result = Assert.IsType<TsArrayValue>(returned);
        Assert.Equal(TsValueType.Array, result.ValueType);
        bool sameWrapper = ReferenceEquals(wrapper, result);
        bool sameArray = ReferenceEquals(array, result.Value);
        Assert.True(sameWrapper);
        Assert.True(sameArray);
        Assert.Equal(originals.Length, result.Value.Count);
        var after = Snapshot(result.Value, originals, fill);

        Write(new
        {
            row,
            route = route.Id,
            bounds,
            fillWrapper = fill.GetType().Name,
            fillTag = fill.ValueType.ToString(),
            fillValue = fill.Value,
            before,
            count = result.Value.Count,
            resultWrapper = result.GetType().Name,
            resultTag = result.ValueType.ToString(),
            sameWrapper,
            sameArray,
            after
        });
        return after;
    }

    private static string Snapshot(TsArray array, TsValue?[] originals, TsInt32Value fill)
    {
        Assert.Equal(originals.Length, array.Count);
        var codes = new char[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            var value = array.Get(i);
            if (array.IsHole(i))
            {
                Assert.Null(originals[i]);
                Assert.Same(TsValue.Void, value);
                Assert.Equal(TsValueType.Void, value.ValueType);
                codes[i] = 'h';
                continue;
            }

            var number = Assert.IsType<TsInt32Value>(value);
            Assert.Equal(TsValueType.Int32, number.ValueType);
            if (ReferenceEquals(fill, number))
                Assert.Equal(9, number.Value);
            else
            {
                Assert.Same(originals[i], number);
                Assert.Equal(i + 1, number.Value);
            }
            codes[i] = (char)('0' + number.Value);
        }
        return new string(codes);
    }

    private static object ObserveBound(TsValue bound)
    {
        switch (bound)
        {
            case TsStringValue text:
                Assert.Equal(TsValueType.String, text.ValueType);
                return Observation(text, text.Value);
            case TsInt32Value integer:
                Assert.Equal(TsValueType.Int32, integer.ValueType);
                return Observation(integer, integer.Value);
            case TsFloat64Value number:
                Assert.Equal(TsValueType.Float64, number.ValueType);
                return Observation(number, double.IsNaN(number.Value) ? "NaN" : number.Value);
            case TsVoid:
                Assert.Same(TsValue.Void, bound);
                Assert.Equal(TsValueType.Void, bound.ValueType);
                return Observation(bound, "undefined");
            case TsNull:
                Assert.Same(TsValue.Null, bound);
                Assert.Equal(TsValueType.Null, bound.ValueType);
                return Observation(bound, "null");
            default:
                throw new InvalidOperationException("Unexpected bound wrapper");
        }
    }

    private static object Observation(TsValue bound, object payload) =>
        new { wrapper = bound.GetType().Name, tag = bound.ValueType.ToString(), value = payload };

    private static void CheckMutableArray(TsType type)
    {
        var array = Assert.IsType<TsArrayType>(type);
        Assert.Same(TsType.Any, array.ElementType);
        Assert.False(array.IsReadonly);
    }

    private static void CheckParameterUse(BoundNode node, ParameterSymbol parameter)
    {
        var variable = Assert.IsType<BoundVariableExpression>(node);
        Assert.Same(parameter, Assert.IsType<ParameterSymbol>(variable.Symbol));
        Assert.Same(parameter.Type, variable.Type);
    }

    private static void CheckInstruction(Instruction instruction, Opcode opcode, int operand = 0, int argc = 0,
        string? target = null)
    {
        Assert.Equal(opcode, instruction.Opcode);
        Assert.Equal(operand, instruction.Operand0);
        Assert.Equal(argc, instruction.Operand1);
        if (target is null)
            Assert.Null(instruction.OperandObject);
        else
            Assert.Equal(target, Assert.IsType<string>(instruction.OperandObject));
    }

    private void Write(object observation) => output.WriteLine(Prefix + " " + JsonSerializer.Serialize(observation));

    private sealed record Route(string Id, BytecodeModule Module, string Main, int ParameterCount);
}
