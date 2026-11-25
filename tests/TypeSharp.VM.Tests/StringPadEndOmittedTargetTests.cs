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

public sealed class StringPadEndOmittedTargetTests(ITestOutputHelper output)
{
    [Fact]
    public void StringPadEnd_ReturnsReceiverWhenTargetIsOmitted()
    {
        const string source = "function main(): string { return \"ABC\".padEnd(); }";
        const string receiver = "ABC";
        const string target = "String::padEnd";

        var stages = new List<string>();
        int? lexerErrors = null, parserDiagnostics = null, binderDiagnostics = null;
        object? boundEvidence = null, irEvidence = null;
        object? compiledEvidence = null, restoredEvidence = null;
        bool? matchingInstructions = null, matchingStringPool = null;
        Exception? setupException = null, runtimeException = null;
        TsValue? result = null;

        try
        {
            var tokens = new Lexer(source).Tokenize();
            lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
            Require(lexerErrors == 0, "Lexer Error tokens");
            stages.Add("lexed");

            var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
            var syntax = parser.Parse();
            parserDiagnostics = parser.Diagnostics.Count;
            Require(parserDiagnostics == 0, "Parser diagnostics");
            stages.Add("parsed");

            var binder = new Binder();
            var bound = binder.Bind(syntax);
            binderDiagnostics = binder.Diagnostics.All.Count;
            Require(binderDiagnostics == 0, "Binder diagnostics");
            var boundMain = bound.Members.OfType<BoundFunctionDeclaration>()
                .Single(function => function.Symbol.Name == "main");
            var boundCall = ((boundMain.Body as BoundBlockStatement)?.Statements.SingleOrDefault()
                as BoundReturnStatement)?.Value as BoundCallExpression;
            var member = boundCall?.Callee as BoundMemberAccessExpression;
            var method = member?.Member as MethodSymbol;
            var literal = member?.Object as BoundLiteralExpression;
            var boundReceiver = literal?.Value as string;
            var boundTarget = method is null ? null : $"{method.DeclaringClassName}::{method.RuntimeName ?? method.Name}";
            var explicitArgs = boundCall?.Arguments.Count;
            boundEvidence = new { receiver = boundReceiver, target = boundTarget, explicitArgs };
            Require(boundMain.Symbol.Parameters.Count == 0 && explicitArgs == 0
                && boundCall?.Type == TsType.String && literal?.Type is TsLiteralType literalType
                && literalType.BaseType == TsType.String
                && boundReceiver == receiver && boundTarget == target, "Unexpected bound call");
            stages.Add("bound");

            var ir = new IRGenerator().Generate(bound);
            var irMain = ir.Functions.Single(function => function.Name == "main");
            var instructions = irMain.Blocks.SelectMany(block => block.Instructions).ToArray();
            var calls = instructions.Where(instruction => instruction.Opcode == Opcode.Call).ToArray();
            var loads = instructions.Where(instruction => instruction.Opcode == Opcode.LoadConst_String).ToArray();
            var irReceiver = loads.Length == 1 ? loads[0].OperandObject as string : null;
            var irTarget = calls.Length == 1 ? calls[0].OperandObject as string : null;
            int? irArgc = calls.Length == 1 ? calls[0].Operand1 : null;
            irEvidence = new
            {
                main = irMain.Name, loadCount = loads.Length, receiver = irReceiver,
                callCount = calls.Length, target = irTarget, argc = irArgc,
                opcodes = instructions.Select(instruction => instruction.Opcode.ToString()).ToArray()
            };
            Require(irReceiver == receiver && irTarget == target && irArgc == 1
                && instructions.Length >= 2 && ReferenceEquals(instructions[0], loads[0]) && ReferenceEquals(instructions[1], calls[0]),
                "Expected receiver immediately followed by direct receiver-only IR Call");
            stages.Add("generatedIr");

            // Compilation mutates the IR string operands into pool indices.
            var module = BytecodeCompiler.Compile(ir);
            var compiledMain = module.Functions.Single(function => function.Name == "main");
            var compiled = DecodeMain(compiledMain);
            compiledEvidence = compiled;
            Require(compiled.Receiver == receiver && compiled.Target == target && compiled.Argc == 1,
                "Unexpected compiled call");
            stages.Add("compiled");

            using var stream = new MemoryStream();
            BytecodeSerializer.Serialize(stream, module);
            stages.Add("serialized");
            stream.Position = 0;
            var restored = BytecodeSerializer.Deserialize(stream);
            stages.Add("deserialized");
            var restoredMain = restored.Functions.Single(function => function.Name == "main");
            var restoredCall = DecodeMain(restoredMain);
            restoredEvidence = restoredCall;
            matchingInstructions = compiledMain.Instructions.SequenceEqual(restoredMain.Instructions);
            matchingStringPool = compiledMain.StringConstants.SequenceEqual(restoredMain.StringConstants, StringComparer.Ordinal);
            Require(matchingInstructions == true && matchingStringPool == true
                && compiledMain.ParameterCount == 0 && restoredMain.ParameterCount == 0
                && restoredCall.Receiver == receiver && restoredCall.Target == target && restoredCall.Argc == 1,
                "Unexpected restored call or round-trip mismatch");
            stages.Add("verifiedRoundTrip");

            var interpreter = new TypeSharp.VM.Interpreter.Interpreter();
            stages.Add("executeEntered");
            runtimeException = Record.Exception(() => { result = interpreter.Execute(restored, "main", []); });
            if (runtimeException is null)
                stages.Add("executeReturned");
        }
        catch (Exception exception)
        {
            setupException = exception;
        }

        var resultType = result?.GetType();
        var resultValue = (result as TsStringValue)?.Value;
        var resultUtf16 = resultValue is null ? null
            : string.Join(" ", resultValue.Select(unit => ((int)unit).ToString("X4")));
        output.WriteLine("C200_PADEND " + JsonSerializer.Serialize(new
        {
            source, stages, lexerErrors, parserDiagnostics, binderDiagnostics,
            bound = boundEvidence, ir = irEvidence, compiled = compiledEvidence, restored = restoredEvidence,
            matchingInstructions, matchingStringPool,
            resultType = resultType?.FullName, resultValue, resultUtf16,
            exception = Describe(runtimeException), setupException = Describe(setupException)
        }));

        Assert.True(setupException is null && runtimeException is null && resultType == typeof(TsStringValue)
            && string.Equals(resultValue, receiver, StringComparison.Ordinal) && resultUtf16 == "0041 0042 0043",
            "String.padEnd with omitted target returns unchanged TsStringValue ABC");
    }

