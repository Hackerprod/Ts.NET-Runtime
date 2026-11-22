using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class QuotedStringControlEscapeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("double-b", """
        "A\bZ"
        """, "0041 0008 005A")]
    [InlineData("single-f", """'A\fZ'""", "0041 000C 005A")]
    [InlineData("double-v", """
        "A\vZ"
        """, "0041 000B 005A")]
    [InlineData("single-mixed", """'A\b\f\vZ'""", "0041 0008 000C 000B 005A")]
    [InlineData("existing-controls", """
        "A\n\r\t\0Z"
        """, "0041 000A 000D 0009 0000 005A")]
    [InlineData("literal-backslash", """'A\\bZ'""", "0041 005C 0062 005A")]
    public void QuotedLiteralReturnsExactUtf16Units(string row, string expression, string expected)
    {
        var source = $"function main(): string {{ return {expression}; }}";
        var tokens = new TypeSharp.Syntax.Lexer(source).Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var tree = parser.Parse();
        var binder = new Binder();
        var bound = binder.Bind(tree);
        var parserDiagnostics = parser.Diagnostics.Select(d => d.ToString()).ToArray();
        var binderDiagnostics = binder.Diagnostics.All.Select(d => d.ToString()).ToArray();
        Log(row, "diagnostics", new { source, parserDiagnostics, binderDiagnostics,
            parserCount = parserDiagnostics.Length, binderCount = binderDiagnostics.Length });
        Assert.Empty(parserDiagnostics);
        Assert.Empty(binderDiagnostics);

        var literal = Assert.Single(tokens, token => token.Kind == TokenKind.StringLiteral);
        var tokenValue = Assert.IsType<string>(literal.Value);
        Log(row, "token", new { text = Hex(literal.Text), value = Hex(tokenValue) });

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var ops = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.DoesNotContain(ops, op => op.Opcode is Opcode.Call or Opcode.CallVirt or Opcode.CallDynamic or Opcode.CallDynamicArray);
        var constant = Assert.Single(ops, op => op.Opcode == Opcode.LoadConst_String);
        var payload = Assert.IsType<string>(constant.OperandObject);
        Log(row, "ir", new { payload = Hex(payload), instructions = ops.Select(op => op.Opcode.ToString()).ToArray() });

        var bytecode = BytecodeCompiler.Compile(ir);
        var compiled = Assert.Single(bytecode.Functions, function => function.Name == "main");
        var loads = new List<int>();
        var opcodes = new List<byte>();
        for (int offset = 0; offset < compiled.Instructions.Length;)
        {
            var opcode = compiled.Instructions[offset];
            var next = offset + OpcodeFormats.InstructionSize(opcode);
            Assert.InRange(next, offset + 1, compiled.Instructions.Length);
            opcodes.Add(opcode);
            if (opcode == Opcodes.LoadConstString)
                loads.Add(BinaryPrimitives.ReadInt32LittleEndian(compiled.Instructions.AsSpan(offset + 1, 4)));
            offset = next;
        }
        Assert.DoesNotContain(opcodes, op => op is Opcodes.Call or Opcodes.CallVirt or Opcodes.CallDynamic or Opcodes.CallDynamicArray);
        var index = Assert.Single(loads);
        Assert.InRange(index, 0, compiled.StringConstants.Length - 1);
        Log(row, "bytecode", new { index, payload = Hex(compiled.StringConstants[index]),
            instructions = Convert.ToHexString(compiled.Instructions) });

        var value = new TypeSharp.VM.Interpreter.Interpreter().Execute(bytecode, "main", []);
        var result = Assert.IsType<TsStringValue>(value);
        var actual = Hex(result.Value);
        Log(row, "return", new { entry = "main", type = result.GetType().Name, length = result.Value.Length,
            expected, actual });
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"C139_QUOTED_ESCAPE_UTF16_MISMATCH row={row}; expected={expected}; actual={actual}");
    }

    private static string Hex(string value) =>
        string.Join(" ", value.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)));

    private void Log(string row, string phase, object data) =>
        output.WriteLine($"C139 {row} {phase} {JsonSerializer.Serialize(data)}");
}
