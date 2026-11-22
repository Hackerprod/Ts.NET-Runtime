using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class QuotedStringLineContinuationTests(ITestOutputHelper output)
{
    private const string Expected = "0041 005A";
    private const string Stem = "function main(): string { return ";

    [Fact]
    public void DoubleQuotedPhysicalLf_ContinuesWithoutCookingNewline()
    {
        var source = Stem + "\"A" + (char)0x005C + (char)0x000A + "Z\"; }";
        var (actual, following) = Measure("lf", source,
            "66756E6374696F6E206D61696E28293A20737472696E67207B2072657475726E2022415C0A5A223B207D");
        Assert.True(string.CompareOrdinal(Expected, actual) == 0,
            $"C156_QUOTED_CONTINUATION_UTF16_MISMATCH row=lf; expected={Expected}; actual={actual}");
        Assert.Equal((2, 3, 39), (following.Line, following.Column, following.Offset));
    }

    [Fact]
    public void SingleQuotedPhysicalCrLf_ContinuesWithoutCookingNewline()
    {
        var source = Stem + "'A" + (char)0x005C + (char)0x000D + (char)0x000A + "Z'; }";
        var (actual, following) = Measure("crlf", source,
            "66756E6374696F6E206D61696E28293A20737472696E67207B2072657475726E2027415C0D0A5A273B207D");
        Assert.True(string.CompareOrdinal(Expected, actual) == 0,
            $"C156_QUOTED_CONTINUATION_UTF16_MISMATCH row=crlf; expected={Expected}; actual={actual}");
        Assert.Equal((2, 3, 40), (following.Line, following.Column, following.Offset));
    }

    private (string Actual, SourceLocation Following) Measure(string row, string source, string expectedUtf8)
    {
        var utf8 = Convert.ToHexString(Encoding.UTF8.GetBytes(source));
        Log(row, "source", new { utf8 });
        Assert.Equal(expectedUtf8, utf8);

        var tokens = new TypeSharp.Syntax.Lexer(source).Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var tree = parser.Parse();
        var binder = new Binder();
        var bound = binder.Bind(tree);
        var parserDiagnostics = parser.Diagnostics.Select(d => d.ToString()).ToArray();
        var binderDiagnostics = binder.Diagnostics.All.Select(d => d.ToString()).ToArray();
        Log(row, "diagnostics", new { parserCount = parserDiagnostics.Length, binderCount = binderDiagnostics.Length,
            parserDiagnostics, binderDiagnostics });
        Assert.Empty(parserDiagnostics);
        Assert.Empty(binderDiagnostics);

        var literal = Assert.Single(tokens, token => token.Kind == TokenKind.StringLiteral);
        var following = Assert.Single(tokens, token => token.Kind == TokenKind.Semicolon);
        var literalIndex = Array.IndexOf(tokens.ToArray(), literal);
        Assert.Same(following, tokens[literalIndex + 1]);
        Assert.Equal(";", following.Text);
        Assert.Equal("<input>", literal.Location.Source);
        Assert.Equal("<input>", following.Location.Source);
        Assert.Equal((1, 34, 33), (literal.Location.Line, literal.Location.Column, literal.Location.Offset));
        var tokenValue = Assert.IsType<string>(literal.Value);
        Log(row, "token", new { kind = literal.Kind.ToString(), text = Hex(literal.Text), value = Hex(tokenValue),
            line = literal.Location.Line, column = literal.Location.Column, offset = literal.Location.Offset });
        Log(row, "following", new { kind = following.Kind.ToString(), text = following.Text,
            line = following.Location.Line, column = following.Location.Column, offset = following.Location.Offset });

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var ops = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.Equal(new[] { Opcode.LoadConst_String, Opcode.Return }, ops.Select(op => op.Opcode).ToArray());
        var irPayload = Assert.IsType<string>(ops[0].OperandObject);
        Log(row, "ir", new { instructions = ops.Select(op => op.Opcode.ToString()).ToArray(), payload = Hex(irPayload) });

        var bytecode = BytecodeCompiler.Compile(ir);
        var compiled = Assert.Single(bytecode.Functions, function => function.Name == "main");
        var instructions = Convert.ToHexString(compiled.Instructions);
        Assert.Equal("050000000075", instructions);
        Assert.Equal(Opcodes.LoadConstString, compiled.Instructions[0]);
        var returnOffset = OpcodeFormats.InstructionSize(compiled.Instructions[0]);
        Assert.Equal(Opcodes.Return, compiled.Instructions[returnOffset]);
        Assert.Equal(compiled.Instructions.Length,
            returnOffset + OpcodeFormats.InstructionSize(compiled.Instructions[returnOffset]));
        var index = BinaryPrimitives.ReadInt32LittleEndian(compiled.Instructions.AsSpan(1, 4));
        Assert.Equal(0, index);
        var compiledPayload = Assert.Single(compiled.StringConstants);
        Log(row, "bytecode", new { instructions, index, payload = Hex(compiledPayload) });

        var value = new TypeSharp.VM.Interpreter.Interpreter().Execute(bytecode, "main", []);
        var result = Assert.IsType<TsStringValue>(value);
        Assert.Equal(result.Value, literal.Text);
        Assert.Equal(result.Value, tokenValue);
        Assert.Equal(result.Value, irPayload);
        Assert.Equal(result.Value, compiledPayload);
        var actual = Hex(result.Value);
        Log(row, "return", new { entry = "main", type = result.GetType().Name, length = result.Value.Length,
            expected = Expected, actual });
        return (actual, following.Location);
    }

    private static string Hex(string value) =>
        string.Join(" ", value.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)));

    private void Log(string row, string stage, object data) =>
        output.WriteLine($"C156 quoted-continuation {row} {stage} {JsonSerializer.Serialize(data)}");
}
