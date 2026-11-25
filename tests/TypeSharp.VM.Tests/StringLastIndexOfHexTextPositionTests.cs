using System.Buffers.Binary;
using System.Globalization;
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

public sealed class StringLastIndexOfHexTextPositionTests(ITestOutputHelper output)
{
    private const string Source = "function main(s: string, needle: string, p: string): int32 { return s.lastIndexOf(needle, p); }";
    private const string File = "c236-string-last-index-of-hex-text.ts";
    private const string Target = "String::lastIndexOf";
    private const string InstructionHex = "12000000001201000000120200000073000000000300000075";

    [Fact]
    public void StringLastIndexOf_CoercesHexTextPositionBeforeSearching()
    {
        var (module, main) = CompileRoute();
        InputRow[] rows =
        [
            new("later-excluded", "ababa", "ba", "0x2"),
            new("exact-start", "ababa", "ba", "0x1"),
            new("all-excluded", "ababa", "ba", "0x0"),
            new("ecma-trim-mixed", "abcdefghijabcabc", "abc", "\uFEFF0Xa\u2028"),
            new("empty-needle", "abcdef", "", "0x3"),
            new("saturated", "ababa", "ba", "0xABCDEF0123456789ABCDEF"),
            new("utf16", "\uD83D\uDE00x\uD83D\uDE00x", "x", "0x3"),
            new("ordinal", "aA", "A", "0x0"),
            new("decimal", "ababa", "ba", " 2.9 "),
            new("fallback", "ababa", "ba", "other"),
            new("long-needle", "a", "ab", "0x2"),
            new("empty-both", "", "", "0x3")
        ];
        var observed = new (TsValue? Result, Exception? Escaped)[rows.Length];
        for (int i = 0; i < rows.Length; i++)
            observed[i] = Execute(module, main, rows[i]);

        var results = new int[rows.Length];
        for (int i = 0; i < observed.Length; i++)
        {
            var (result, escaped) = observed[i];
            Assert.Null(escaped);
            var integer = Assert.IsType<TsInt32Value>(result);
            Assert.Equal(TsValueType.Int32, integer.ValueType);
            results[i] = integer.Value;
        }

        int[] stock = [3, 3, 3, 13, 6, 3, 5, 1, 1, 3, -1, 0];
        int[] fixedProfile = [1, 1, -1, 10, 3, 3, 2, -1, 1, 3, -1, 0];
        bool matchesStock = results.SequenceEqual(stock);
        bool matchesFixed = results.SequenceEqual(fixedProfile);
        Assert.True(matchesFixed, matchesStock ? $"C236_STRING_LAST_INDEX_OF_HEX_TEXT_POSITION_RESULT expected {CompactProfile(fixedProfile)}, observed {CompactProfile(results)}"
            : $"Unexpected joint String.lastIndexOf observation profile: {CompactProfile(results)}");
    }

    private static string CompactProfile(int[] values) =>
        "[" + string.Join(",", values.Select(value => value.ToString(CultureInfo.InvariantCulture))) + "]";

