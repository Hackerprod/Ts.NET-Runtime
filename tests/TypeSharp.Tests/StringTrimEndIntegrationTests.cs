using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeSharp.Hosting;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.VM.Bytecode;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.Tests;

public sealed class StringTrimEndIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ascii")]
    [InlineData("mixed")]
    [InlineData("nel-stop")]
    [InlineData("supplementary")]
    [InlineData("unchanged")]
    [InlineData("all")]
    [InlineData("empty")]
    public async Task TrimEnd_RemovesOnlyTrailingEcmaWhitespace(string row)
    {
        var (input, expected) = row switch
        {
            "ascii" => (" \tready \t", " \tready"),
            "mixed" => (" \tleft\t right\u00A0\r\n\u2028\u2029\uFEFF", " \tleft\t right"),
            "nel-stop" => (" item\u00A0\u0085\uFEFF", " item\u00A0\u0085"),
            "supplementary" => (" \t\U0001F600\uFEFF", " \t\U0001F600"),
            "unchanged" => (" item", " item"),
            "all" => (" \t\u00A0\uFEFF", ""),
            "empty" => ("", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
        const string source = "export function trimTrailing(input: string): string { return input.trimEnd(); }\n";
        var dir = Directory.CreateTempSubdirectory("typesharp-trim-end-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "app.ts"), source, new UTF8Encoding(false));
            var parser = new TypeSharp.Syntax.Parser.Parser(new TypeSharp.Syntax.Lexer(source).Tokenize());
            var tree = parser.Parse();
            var parserDiagnostics = parser.Diagnostics.Select(d => d.ToString()).ToArray();
            var binder = new Binder();
            var bound = binder.Bind(tree);
            var binderDiagnostics = binder.Diagnostics.All.Select(d => new { code = d.Code.ToString(), text = d.ToString() }).ToArray();
            Log(row, "source", new { source, parserDiagnostics, binderDiagnostics, input = Hex(input), expected = Hex(expected) });
            Assert.Empty(parserDiagnostics);
            await using var runtime = await BuildAsync(row, dir.FullName);
            Assert.Empty(binderDiagnostics);
            CheckNativeShape(row, bound);
            await ObserveAsync(row, runtime, "trimTrailing", input, expected);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExistingTrimAndTrimStart_KeepTheirOwnDirections()
    {
        const string source = "export function trimBoth(input: string): string { return input.trim(); }\n" +
            "export function trimLeading(input: string): string { return input.trimStart(); }\n";
        const string input = "\uFEFF ready \u00A0";
        var dir = Directory.CreateTempSubdirectory("typesharp-trim-control-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "app.ts"), source, new UTF8Encoding(false));
            Log("control", "source", new { source });
            await using var runtime = await BuildAsync("control", dir.FullName);
            await ObserveAsync("control", runtime, "trimBoth", input, "ready");
            await ObserveAsync("control", runtime, "trimLeading", input, "ready \u00A0");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private async Task<TypeSharpRuntime> BuildAsync(string row, string directory)
    {
        Log(row, "hosting-build-start", new { directory });
        try
        {
            var runtime = await new TypeSharpRuntimeBuilder().AddSourceDirectory(directory).BuildAsync();
            Log(row, "hosting-build-success", new { module = "app" });
            return runtime;
        }
        catch (Exception error)
        {
            Log(row, "hosting-build-failure", new { type = error.GetType().FullName, error = error.ToString() });
            throw;
        }
    }

    private async Task ObserveAsync(string row, TypeSharpRuntime runtime, string name, string input, string expected)
    {
        Log(row, "invoke-start", new { name, input = Hex(input), expected = Hex(expected) });
        string actual;
        try
        {
            actual = await runtime.InvokeAsync<string>("app", name, input);
        }
        catch (Exception error)
        {
            Log(row, "invoke-failure", new { name, type = error.GetType().FullName, error = error.ToString() });
            throw;
        }
        Log(row, "invoke-success", new { name, input = Hex(input), expected = Hex(expected), actual = Hex(actual) });
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal));
    }

    private void CheckNativeShape(string name, BoundSourceFile bound)
    {
        const string memberName = "trimEnd";
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
        Assert.Equal(memberName, method.Name);
        Assert.False(method.IsStatic);
        Assert.Same(TsType.String, member.Object.Type);
        Assert.Same(TsType.String, call.Type);
        Assert.Same(TsType.String, method.Type);
        Assert.Same(TsType.String, function.Symbol.Type);
        Assert.Empty(call.Arguments);

        var target = "String::" + memberName;
        var ir = new IRGenerator().Generate(bound);
        var irFunction = Assert.Single(ir.Functions, f => f.Name == "trimTrailing");
        var ops = irFunction.Blocks.SelectMany(block => block.Instructions).ToArray();
        Log(name, "ir", ops.Select(op => new { opcode = op.Opcode.ToString(), op.Operand0, op.Operand1, op.OperandObject }).ToArray());
        Assert.DoesNotContain(ops, op => op.Opcode is Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        var direct = Assert.Single(ops, op => op.Opcode == Opcode.Call);
        Assert.Equal(target, Assert.IsType<string>(direct.OperandObject));
        Assert.Equal(1, direct.Operand1);
        var callBlock = Assert.Single(irFunction.Blocks, block => block.Instructions.Contains(direct));
        var callIndex = callBlock.Instructions.IndexOf(direct);
        Assert.True(callIndex > 0);
        var receiver = callBlock.Instructions[callIndex - 1];
        Assert.Equal(Opcode.LoadArg, receiver.Opcode);
        Assert.Equal(0, receiver.Operand0);

        var code = BytecodeCompiler.Compile(ir);
        var encoded = Assert.Single(code.Functions, f => f.Name == "trimTrailing");
        var decoded = Decode(encoded.Instructions);
        Log(name, "bytecode", new { bytes = Convert.ToHexString(encoded.Instructions), strings = encoded.StringConstants, instructions = decoded });
        Assert.DoesNotContain(decoded, op => op.Opcode is Opcodes.CallVirt or Opcodes.CallDynamic or Opcodes.CallDynamicArray);
        var native = Assert.Single(decoded, op => op.Opcode == Opcodes.Call);
        Assert.InRange(native.A, 0, encoded.StringConstants.Length - 1);
        Assert.Equal(target, encoded.StringConstants[native.A]);
        Assert.Equal(1, native.B);
        var nativeIndex = decoded.IndexOf(native);
        Assert.True(nativeIndex > 0);
        var nativeReceiver = decoded[nativeIndex - 1];
        Assert.Equal(Opcodes.LoadArg, nativeReceiver.Opcode);
        Assert.Equal(0, nativeReceiver.A);

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
            if (opcode is Opcodes.LoadArg or Opcodes.Call)
                Assert.True(next - offset >= (opcode == Opcodes.Call ? 9 : 5));
            var a = opcode is Opcodes.LoadArg or Opcodes.Call
                ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1, 4)) : 0;
            var b = opcode == Opcodes.Call ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4)) : 0;
            decoded.Add(new Instruction(offset, opcode, a, b));
            offset = next;
        }
        return decoded;
    }

    private static string Hex(string value) => string.Join(" ", value.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)));
    private void Log(string name, string phase, object data) => output.WriteLine($"C146 {name} {phase} {JsonSerializer.Serialize(data)}");
}