    private static object? Describe(Exception? exception) => exception is null ? null : new
    {
        type = exception.GetType().FullName, message = exception.Message, stack = exception.StackTrace
    };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static CallEvidence DecodeMain(BytecodeFunction function)
    {
        var bytes = function.Instructions;
        var loads = new List<(int Offset, int End, int Index)>();
        var calls = new List<(int Offset, int Index, int Argc)>();
        for (int offset = 0; offset < bytes.Length;)
        {
            byte opcode = bytes[offset];
            Require(OpcodeFormats.TryGet(opcode, out var format), $"Unknown opcode at {offset}");
            int size = 1 + format.OperandBytes;
            Require(size <= bytes.Length - offset, $"Truncated opcode at {offset}");
            if (opcode == Opcodes.LoadConstString)
            {
                Require(format.Operand == OperandKind.Int32 && size == 5, "Unexpected string load format");
                loads.Add((offset, offset + size, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4))));
            }
            else if (opcode == Opcodes.Call)
            {
                Require(format.Operand == OperandKind.Int32x2 && size == 9, "Unexpected Call format");
                calls.Add((offset, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4))));
            }
            offset += size;
        }

        Require(loads.Count == 1 && calls.Count == 1, "Expected one string load and one direct bytecode Call");
        var load = loads[0];
        var call = calls[0];
        Require(load.Offset == 0 && load.End == call.Offset, "Expected entry receiver load to feed Call directly");
        var pool = function.StringConstants;
        Require((uint)load.Index < (uint)pool.Length && (uint)call.Index < (uint)pool.Length, "Invalid string pool index");
        return new CallEvidence(function.Name, loads.Count, load.Offset, load.Index, pool[load.Index],
            calls.Count, call.Offset, call.Index, call.Argc, pool[call.Index], pool, Convert.ToHexString(bytes));
    }

    private sealed record CallEvidence(string Main, int LoadCount, int ReceiverOffset, int ReceiverPoolIndex,
        string Receiver, int CallCount, int CallOffset, int TargetPoolIndex, int Argc, string Target,
        string[] StringPool, string InstructionHex);
}