    private (BytecodeModule Module, string Main) CompileRoute()
    {
        var tokens = new Lexer(Source, File).Tokenize();
        int lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        Assert.Equal(0, lexerErrors);
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var parsed = parser.Parse();
        int parserDiagnostics = parser.Diagnostics.Count;
        Assert.Equal(0, parserDiagnostics);
        var binder = new Binder();
        var bound = binder.Bind(parsed);
        int binderDiagnostics = binder.Diagnostics.All.Count();
        Assert.Equal(0, binderDiagnostics);

        var function = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", function.Symbol.Name);
        Assert.Same(TsType.Int32, function.Symbol.Type);
        var parameters = function.Symbol.Parameters.ToArray();
        string[] formalNames = ["s", "needle", "p"];
        TsType[] formalTypes = [TsType.String, TsType.String, TsType.String];
        Assert.Equal(formalNames, parameters.Select(parameter => parameter.Name).ToArray());
        for (int i = 0; i < parameters.Length; i++)
            Assert.Same(formalTypes[i], parameters[i].Type);
        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var boundCall = Assert.IsType<BoundCallExpression>(ret.Value);
        Assert.Same(TsType.Int32, boundCall.Type);
        Assert.False(boundCall.IsNullConditional);
        var access = Assert.IsType<BoundMemberAccessExpression>(boundCall.Callee);
        Assert.False(access.IsNullConditional);
        var accessType = Assert.IsType<TsFunctionType>(access.Type);
        Assert.Same(TsType.Int32, accessType.ReturnType);
        Assert.True(accessType.HasDynamicSignature);
        Assert.Empty(accessType.Parameters);
        var method = Assert.IsType<MethodSymbol>(access.Member);
        Assert.Equal("lastIndexOf", method.Name);
        Assert.Equal("String", method.DeclaringClassName);
        Assert.Same(TsType.Int32, method.Type);
        Assert.False(method.IsStatic);
        Assert.True(method.HasDynamicSignature);
        Assert.Empty(method.Parameters);
        // The direct-call lowering uses owner plus RuntimeName, falling back to Name.
        string boundTarget = $"{method.DeclaringClassName}::{method.RuntimeName ?? method.Name}";
        Assert.Equal(Target, boundTarget);
        Assert.Equal(2, boundCall.Arguments.Count);
        var boundOperands = new[]
        {
            CheckOperand(access.Object, parameters, 0),
            CheckOperand(boundCall.Arguments[0], parameters, 1),
            CheckOperand(boundCall.Arguments[1], parameters, 2)
        };

        var ir = new IRGenerator().Generate(bound);
        var irMain = Assert.Single(ir.Functions);
        Assert.Equal("main", irMain.Name);
        Assert.Equal(formalNames, irMain.Parameters.Select(parameter => parameter.Name).ToArray());
        var block = Assert.Single(irMain.Blocks);
        var instructions = block.Instructions.ToArray();
        Assert.Equal(new[] { Opcode.LoadArg, Opcode.LoadArg, Opcode.LoadArg, Opcode.Call, Opcode.Return },
            instructions.Select(instruction => instruction.Opcode).ToArray());
        int[] irSlots = instructions.Take(3).Select(load => load.Operand0).ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, irSlots);
        var irCall = instructions[3];
        string irTarget = Assert.IsType<string>(irCall.OperandObject);
        Assert.Equal(Target, irTarget);
        Assert.Equal(3, irCall.Operand1);

