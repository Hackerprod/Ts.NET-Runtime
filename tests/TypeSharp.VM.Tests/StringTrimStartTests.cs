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

public sealed class StringTrimStartTests(ITestOutputHelper output)
{
    [Fact]
    public void TrimStart_RemovesOnlyLeadingEcmaWhitespace() => Check("trimStart",
        ("  \tready  ", "ready  "),
        ("\uFEFF\u00A0\u2028ready\n ", "ready\n "),
        (" \u0085\u00A0ready ", "\u0085\u00A0ready "),
        ("\t\u180E\u200Bready\uFEFF", "\u180E\u200Bready\uFEFF"),
        ("x\t y\uFEFF", "x\t y\uFEFF"),
        (" \uD83D\uDE00\uFEFF\u00A0", "\uD83D\uDE00\uFEFF\u00A0"),
        ("\t\u00A0\u2029", ""),
        ("", ""));

    [Fact]
    public void Trim_IndependentlyRemovesBothEdges() => Check("trim",
        ("\uFEFF\u00A0\u2028ready\n ", "ready"),
        (" \u0085\u00A0ready\u0085\uFEFF", "\u0085\u00A0ready\u0085"));

    private void Check(string name, params (string Input, string Expected)[] vectors)
    {
        var source = $"function main(s: string): string {{ return s.{name}(); }}";
        var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
        var tree = parser.Parse();
        var binder = new Binder();
        var bound = binder.Bind(tree);
        var parserDiagnostics = parser.Diagnostics.Select(d => d.ToString()).ToArray();
        var binderDiagnostics = binder.Diagnostics.All.Select(d => d.ToString()).ToArray();
        Log(name, "source", new { source, parserDiagnostics, binderDiagnostics });
        Assert.True(parserDiagnostics.Length == 0, "C138 clean-parser gate: " + string.Join("; ", parserDiagnostics));
        Assert.True(binderDiagnostics.Length == 0, "C138 clean-binder gate: " + string.Join("; ", binderDiagnostics));

        var function = Assert.Single(bound.Members.OfType<BoundFunctionDeclaration>());
        var body = Assert.IsType<BoundBlockStatement>(function.Body);
        var ret = Assert.IsType<BoundReturnStatement>(Assert.Single(body.Statements));
        var call = Assert.IsType<BoundCallExpression>(ret.Value);
        var member = Assert.IsType<BoundMemberAccessExpression>(call.Callee);
        var method = Assert.IsType<MethodSymbol>(member.Member);
        Log(name, "bound", new { owner = method.DeclaringClassName, member = method.Name, receiver = member.Object.Type.ToString(),
            result = call.Type.ToString(), methodResult = method.Type.ToString(), functionResult = function.Symbol.Type.ToString(),
            explicitArgs = call.Arguments.Count });
        Assert.Equal("String", method.DeclaringClassName);
        Assert.Equal(name, method.Name);
        Assert.Same(TsType.String, member.Object.Type);
        Assert.Same(TsType.String, call.Type);
        Assert.Same(TsType.String, method.Type);
        Assert.Same(TsType.String, function.Symbol.Type);
        Assert.Empty(call.Arguments);

        var target = "String::" + name;
        var ir = new IRGenerator().Generate(bound);
        var irFunction = Assert.Single(ir.Functions, f => f.Name == "main");
        var ops = irFunction.Blocks.SelectMany(block => block.Instructions).ToArray();
        Log(name, "ir", ops.Select(op => new { opcode = op.Opcode.ToString(), op.Operand0, op.Operand1, op.OperandObject }).ToArray());
        Assert.DoesNotContain(ops, op => op.Opcode is Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        var direct = Assert.Single(ops, op => op.Opcode == Opcode.Call);
        Assert.Equal(target, Assert.IsType<string>(direct.OperandObject));
        Assert.Equal(1, direct.Operand1);
        var callBlock = Assert.Single(irFunction.Blocks, block => block.Instructions.Contains(direct));
        var receiver = callBlock.Instructions[callBlock.Instructions.IndexOf(direct) - 1];
        Assert.Equal(Opcode.LoadArg, receiver.Opcode);
        Assert.Equal(0, receiver.Operand0);

        var code = BytecodeCompiler.Compile(ir);
        var encoded = Assert.Single(code.Functions, f => f.Name == "main");
        var decoded = Decode(encoded.Instructions);
        Log(name, "bytecode", new { bytes = Convert.ToHexString(encoded.Instructions), strings = encoded.StringConstants, instructions = decoded });
        Assert.DoesNotContain(decoded, op => op.Opcode is Opcodes.CallVirt or Opcodes.CallDynamic or Opcodes.CallDynamicArray);
        var native = Assert.Single(decoded, op => op.Opcode == Opcodes.Call);
        Assert.InRange(native.A, 0, encoded.StringConstants.Length - 1);
        Assert.Equal(target, encoded.StringConstants[native.A]);
        Assert.Equal(1, native.B);
        var nativeReceiver = decoded[decoded.IndexOf(native) - 1];
        Assert.Equal(Opcodes.LoadArg, nativeReceiver.Opcode);
        Assert.Equal(0, nativeReceiver.A);

        for (int i = 0; i < vectors.Length; i++)
        {
            var (input, expected) = vectors[i];
            TsValue? value;
            try
            {
                value = new TypeSharp.VM.Interpreter.Interpreter().Execute(code, "main", [TsValue.FromString(input)]);
            }
            catch (Exception error)
            {
                Log(name, "case", new { i, input = Hex(input), expected = Hex(expected), completed = false, exception = error.ToString() });
                throw;
            }
            Log(name, "case", new { i, input = Hex(input), expected = Hex(expected), actual = value is TsStringValue s ? Hex(s.Value) : null,
                wrapper = value?.GetType().Name, tag = value?.ValueType.ToString(), completed = true });
            var result = Assert.IsType<TsStringValue>(value);
            Assert.Equal(TsValueType.String, result.ValueType);
            Assert.Equal(expected, result.Value);
        }
    }

    private sealed record Instruction(int Offset, byte Opcode, int A, int B);
    private static List<Instruction> Decode(byte[] bytes)
    {
        var decoded = new List<Instruction>();
        for (int offset = 0; offset < bytes.Length;)
        {
            var opcode = bytes[offset];
            var next = offset + OpcodeFormats.InstructionSize(opcode);
            Assert.InRange(next, offset + 1, bytes.Length);
            var a = opcode is Opcodes.LoadArg or Opcodes.Call
                ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)) : 0;
            var b = opcode == Opcodes.Call ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4)) : 0;
            decoded.Add(new Instruction(offset, opcode, a, b));
            offset = next;
        }
        return decoded;
    }

    private static string Hex(string value) => string.Join(" ", value.Select(c => ((int)c).ToString("X4")));
    private void Log(string name, string phase, object data) => output.WriteLine($"C138 {name} {phase} {JsonSerializer.Serialize(data)}");
}
