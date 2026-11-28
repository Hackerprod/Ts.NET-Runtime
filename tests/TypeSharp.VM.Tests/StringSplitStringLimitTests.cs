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

public sealed class StringSplitStringLimitTests(ITestOutputHelper output)
{
    private const string Source = "function main(text: string, separator: string, limit: any): string[] { return text.split(separator, limit); }";
    private const string Target = "String::split";

    [Fact]
    public void Split_UsesExplicitDecimalTextLimit()
    {
        var tokens = new Lexer(Source, "c211-string-split-string-limit.ts").Tokenize();
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
        CheckStringArrayType(function.Symbol.Type);
        var parameters = function.Symbol.Parameters;
        Assert.Equal(new[] { "text", "separator", "limit" }, parameters.Select(parameter => parameter.Name));
        Assert.Same(TsType.String, parameters[0].Type);
        Assert.Same(TsType.String, parameters[1].Type);
        Assert.Same(TsType.Any, parameters[2].Type);

        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var boundCall = Assert.IsType<BoundCallExpression>(ret.Value);
        CheckStringArrayType(boundCall.Type);
        var member = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal("String", method.DeclaringClassName);
        Assert.Equal("split", method.Name);
        CheckStringArrayType(method.Type);
        CheckParameterUse(member.Object, parameters[0], TsType.String);
        Assert.Collection(boundCall.Arguments,
            argument => CheckParameterUse(argument, parameters[1], TsType.String),
            argument => CheckParameterUse(argument, parameters[2], TsType.Any));

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions, item => item.Name == "main");
        Assert.Equal(new[] { "text", "separator", "limit" }, irMain.Parameters.Select(parameter => parameter.Name));
        Assert.Collection(Assert.Single(irMain.Blocks).Instructions,
            load => CheckLoad(load, 0),
            load => CheckLoad(load, 1),
            load => CheckLoad(load, 2),
            call =>
            {
                Assert.Equal(Opcode.Call, call.Opcode);
                Assert.Equal(Target, Assert.IsType<string>(call.OperandObject));
                Assert.Equal(3, call.Operand1);
            },
            instruction => Assert.Equal(Opcode.Return, instruction.Opcode));

        var compiled = BytecodeCompiler.Compile(ir);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        var compiledMain = Assert.Single(compiled.Functions, item => item.Name == irMain.Name);
        var restoredMain = Assert.Single(restored.Functions, item => item.Name == irMain.Name);
        Assert.Equal(3, compiledMain.ParameterCount);
        Assert.Equal(compiledMain.ParameterCount, restoredMain.ParameterCount);
        Assert.Equal(compiledMain.Instructions, restoredMain.Instructions);
        Assert.Equal(compiledMain.StringConstants, restoredMain.StringConstants);

        var (loads, targetIndex, argc) = DecodeDirectCall(restoredMain.Instructions);
        Assert.InRange(targetIndex, 0, restoredMain.StringConstants.Length - 1);
        var target = restoredMain.StringConstants[targetIndex];
        Assert.Equal(Target, target);
        Assert.Equal(3, argc);
        Write(new
        {
            row = "route",
            source = Source,
            lexerErrors,
            parserDiagnostics = parser.Diagnostics.Count,
            binderDiagnostics = binder.Diagnostics.All.Count,
            main = restoredMain.Name,
            parameterCount = restoredMain.ParameterCount,
            route = "LoadArg(0),LoadArg(1),LoadArg(2),Call(3),Return",
            loadArgs = loads,
            target,
            argc
        });

        var first = new[] { "a" };
        var all = new[] { "a", "b", "c" };
        var numeric = Assert.IsType<TsFloat64Value>(TsValue.FromFloat64(1.9d));
        Assert.Equal(TsValueType.Float64, numeric.ValueType);
        Assert.Equal(1.9d, numeric.Value);
        Check("float64-1.9", numeric, numeric.Value, first);
        var boolean = Assert.IsType<TsBoolValue>(TsValue.FromBool(true));
        Assert.Equal(TsValueType.Bool, boolean.ValueType);
        Assert.True(boolean.Value);
        Check("boolean-true", boolean, boolean.Value, first);
        var undefined = Assert.IsType<TsVoid>(TsValue.Void);
        Assert.Equal(TsValueType.Void, undefined.ValueType);
        Check("explicit-undefined", undefined, "undefined", all);
        CheckText("text-radix", "0x1", first);
        CheckText("text-exponent", "1e0", first);
        CheckText("text-empty", "", []);
        CheckText("text-trailing-nul", "1\0", []);
        CheckText("text-negative-decimal", "-1.9", all);