        var compiled = BytecodeCompiler.Compile(ir);
        bool compiledVerified = Verify(compiled);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, compiled);
        Assert.True(stream.Length > 0);
        stream.Position = 0;
        var restored = BytecodeSerializer.Deserialize(stream);
        Assert.Equal(stream.Length, stream.Position);
        bool restoredVerified = Verify(restored);
        var compiledMain = Assert.Single(compiled.Functions);
        var restoredMain = Assert.Single(restored.Functions);
        Assert.Equal("main", compiledMain.Name);
        Assert.Equal("main", restoredMain.Name);
        Assert.Equal(3, compiledMain.ParameterCount);
        Assert.Equal(3, restoredMain.ParameterCount);
        bool sameInstructionBytes = compiledMain.Instructions.SequenceEqual(restoredMain.Instructions);
        bool sameStringTable = compiledMain.StringConstants.SequenceEqual(restoredMain.StringConstants, StringComparer.Ordinal);
        Assert.True(sameInstructionBytes);
        Assert.True(sameStringTable);
        Assert.Equal(Target, Assert.Single(compiledMain.StringConstants));
        Assert.Equal(Target, Assert.Single(restoredMain.StringConstants));
        Assert.Equal(25, compiledMain.Instructions.Length);
        Assert.Equal(25, restoredMain.Instructions.Length);
        var (compiledDecoded, compiledConsumedBytes) = Decode(compiledMain.Instructions, compiledMain.StringConstants, compiledMain.ParameterCount);
        string compiledInstructionHex = Convert.ToHexString(compiledMain.Instructions);
        Assert.Equal(InstructionHex, compiledInstructionHex);
        var (decoded, consumedBytes) = Decode(restoredMain.Instructions, restoredMain.StringConstants, restoredMain.ParameterCount);
        string instructionHex = Convert.ToHexString(restoredMain.Instructions);
        Assert.Equal(InstructionHex, instructionHex);
        Assert.Equal(new[] { 0, 5, 10, 15, 24 }, decoded.Select(item => item.offset).ToArray());
        Assert.Equal(new[] { "LoadArg", "LoadArg", "LoadArg", "Call", "Return" }, decoded.Select(item => item.opcode).ToArray());
        Assert.Equal(new int?[] { 0, 1, 2, null, null }, decoded.Select(item => item.slot).ToArray());
        Assert.Equal(0, decoded[3].targetIndex.GetValueOrDefault(-1));
        Assert.Equal(Target, decoded[3].target);
        Assert.Equal(3, decoded[3].argc.GetValueOrDefault(-1));
        Assert.Equal(25, consumedBytes);
        Assert.Equal(25, compiledConsumedBytes);
        Assert.True(compiledDecoded.SequenceEqual(decoded));

        Write(new
        {
            kind = "route",
            source = Source,
            file = File,
            lexerErrors,
            parserDiagnostics,
            binderDiagnostics,
            boundMemberCount = bound.Members.Count(),
            boundStatementCount = body.Statements.Count(),
            boundMain = function.Symbol.Name,
            functionResultType = function.Symbol.Type.ToString(),
            boundOperands,
            calleeKind = access.GetType().Name,
            calleeType = accessType.ToString(),
            boundArgumentCount = boundCall.Arguments.Count,
            methodKind = method.GetType().Name,
            method = method.Name,
            owner = method.DeclaringClassName,
            methodIsStatic = method.IsStatic,
            boundTarget,
            methodResultType = method.Type.ToString(),
            callResultType = boundCall.Type.ToString(),
            irFunctionCount = ir.Functions.Count(),
            irBlockCount = irMain.Blocks.Count(),
            irMain = irMain.Name,
            irSequence = instructions.Select(instruction => instruction.Opcode.ToString()).ToArray(),
            irSlots,
            irTarget,
            irArgc = irCall.Operand1,
            compiledFunctionCount = compiled.Functions.Length,
            compiledMain = compiledMain.Name,
            compiledParameterCount = compiledMain.ParameterCount,
            compiledVerified,
            compiledDecoded,
            compiledConsumedBytes,
            compiledInstructionByteLength = compiledMain.Instructions.Length,
            compiledInstructionHex,
            restoredFunctionCount = restored.Functions.Length,
            restoredMain = restoredMain.Name,
            restoredParameterCount = restoredMain.ParameterCount,
            restoredVerified,
            sameInstructionBytes,
            sameStringTable,
            stringConstants = restoredMain.StringConstants,
            decoded,
            consumedBytes,
            instructionByteLength = restoredMain.Instructions.Length,
            instructionHex
        });
        return (restored, restoredMain.Name);
    }

    private sealed record BoundOperand(
        int formalSlot, string formal, string formalType, int operandSlot, string operand, string operandType, bool sameSymbol);

    private static BoundOperand CheckOperand(BoundNode node, ParameterSymbol[] parameters, int slot)
    {
        var variable = Assert.IsType<BoundVariableExpression>(node);
        var symbol = Assert.IsType<ParameterSymbol>(variable.Symbol);
        var formal = parameters[slot];
        Assert.Same(formal, symbol);
        Assert.Same(formal.Type, variable.Type);
        int operandSlot = Array.FindIndex(parameters, candidate => ReferenceEquals(candidate, symbol));
        Assert.Equal(slot, operandSlot);
        return new(slot, formal.Name, formal.Type.ToString(), operandSlot, symbol.Name,
            variable.Type.ToString(), ReferenceEquals(formal, symbol));
    }

    private static bool Verify(BytecodeModule module)
    {
        BytecodeVerifier.Verify(module);
        return true;
    }

    private sealed record DecodedInstruction(
        int offset, string opcode, string operandKind, int operandBytes, int? slot, int? targetIndex, string? target, int? argc);

    private static (DecodedInstruction[] Instructions, int ConsumedBytes) Decode(byte[] bytes, string[] strings, int parameters)
    {
        var decoded = new List<DecodedInstruction>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            byte opcode = bytes[offset];
            Assert.True(OpcodeFormats.TryGet(opcode, out var format), $"Unknown opcode at {offset}");
            Assert.Equal(opcode, format.Code);
            Assert.InRange(format.OperandBytes, 0, bytes.Length - offset - 1);
            switch (opcode)
            {
                case Opcodes.LoadArg:
                    Assert.Equal(OperandKind.Int32, format.Operand);
                    Assert.Equal(4, format.OperandBytes);
                    int slot = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4));
                    Assert.InRange(slot, 0, parameters - 1);
                    decoded.Add(new(offset, nameof(Opcodes.LoadArg), format.Operand.ToString(), format.OperandBytes,
                        slot, null, null, null));
                    break;
                case Opcodes.Call:
                    Assert.Equal(OperandKind.Int32x2, format.Operand);
                    Assert.Equal(8, format.OperandBytes);
                    int index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4));
                    int argc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4));
                    Assert.InRange(index, 0, strings.Length - 1);
                    decoded.Add(new(offset, nameof(Opcodes.Call), format.Operand.ToString(), format.OperandBytes,
                        null, index, strings[index], argc));
                    break;
                case Opcodes.Return:
                    Assert.Equal(OperandKind.None, format.Operand);
                    Assert.Equal(0, format.OperandBytes);
                    decoded.Add(new(offset, nameof(Opcodes.Return), format.Operand.ToString(), format.OperandBytes,
                        null, null, null, null));
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected opcode {opcode} at {offset}");
            }
            offset += 1 + format.OperandBytes;
        }
        Assert.Equal(bytes.Length, offset);
        return (decoded.ToArray(), offset);
    }

    private sealed record InputRow(string Label, string Receiver, string Needle, string Position);

    private sealed record ArgumentObservation(string wrapper, string tag, string text, string[] utf16);

    private (TsValue? Result, Exception? Escaped) Execute(BytecodeModule module, string main, InputRow row)
    {
        string[] payloads = [row.Receiver, row.Needle, row.Position];
        TsValue[] args = payloads.Select(text => TsValue.FromString(text)).ToArray();
        var arguments = new ArgumentObservation[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            var argument = Assert.IsType<TsStringValue>(args[i]);
            Assert.Equal(TsValueType.String, argument.ValueType);
            Assert.True(string.Equals(payloads[i], argument.Value, StringComparison.Ordinal));
            arguments[i] = new(argument.GetType().Name, argument.ValueType.ToString(), argument.Value,
                argument.Value.Select(unit => ((ushort)unit).ToString("X4", CultureInfo.InvariantCulture)).ToArray());
        }

        var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
        TsValue? result = null;
        var escaped = Record.Exception(() => { result = interpreter.Execute(module, main, args); });
        Write(new
        {
            kind = "execution",
            row = row.Label,
            arguments,
            returnedNormally = escaped is null,
            escaped = escaped?.GetType().FullName,
            resultWrapper = result?.GetType().Name,
            resultTag = result?.ValueType.ToString(),
            observed = result is TsInt32Value integer ? (int?)integer.Value : null
        });
        return (result, escaped);
    }

    private void Write(object observation) =>
        output.WriteLine("C236_STRING_LAST_INDEX_OF_HEX_TEXT_POSITION " + JsonSerializer.Serialize(observation));
}
