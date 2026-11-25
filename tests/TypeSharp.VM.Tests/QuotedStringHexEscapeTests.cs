using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.Syntax.SyntaxTree;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;

namespace TypeSharp.VM.Tests;

public sealed class QuotedStringHexEscapeTests(ITestOutputHelper output)
{
    private const string Stem = "function main(): string { return ";
    private const string ExpectedInstructions = "050000000075";
    private sealed record Row(string Name, string Expression, string SourceUtf8, int SemicolonOffset,
        string Expected, bool Unchanged = false);

    [Fact]
    public void QuotedLiteral_CooksExactlyTwoHexDigits()
    {
        Row[] rows =
        [
            new("literal-backslash", """
                "A\\x42Z"
                """,
                "66756E6374696F6E206D61696E28293A20737472696E67207B2072657475726E2022415C5C7834325A223B207D",
                42, "0041 005C 0078 0034 0032 005A", Unchanged: true),
            new("fallback-and-escaped-delimiter", """'\q\''""",
                "66756E6374696F6E206D61696E28293A20737472696E67207B2072657475726E20275C715C27273B207D",
                39, "0071 0027", Unchanged: true),
            new("mixed-case-adjacent-nul", """'\xAf\x00'""",
                "66756E6374696F6E206D61696E28293A20737472696E67207B2072657475726E20275C7841665C783030273B207D",
                43, "00AF 0000"),
            new("two-not-three", """'\x414'""",
                "66756E6374696F6E206D61696E28293A20737472696E67207B2072657475726E20275C78343134273B207D",
                40, "0041 0034"),
            new("decoded-quote-backslash", """'\x27\x5C'""",
                "66756E6374696F6E206D61696E28293A20737472696E67207B2072657475726E20275C7832375C783543273B207D",
                43, "0027 005C"),
            new("next-named-escape", """
                "\tx\x41\n"
                """,
                "66756E6374696F6E206D61696E28293A20737472696E67207B2072657475726E20225C74785C7834315C6E223B207D",
                44, "0009 0078 0041 000A"),
            new("primary", """
                "A\x42Z"
                """,
                "66756E6374696F6E206D61696E28293A20737472696E67207B2072657475726E2022415C7834325A223B207D",
                41, "0041 0042 005A"),
        ];

        var measured = rows.Select(row => (Row: row, Actual: Measure(row))).ToArray();
        foreach (var (row, actual) in measured.Where(item => item.Row.Unchanged))
            Assert.Equal(row.Expected, actual);

        var mismatches = measured.Where(item => !item.Row.Unchanged &&
            !string.Equals(item.Row.Expected, item.Actual, StringComparison.Ordinal))
            .Select(item => $"{item.Row.Name}: expected {item.Row.Expected}, actual {item.Actual}").ToArray();
        Assert.True(mismatches.Length == 0,
            $"C213_QUOTED_STRING_HEX_ESCAPE_RESULT {string.Join("; ", mismatches)}");
    }

    private string Measure(Row row)
    {
        var source = Stem + row.Expression + "; }";
        var utf8 = Convert.ToHexString(Encoding.UTF8.GetBytes(source));
        Log(row, "source", new { utf8 });
        Assert.Equal(row.SourceUtf8, utf8);

        var tokens = new Lexer(source).Tokenize();
        var lexerErrors = tokens.Where(token => token.Kind == TokenKind.Error).Select(token => token.Text).ToArray();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        var syntax = parser.Parse();
        var parserDiagnostics = parser.Diagnostics.Select(diagnostic => diagnostic.ToString()).ToArray();
        var binder = new Binder();
        var bound = binder.Bind(syntax);
        var binderDiagnostics = binder.Diagnostics.All.Select(diagnostic => diagnostic.ToString()).ToArray();
        Log(row, "diagnostics", new { lexerErrors, parserDiagnostics, binderDiagnostics,
            lexerErrorCount = lexerErrors.Length, parserCount = parserDiagnostics.Length,
            binderCount = binderDiagnostics.Length });
        Assert.Empty(lexerErrors);
        Assert.Empty(parserDiagnostics);
        Assert.Empty(binderDiagnostics);

        var literal = Assert.Single(tokens, token => token.Kind == TokenKind.StringLiteral);
        var tokenText = Assert.IsType<string>(literal.Text);
        var tokenValue = Assert.IsType<string>(literal.Value);
        var following = Assert.Single(tokens, token => token.Kind == TokenKind.Semicolon);
        var tail = tokens.Skip(tokens.IndexOf(literal) + 1).ToArray();
        Log(row, "token", new { kind = literal.Kind.ToString(), location = Location(literal),
            text = Hex(tokenText), value = Hex(tokenValue) });
        Log(row, "following", tail.Select(token => new { kind = token.Kind.ToString(), token.Text,
            location = Location(token) }).ToArray());
        Assert.Equal("<input>", literal.Location.Source);
        Assert.Equal((1, 34, 33), (literal.Location.Line, literal.Location.Column, literal.Location.Offset));
        Assert.Equal(new[] { TokenKind.Semicolon, TokenKind.CloseBrace, TokenKind.EOF },
            tail.Select(token => token.Kind).ToArray());
        Assert.Same(following, tail[0]);
        Assert.Equal(";", following.Text);
        Assert.Equal("<input>", following.Location.Source);
        Assert.Equal((1, row.SemicolonOffset + 1, row.SemicolonOffset),
            (following.Location.Line, following.Location.Column, following.Location.Offset));
        Assert.Equal("}", tail[1].Text);
        Assert.Equal("", tail[2].Text);

        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(syntax.Members));
        Assert.Equal("main", function.Name);
        Assert.Empty(function.Parameters);
        var declaredReturn = Assert.IsType<PrimitiveTypeSyntax>(function.ReturnType);
        Assert.Equal(TokenKind.StringKeyword, declaredReturn.TypeKeyword.Kind);
        var body = Assert.IsType<BlockStatementSyntax>(function.Body);
        var ret = Assert.IsType<ReturnStatementSyntax>(Assert.Single(body.Statements));
        var retval = Assert.IsType<LiteralExpressionSyntax>(ret.Value);
        Assert.Same(literal, retval.Token);
        var boundMain = Assert.IsType<BoundFunctionDeclaration>(Assert.Single(bound.Members));
        Assert.Equal("main", boundMain.Symbol.Name);
        Assert.Same(TsType.String, boundMain.Symbol.Type);
        Assert.Empty(boundMain.Symbol.Parameters);
        Log(row, "return-syntax", new { function.Name, declaredType = declaredReturn.TypeKeyword.Text,
            literalKind = retval.Token.Kind.ToString(), boundReturnType = boundMain.Symbol.Type.ToString() });