        var decimalText = CheckedString("1.9");
        var observed = Run("text-positive-decimal", decimalText, decimalText.Value, first);
        Assert.True(first.SequenceEqual(observed, StringComparer.Ordinal),
            $"C211_STRING_SPLIT_STRING_LIMIT_RESULT expected {JsonSerializer.Serialize(first)}, observed {JsonSerializer.Serialize(observed)}");

        void CheckText(string label, string value, string[] expected)
        {
            var text = CheckedString(value);
            Check(label, text, text.Value, expected);
        }

        void Check(string label, TsValue limit, object limitValue, string[] expected)
        {
            var actual = Run(label, limit, limitValue, expected);
            Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
                $"Control {label}: expected {JsonSerializer.Serialize(expected)}, observed {JsonSerializer.Serialize(actual)}");
        }

        string[] Run(string label, TsValue limit, object limitValue, string[] expected)
        {
            var receiver = CheckedString("a,b,c");
            var separator = CheckedString(",");
            var result = Assert.IsType<TsArrayValue>(
                new TypeSharp.VM.Interpreter.Interpreter().Execute(restored, restoredMain.Name, [receiver, separator, limit]));
            Assert.Equal(TsValueType.Array, result.ValueType);
            var array = result.Value;
            var actual = new string[array.Count];
            var elements = new object[array.Count];
            for (int i = 0; i < array.Count; i++)
            {
                Assert.False(array.IsHole(i));
                var item = Assert.IsType<TsStringValue>(array.Get(i));
                Assert.Equal(TsValueType.String, item.ValueType);
                actual[i] = item.Value;
                elements[i] = ValueObservation(item, item.Value);
            }

            Write(new
            {
                row = label,
                target,
                argc,
                inputs = new[] { ValueObservation(receiver, receiver.Value), ValueObservation(separator, separator.Value), ValueObservation(limit, limitValue) },
                returnedNormally = true,
                resultWrapper = result.GetType().Name,
                resultTag = result.ValueType.ToString(),
                dense = true,
                count = array.Count,
                elements,
                observed = actual,
                expected
            });
            return actual;
        }
    }

    private static void CheckStringArrayType(TsType type)
    {
        var array = Assert.IsType<TsArrayType>(type);
        Assert.Same(TsType.String, array.ElementType);
        Assert.False(array.IsReadonly);
    }

    private static void CheckParameterUse(BoundNode node, ParameterSymbol parameter, TsType type)
    {
        var variable = Assert.IsType<BoundVariableExpression>(node);
        Assert.Same(parameter, Assert.IsType<ParameterSymbol>(variable.Symbol));
        Assert.Same(type, variable.Type);
    }

    private static void CheckLoad(Instruction instruction, int index)
    {
        Assert.Equal(Opcode.LoadArg, instruction.Opcode);
        Assert.Equal(index, instruction.Operand0);
    }

    private static (int[] Loads, int TargetIndex, int Argc) DecodeDirectCall(byte[] bytes)
    {
        var sequence = new List<byte>();
        var loads = new List<int>();
        var calls = new List<(int TargetIndex, int Argc)>();
        for (int offset = 0; offset < bytes.Length;)
        {
            var opcode = bytes[offset];
            Assert.True(OpcodeFormats.TryGet(opcode, out var format), $"Unknown opcode at {offset}");
            int width = 1 + format.OperandBytes;
            Assert.True(width <= bytes.Length - offset, $"Truncated opcode at {offset}");
            sequence.Add(opcode);
            if (opcode == Opcodes.LoadArg)
            {
                Assert.Equal(OperandKind.Int32, format.Operand);
                loads.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)));
            }
            else if (opcode == Opcodes.Call)
            {
                Assert.Equal(OperandKind.Int32x2, format.Operand);
                calls.Add((
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4))));
            }
            else if (opcode == Opcodes.Return)
            {
                Assert.Equal(OperandKind.None, format.Operand);
            }
            offset += width;
        }
        Assert.Equal(new[] { Opcodes.LoadArg, Opcodes.LoadArg, Opcodes.LoadArg, Opcodes.Call, Opcodes.Return }, sequence.ToArray());
        var orderedLoads = loads.ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, orderedLoads);
        var (targetIndex, argc) = Assert.Single(calls);
        return (orderedLoads, targetIndex, argc);
    }

    private static TsStringValue CheckedString(string value)
    {
        var text = Assert.IsType<TsStringValue>(TsValue.FromString(value));
        Assert.Equal(TsValueType.String, text.ValueType);
        Assert.True(string.Equals(value, text.Value, StringComparison.Ordinal));
        return text;
    }

    private static object ValueObservation(TsValue item, object value) =>
        new { wrapper = item.GetType().Name, tag = item.ValueType.ToString(), value };

    private void Write(object observation) =>
        output.WriteLine("C211_STRING_SPLIT_STRING_LIMIT " + JsonSerializer.Serialize(observation));
}
