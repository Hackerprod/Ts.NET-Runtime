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

public sealed class StringAtHexTextIndexTests(ITestOutputHelper output)
{
    private const string Target = "String::at";
    private const string Prefix = "C247_STRING_AT_HEX ";
    private const string Abc = "ABC";
    private const string Mixed = "A\U0001F600Z";
    private const string AbcProjection = @"\u0041\u0042\u0043";
    private const string MixedProjection = @"\u0041\uD83D\uDE00\u005A";

    [Fact]
    public void At_CoercesUnsignedHexTextIndex()
    {
        var explicitIndex = CompileRoute("explicit-route", "c247-string-at-hex.ts",
            "function main(text: string, index: any): string | undefined { return text.at(index); }",
            ["text", "index"], [0, 1], "1200000000120100000073000000000200000075");
        var omittedIndex = CompileRoute("omitted-route", "c247-string-at-omitted.ts",
            "function main(text: string): string | undefined { return text.at(); }",
            ["text"], [0], "120000000073000000000100000075");

        InputRow[] rows =
        [
            new("hex-inside", Abc, "0x1", AbcProjection, "String", @"\u0030\u0078\u0031"),
            new("hex-trimmed-uppercase", Abc, " 0X2 ", AbcProjection, "String", @"\u0020\u0030\u0058\u0032\u0020"),
            new("hex-at-length", Abc, "0x3", AbcProjection, "String", @"\u0030\u0078\u0033"),
            new("hex-beyond-length", Abc, "0x10", AbcProjection, "String", @"\u0030\u0078\u0031\u0030"),
            new("hex-zero", Abc, "0x0", AbcProjection, "String", @"\u0030\u0078\u0030"),
            new("hex-malformed", Abc, "0x2g", AbcProjection, "String", @"\u0030\u0078\u0032\u0067"),
            new("hex-signed", Abc, "-0x1", AbcProjection, "String", @"\u002D\u0030\u0078\u0031"),
            new("decimal-fraction", Abc, "1.9", AbcProjection, "String", @"\u0031\u002E\u0039"),
            new("decimal-relative", Abc, "-1", AbcProjection, "String", @"\u002D\u0031"),
            new("numeric-relative-fraction", Abc, -1.8, AbcProjection, "Float64", "BFFCCCCCCCCCCCCD"),
            new("explicit-undefined", Abc, TsValue.Void, AbcProjection, "Void", "undefined"),
            new("omitted-index", Abc, null, AbcProjection, "Omitted", null),
            new("utf16-low-unit", Mixed, -2.0, MixedProjection, "Float64", "C000000000000000")
        ];
        var observed = rows.Select(row => Measure(row.Index is null ? omittedIndex : explicitIndex, row)).ToArray();

        var a = new Result("String", @"\u0041");
        var b = new Result("String", @"\u0042");
        var c = new Result("String", @"\u0043");
        var low = new Result("String", @"\uDE00");
        var undefined = new Result("Void", null);
        Result[] controls = [a, a, a, b, c, c, a, a, low];
        Result[] stock = [a, a, a, a];
        Result[] green = [b, c, undefined, undefined];

        Assert.Equal(controls, observed[4..]);
        bool matchesStock = observed[..4].SequenceEqual(stock);
        bool matchesGreen = observed[..4].SequenceEqual(green);
        Assert.True(matchesStock ^ matchesGreen, "C247_STRING_AT_HEX_UNEXPECTED_PROFILE");
        Assert.True(matchesGreen,
            "C247_STRING_AT_HEX_RESULT expected B,C,Void,Void for valid unsigned hexadecimal positions");
    }