        var ir = new IRGenerator().Generate(bound);
        var main = Assert.Single(ir.Functions, function => function.Name == "main");
        var ops = main.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.Equal(new[] { Opcode.LoadConst_String, Opcode.Return }, ops.Select(op => op.Opcode).ToArray());
        var irPayload = Assert.IsType<string>(ops[0].OperandObject);
        Log(row, "ir", new { instructions = ops.Select(op => op.Opcode.ToString()).ToArray(),
            payload = Hex(irPayload) });

        var bytecode = BytecodeCompiler.Compile(ir);
        var compiled = Assert.Single(bytecode.Functions, function => function.Name == "main");
        var compiledPayload = MeasureBytecode(row, "compiled", compiled);
        using var stream = new MemoryStream();
        BytecodeSerializer.Serialize(stream, bytecode);
        stream.Position = 0;
        var restoredModule = BytecodeSerializer.Deserialize(stream);
        var restored = Assert.Single(restoredModule.Functions, function => function.Name == "main");
        var restoredPayload = MeasureBytecode(row, "restored", restored);
        Assert.True(compiled.Instructions.SequenceEqual(restored.Instructions));
        Assert.Equal(compiledPayload, restoredPayload);

        var value = new TypeSharp.VM.Interpreter.Interpreter().Execute(restoredModule, "main", []);
        var result = Assert.IsType<TsStringValue>(value);
        var actual = Hex(result.Value);
        Log(row, "return", new { entry = "main", wrapper = result.GetType().Name, tag = result.ValueType.ToString(),
            length = result.Value.Length, expected = row.Expected, actual });
        Assert.Equal(TsValueType.String, result.ValueType);
        Assert.Equal(tokenText, result.Value);
        Assert.Equal(tokenValue, result.Value);
        Assert.Equal(irPayload, result.Value);
        Assert.Equal(restoredPayload, result.Value);
        return actual;
    }

    private string MeasureBytecode(Row row, string stage, BytecodeFunction function)
    {
        var instructions = Convert.ToHexString(function.Instructions);
        Assert.Equal(ExpectedInstructions, instructions);
        Assert.Equal(Opcodes.LoadConstString, function.Instructions[0]);
        var returnOffset = OpcodeFormats.InstructionSize(function.Instructions[0]);
        Assert.Equal(Opcodes.Return, function.Instructions[returnOffset]);
        Assert.Equal(function.Instructions.Length,
            returnOffset + OpcodeFormats.InstructionSize(function.Instructions[returnOffset]));
        var index = BinaryPrimitives.ReadInt32LittleEndian(function.Instructions.AsSpan(1, 4));
        Assert.Equal(0, index);
        var payload = Assert.Single(function.StringConstants);
        Assert.Equal(0, function.ParameterCount);
        Log(row, stage, new { instructions, index, payload = Hex(payload), function.ParameterCount,
            stringCount = function.StringConstants.Length });
        return payload;
    }

    private static object Location(Token token) =>
        new { token.Location.Source, token.Location.Line, token.Location.Column, token.Location.Offset };

    private static string Hex(string text) =>
        string.Join(" ", text.Select(unit => ((int)unit).ToString("X4", CultureInfo.InvariantCulture)));

    private void Log(Row row, string stage, object observed) =>
        output.WriteLine("C213_QUOTED_STRING_HEX_ESCAPE " +
            JsonSerializer.Serialize(new { row = row.Name, stage, observed }));
}
