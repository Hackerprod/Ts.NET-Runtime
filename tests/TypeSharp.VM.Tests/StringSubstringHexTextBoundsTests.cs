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

public sealed class StringSubstringHexTextBoundsTests(ITestOutputHelper output)
{
    private const string Target = "String::substring";
    private const string Prefix = "C244_STRING_SUBSTRING_HEX_TEXT_BOUNDS";

    [Fact]
    public void Substring_CoercesUnsignedHexTextBounds()
    {
        var two = CompileRoute("route-two-bounds", "c244-string-substring-two-bounds.ts",
            "function main(s: string, start: any, end: any): string { return s.substring(start, end); }",
            ["s", "start", "end"], [0, 1, 2], "12000000001201000000120200000073000000000300000075");
        var one = CompileRoute("route-one-bound", "c244-string-substring-one-bound.ts",
            "function main(s: string, start: any): string { return s.substring(start); }",
            ["s", "start"], [0, 1], "1200000000120100000073000000000200000075");

        InputRow[] rows =
        [
            new("hex-end", "abcdef", 1, "0x4"),
            new("uppercase-hex-start-swap", "abcdef", "0X4", 1),
            new("trimmed-hex-end-cap", "abcdef", 1, " 0xA "),
            new("two-hex-utf16", "a\uD83D\uDE00z", "0x1", "0X3"),
            new("decimal-text-control", "abcdef", 1, "4"),
            new("signed-text-fallback", "abcdef", 1, "+0x4"),
            new("explicit-undefined-end", "abcdef", 1, TsValue.Void),
            new("omitted-end", "abcdef", 1, null)
        ];
        var observed = rows.Select(row => Measure(row.End is null ? one : two, row)).ToArray();

        Assert.Equal("bcd", observed[4]);
        Assert.Equal("a", observed[5]);
        Assert.Equal("bcdef", observed[6]);
        Assert.Equal("bcdef", observed[7]);

        string[] stock = ["a", "a", "a", "", "bcd", "a", "bcdef", "bcdef"];
        string[] fixedProfile = ["bcd", "bcd", "bcdef", "\uD83D\uDE00", "bcd", "a", "bcdef", "bcdef"];
        bool matchesStock = observed.SequenceEqual(stock, StringComparer.Ordinal);
        bool matchesFixed = observed.SequenceEqual(fixedProfile, StringComparer.Ordinal);
        Assert.True(matchesStock ^ matchesFixed, "Unexpected joint String.substring observation profile");

        if (matchesFixed)
            Assert.Equal(new[] { '\uD83D', '\uDE00' }, observed[3].ToCharArray());
        else
            Assert.Empty(observed[3]);

        Assert.True(matchesFixed,
            "C244_STRING_SUBSTRING_HEX_TEXT_BOUNDS_RESULT expected fixed unsigned hexadecimal substring bounds");
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
        Assert.Same(TsType.String, function.Symbol.Type);
        var parameters = function.Symbol.Parameters.ToArray();
        Assert.Equal(names, parameters.Select(parameter => parameter.Name).ToArray());
        for (int i = 0; i < parameters.Length; i++)
            Assert.Same(i == 0 ? TsType.String : TsType.Any, parameters[i].Type);

        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.Same(TsType.String, call.Type);
        Assert.False(call.IsNullConditional);
        var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        Assert.False(member.IsNullConditional);
        var memberType = Assert.IsType<TsFunctionType>(member.Type);
        Assert.Same(TsType.String, memberType.ReturnType);
        Assert.True(memberType.HasDynamicSignature);
        Assert.Empty(memberType.Parameters);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Assert.Equal("substring", method.Name);
        Assert.Equal("String", method.DeclaringClassName);
        Assert.Same(TsType.String, method.Type);
        Assert.False(method.IsStatic);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        Assert.Null(method.RuntimeName);
        Assert.Equal(Target, $"{method.DeclaringClassName}::{method.RuntimeName ?? method.Name}");
        Assert.Equal(parameters.Length - 1, call.Arguments.Count);
        CheckParameterUse(member.Object, parameters[0]);
        for (int i = 1; i < parameters.Length; i++)
            CheckParameterUse(call.Arguments[i - 1], parameters[i]);

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions);
        Assert.Equal(function.Symbol.Name, irMain.Name);
        Assert.Same(TsType.String, irMain.ReturnType);
        Assert.Equal(names, irMain.Parameters.Select(parameter => parameter.Name).ToArray());
        for (int i = 0; i < irMain.Parameters.Count; i++)
            Assert.Same(parameters[i].Type, irMain.Parameters[i].Type);
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