    private Route CompileRoute(string row, string file, string source, string[] names, int[] slots, string expectedHex)
    {
        var tokens = new Lexer(source, file).Tokenize();
        int lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        int parserDiagnostics = parser.Diagnostics.Count;
        Assert.Equal(0, parserDiagnostics);
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        int binderDiagnostics = binder.Diagnostics.All.Count();
        Assert.Equal(0, binderDiagnostics);

        var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", function.Symbol.Name);
        CheckReturnType(function.Symbol.Type);
        var parameters = function.Symbol.Parameters.ToArray();
        Assert.Equal(names, parameters.Select(parameter => parameter.Name).ToArray());
        for (int i = 0; i < parameters.Length; i++)
        {
            Assert.False(parameters[i].IsRest);
            Assert.Same(i == 0 ? TsType.String : TsType.Any, parameters[i].Type);
        }

        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        CheckReturnType(call.Type);
        Assert.False(call.IsNullConditional);
        var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        Assert.False(member.IsNullConditional);
        var memberType = Assert.IsType<TsFunctionType>(member.Type);
        CheckReturnType(memberType.ReturnType);
        Assert.True(memberType.HasDynamicSignature);
        Assert.Empty(memberType.Parameters);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal("at", method.Name);
        Assert.Equal("String", method.DeclaringClassName);
        CheckReturnType(method.Type);
        Assert.False(method.IsStatic);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Equal(Target, $"{method.DeclaringClassName}::{method.RuntimeName ?? method.Name}");
        Assert.Equal(parameters.Length - 1, call.Arguments.Count);
        CheckParameterUse(member.Object, parameters[0]);
        for (int i = 1; i < parameters.Length; i++)
            CheckParameterUse(call.Arguments[i - 1], parameters[i]);

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions);
        Assert.Equal(function.Symbol.Name, irMain.Name);
        CheckReturnType(irMain.ReturnType);
        Assert.Equal(names, irMain.Parameters.Select(parameter => parameter.Name).ToArray());
        for (int i = 0; i < irMain.Parameters.Count; i++)
        {
            Assert.False(irMain.Parameters[i].IsRest);
            Assert.Same(parameters[i].Type, irMain.Parameters[i].Type);
        }
        var instructions = Assert.Single(irMain.Blocks).Instructions;
        Assert.Equal(slots.Length + 2, instructions.Count);
        for (int i = 0; i < slots.Length; i++)
        {
            Assert.Equal(Opcode.LoadArg, instructions[i].Opcode);
            Assert.Equal(slots[i], instructions[i].Operand0);
        }
        Assert.Equal(Opcode.Call, instructions[^2].Opcode);
        Assert.Equal(Target, Assert.IsType<string>(instructions[^2].OperandObject));
        Assert.Equal(slots.Length, instructions[^2].Operand1);
        Assert.Equal(Opcode.Return, instructions[^1].Opcode);

        var compiled = BytecodeCompiler.Compile(ir);
        BytecodeVerifier.Verify(compiled);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        Assert.True(stream.Length > 0);
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
        var compiledCode = Decode(compiledMain, slots, expectedHex);
        var restoredCode = Decode(restoredMain, slots, expectedHex);

        Assert.Equal(compiledCode.Slots, restoredCode.Slots);
        Assert.Equal(compiledCode.Target, restoredCode.Target);
        Assert.Equal(compiledCode.Argc, restoredCode.Argc);
        Assert.Equal(compiledCode.Hex, restoredCode.Hex);

        Write(new
        {
            row,
            lexerErrors,
            parserDiagnostics,
            binderDiagnostics,
            main = restoredMain.Name,
            parameterCount = restoredMain.ParameterCount,
            receiverSlot = restoredCode.Slots[0],
            indexSlot = restoredCode.Slots.Length == 2 ? (int?)restoredCode.Slots[1] : null,
            target = restoredCode.Target,
            argc = restoredCode.Argc,
            compiledHex = compiledCode.Hex,
            restoredHex = restoredCode.Hex
        });
        return new Route(restored, restoredMain.Name, restoredMain.ParameterCount);
    }

    private static void CheckParameterUse(BoundNode node, ParameterSymbol parameter)
    {
        var variable = Assert.IsType<BoundVariableExpression>(node);
        Assert.Same(parameter, Assert.IsType<ParameterSymbol>(variable.Symbol));
        Assert.Same(parameter.Type, variable.Type);
    }

    private static (int[] Slots, string Target, int Argc, string Hex) Decode(
        BytecodeFunction function, int[] expectedSlots, string expectedHex)
    {
        byte[] bytes = function.Instructions;
        string hex = Convert.ToHexString(bytes).ToLowerInvariant();
        Assert.Equal(expectedHex, hex);
        Assert.Equal(expectedSlots.Length * 5 + 10, bytes.Length);
        Assert.Equal(Target, Assert.Single(function.StringConstants));

        int offset = 0;
        var slots = new int[expectedSlots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            Assert.Equal((byte)0x12, bytes[offset++]);
            slots[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
            offset += 4;
        }
        Assert.Equal(expectedSlots, slots);
        Assert.Equal((byte)0x73, bytes[offset++]);
        int targetIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        Assert.Equal(0, targetIndex);
        string target = function.StringConstants[targetIndex];
        Assert.Equal(Target, target);
        offset += 4;
        int argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        Assert.Equal(expectedSlots.Length, argc);
        offset += 4;
        Assert.Equal((byte)0x75, bytes[offset++]);
        Assert.Equal(bytes.Length, offset);

        return (slots, target, argc, hex);
    }

    private static void CheckReturnType(TsType type)
    {
        var members = Assert.IsType<TsUnionType>(type).Types;
        Assert.Equal(2, members.Count);
        Assert.Single(members, member => member.Equals(TsType.String));
        Assert.Single(members, member => member.Equals(TsType.Undefined));
    }

    private Result Measure(Route route, InputRow specification)
    {
        var receiver = Assert.IsType<TsStringValue>(TsValue.FromString(specification.Receiver));
        Assert.Equal(TsValueType.String, receiver.ValueType);
        Assert.Equal(specification.ReceiverProjection, ProjectText(receiver.Value));

        TsValue[] args = specification.Index switch
        {
            null => [receiver],
            string index => [receiver, TsValue.FromString(index)],
            double index => [receiver, TsValue.FromFloat64(index)],
            TsVoid => [receiver, TsValue.Void],
            _ => throw new InvalidOperationException("Unexpected input specification")
        };
        Assert.Equal(route.ParameterCount, args.Length);
        (string Type, string? Value) input = args.Length == 1 ? ("Omitted", null) : ProjectInput(args[1]);
        Assert.Equal(specification.InputType, input.Type);
        Assert.Equal(specification.InputProjection, input.Value);
        var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(route.Module, route.Main, args);
        var observation = ProjectResult(result);
        Write(new
        {
            row = specification.Row,
            receiver = ProjectText(receiver.Value),
            inputType = input.Type,
            input = input.Value,
            outputType = observation.Type,
            observed = observation.Value
        });
        return observation;
    }

    private static (string Type, string Value) ProjectInput(TsValue value)
    {
        switch (value)
        {
            case TsStringValue text:
                Assert.Equal(TsValueType.String, text.ValueType);
                return (text.ValueType.ToString(), ProjectText(text.Value));
            case TsFloat64Value number:
                Assert.Equal(TsValueType.Float64, number.ValueType);
                return (number.ValueType.ToString(), BitConverter.DoubleToInt64Bits(number.Value).ToString("X16"));
            default:
                return (CheckVoid(value).ValueType.ToString(), "undefined");
        }
    }

    private static Result ProjectResult(TsValue? value)
    {
        if (value is TsStringValue text)
        {
            Assert.Equal(TsValueType.String, text.ValueType);
            Assert.Equal(1, text.Value.Length);
            return new Result(text.ValueType.ToString(), ProjectText(text.Value));
        }

        return new Result(CheckVoid(value).ValueType.ToString(), null);
    }

    private static TsVoid CheckVoid(TsValue? value)
    {
        var undefined = Assert.IsType<TsVoid>(value);
        Assert.Same(TsValue.Void, undefined);
        Assert.Equal(TsValueType.Void, undefined.ValueType);
        Assert.Null(undefined.RawValue);
        return undefined;
    }

    private static string ProjectText(string text) => string.Concat(text.Select(unit => $"\\u{(int)unit:X4}"));

    private void Write(object observation) => output.WriteLine(Prefix + JsonSerializer.Serialize(observation));

    private sealed record InputRow(string Row, string Receiver, object? Index, string ReceiverProjection,
        string InputType, string? InputProjection);
    private sealed record Route(BytecodeModule Module, string Main, int ParameterCount);
    private sealed record Result(string Type, string? Value);
}