        Write(new
        {
            row,
            lexerErrors,
            parserDiagnostics,
            binderDiagnostics,
            main = restoredMain.Name,
            parameterCount = restoredMain.ParameterCount,
            argumentSlots = restoredCode.Slots,
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
        string hex = Convert.ToHexString(bytes);
        Assert.Equal(expectedHex, hex);
        Assert.Equal(expectedSlots.Length * 5 + 10, bytes.Length);
        Assert.Equal(Target, Assert.Single(function.StringConstants));

        int offset = 0;
        var slots = new int[expectedSlots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            Assert.Equal(Opcodes.LoadArg, bytes[offset++]);
            slots[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
            offset += 4;
        }
        Assert.Equal(expectedSlots, slots);
        Assert.Equal(Opcodes.Call, bytes[offset++]);
        int targetIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        Assert.Equal(0, targetIndex);
        string target = function.StringConstants[targetIndex];
        Assert.Equal(Target, target);
        offset += 4;
        int argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        Assert.Equal(expectedSlots.Length, argc);
        offset += 4;
        Assert.Equal(Opcodes.Return, bytes[offset++]);
        Assert.Equal(bytes.Length, offset);

        return (slots, target, argc, hex);
    }

    private string Measure(Route route, InputRow input)
    {
        TsValue[] args = input.End is null
            ? [TsValue.FromString(input.Receiver), MakeBound(input.Start)]
            : [TsValue.FromString(input.Receiver), MakeBound(input.Start), MakeBound(input.End)];
        Assert.Equal(route.ParameterCount, args.Length);
        Assert.Equal(input.End is null ? 2 : 3, args.Length);

        var receiver = Assert.IsType<TsStringValue>(args[0]);
        Assert.Equal(TsValueType.String, receiver.ValueType);
        Assert.Equal(input.Receiver, receiver.Value);
        if (input.Label == "two-hex-utf16")
            Assert.Equal(new[] { 'a', '\uD83D', '\uDE00', 'z' }, receiver.Value.ToCharArray());

        var start = CheckBound(input.Start, args[1]);
        (string Type, object? Value) end = input.End is null
            ? ("Omitted", null) : CheckBound(input.End, args[2]);
        var result = Assert.IsType<TsStringValue>(
            new TypeSharp.VM.Interpreter.Interpreter().Execute(route.Module, route.Main, args));
        Assert.Equal(TsValueType.String, result.ValueType);
        Write(new
        {
            row = input.Label,
            receiver = receiver.Value,
            startType = start.Type,
            start = start.Value,
            endType = end.Type,
            end = end.Value,
            outputType = result.ValueType.ToString(),
            observed = result.Value
        });
        return result.Value;
    }

    private static TsValue MakeBound(object expected) => expected switch
    {
        string text => TsValue.FromString(text),
        int integer => TsValue.FromInt32(integer),
        TsVoid => TsValue.Void,
        _ => throw new InvalidOperationException("Unexpected substring input specification")
    };

    private static (string Type, object Value) CheckBound(object expected, TsValue actual)
    {
        switch (expected)
        {
            case string text:
                var stringValue = Assert.IsType<TsStringValue>(actual);
                Assert.Equal(TsValueType.String, stringValue.ValueType);
                Assert.Equal(text, stringValue.Value);
                return (stringValue.ValueType.ToString(), stringValue.Value);
            case int integer:
                var intValue = Assert.IsType<TsInt32Value>(actual);
                Assert.Equal(TsValueType.Int32, intValue.ValueType);
                Assert.Equal(integer, intValue.Value);
                return (intValue.ValueType.ToString(), intValue.Value);
            case TsVoid:
                var undefined = Assert.IsType<TsVoid>(actual);
                Assert.Same(TsValue.Void, expected);
                Assert.Same(TsValue.Void, undefined);
                Assert.Equal(TsValueType.Void, undefined.ValueType);
                return (undefined.ValueType.ToString(), "undefined");
            default:
                throw new InvalidOperationException("Unexpected substring bound");
        }
    }

    private void Write(object observation) => output.WriteLine(Prefix + " " + JsonSerializer.Serialize(observation));

    private sealed record InputRow(string Label, string Receiver, object Start, object? End);
    private sealed record Route(BytecodeModule Module, string Main, int ParameterCount);
}
