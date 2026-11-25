using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Diagnostics;
using TypeSharp.Syntax.SyntaxTree;
using TypeSharp.VM.Bytecode;
using TypeSharp.VM.Memory;
using Xunit;
using Xunit.Abstractions;
using SyntaxParser = TypeSharp.Syntax.Parser.Parser;

namespace TypeSharp.VM.Tests;

public sealed class LeadingDotDecimalLiteralTests(ITestOutputHelper output)
{
    private const string Source = "function main(): number { return .5; }";
    private const string FileName = "leading-dot-decimal.ts";
    private const string HalfBits = "3fe0000000000000";

    [Fact]
    public void LeadingDotDecimal_ProducesNativeFloat64()
    {
        var mismatches = new List<string>();
        var tokens = new Lexer(Source, FileName).Tokenize();
        var lexerErrors = tokens.Count(token => token.Kind == TokenKind.Error);
        var parser = new SyntaxParser(tokens);
        var syntax = parser.Parse(FileName);
        var parserErrors = parser.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        var probes = new List<object>();
        const string notRun = "not-run: parser errors";
        object ast = notRun, bound = notRun, ir = notRun, bytecode = notRun, vm = notRun;
        bool? baselineWitness = null;

        Token[] prefix =
        [
            Expected(TokenKind.FuncKeyword, "function", 0), Expected(TokenKind.Identifier, "main", 9),
            Expected(TokenKind.OpenParen, "(", 13), Expected(TokenKind.CloseParen, ")", 14),
            Expected(TokenKind.Colon, ":", 15), Expected(TokenKind.NumberKeyword, "number", 17),
            Expected(TokenKind.OpenBrace, "{", 24), Expected(TokenKind.ReturnKeyword, "return", 26)
        ];
        Token[] suffix =
        [
            Expected(TokenKind.Semicolon, ";", 35), Expected(TokenKind.CloseBrace, "}", 37), End(38)
        ];

        Check(lexerErrors == 0, "main lexer must not emit Error");
        Check(parser.Diagnostics.Count == 0, "main parser must have zero diagnostics");
        Probe("obj.name obj?.name ...row .",
            Expected(TokenKind.Identifier, "obj", 0), Expected(TokenKind.Dot, ".", 3),
            Expected(TokenKind.Identifier, "name", 4), Expected(TokenKind.Identifier, "obj", 9),
            Expected(TokenKind.QuestionDot, "?.", 12), Expected(TokenKind.Identifier, "name", 14),
            Expected(TokenKind.DotDotDot, "...", 19), Expected(TokenKind.Identifier, "row", 22),
            Expected(TokenKind.Dot, ".", 26), End(27));

        if (parserErrors != 0)
        {
            Token[] redTokens =
            [
                .. prefix, Expected(TokenKind.Dot, ".", 33),
                Expected(TokenKind.IntegerLiteral, "5", 34, 5L), .. suffix
            ];
            // This is an observation of the suspected baseline, never an alternate success contract.
            baselineWitness = lexerErrors == 0 && MatchTokens(tokens, redTokens) &&
                parser.Diagnostics is [var diagnostic] && diagnostic.Severity == DiagnosticSeverity.Error &&
                diagnostic.Code == DiagnosticCode.None && diagnostic.Message == "Unexpected token: Dot '.'" &&
                MatchLocation(diagnostic.Location, Location(33));
        }
        else
        {
            Token expectedLiteral = Expected(TokenKind.FloatLiteral, ".5", 33, 0.5d);
            Token[] greenTokens = [.. prefix, expectedLiteral, .. suffix];
            Check(MatchTokens(tokens, greenTokens), "main token sequence/type/location/bits");

            var astMain = syntax.Members.Count == 1 ? syntax.Members[0] as FunctionDeclarationSyntax : null;
            var astBody = astMain?.Body as BlockStatementSyntax;
            var astReturn = astBody?.Statements is [ReturnStatementSyntax returned] ? returned : null;
            var astLiteral = astReturn?.Value as LiteralExpressionSyntax;
            var astToken = astLiteral?.Token;
            ast = new
            {
                memberCount = syntax.Members.Count, main = astMain?.Name, parameters = astMain?.Parameters.Count,
                statementCount = astBody?.Statements.Count, returnType = astReturn?.GetType().FullName,
                expressionType = astReturn?.Value?.GetType().FullName,
                token = astToken is null ? null : DescribeToken(astToken)
            };
            Check(astMain is { Name: "main" } && astMain.Parameters.Count == 0 &&
                astToken is not null && MatchToken(astToken, expectedLiteral), "sole AST main return literal");

            var binder = new Binder();
            var program = binder.Bind(syntax);
            var boundMain = program.Members.Count == 1 ? program.Members[0] as BoundFunctionDeclaration : null;
            var boundBody = boundMain?.Body as BoundBlockStatement;
            var boundReturn = boundBody?.Statements is [BoundReturnStatement returnedBound] ? returnedBound : null;
            var boundLiteral = boundReturn?.Value as BoundLiteralExpression;
            var literalType = boundLiteral?.Type as TsLiteralType;
            bool numberBase = ReferenceEquals(literalType?.BaseType, TsType.Number);
            bound = new
            {
                diagnostics = binder.Diagnostics.All.Select(DescribeDiagnostic).ToArray(),
                memberCount = program.Members.Count, main = boundMain?.Symbol.Name,
                statementCount = boundBody?.Statements.Count, expressionType = boundReturn?.Value?.GetType().FullName,
                literalType = boundLiteral?.Type.GetType().FullName, baseType = literalType?.BaseType.Name, numberBase,
                value = DescribeValue(boundLiteral?.Value), literalTypeValue = DescribeValue(literalType?.Value)
            };
            Check(binder.Diagnostics.All.Count == 0, "binder diagnostics");
            Check(boundMain?.Symbol.Name == "main" && numberBase &&
                DoubleBits(boundLiteral?.Value) == HalfBits && DoubleBits(literalType?.Value) == HalfBits,
                "sole bound main return Number literal with boxed Double bits");

            var module = new IRGenerator().Generate(program);
            var irMain = module.Functions.Where(function => function.Name == "main").ToArray();
            var irOps = irMain.SelectMany(function => function.Blocks)
                .SelectMany(block => block.Instructions).ToArray();
            var constants = irOps.Where(op => op.Opcode == Opcode.LoadConst_F64).ToArray();
            ir = new
            {
                mainCount = irMain.Length, constantCount = constants.Length,
                opcodes = irOps.Select(op => op.Opcode.ToString()).ToArray(),
                constants = constants.Select(op => DescribeValue(op.OperandObject)).ToArray()
            };
            Check(irMain.Length == 1 && constants.Length == 1 &&
                DoubleBits(constants[0].OperandObject) == HalfBits, "main IR LoadConst_F64 boxed Double bits");

            var compiled = BytecodeCompiler.Compile(module);
            using var buffer = new MemoryStream();
            BytecodeSerializer.Serialize(buffer, compiled);
            buffer.Position = 0;
            var restored = BytecodeSerializer.Deserialize(buffer);
            var restoredMain = restored.Functions.Where(function => function.Name == "main").ToArray();
            var ops = new List<object>();
            var loads = new List<string>();
            byte[]? instructions = restoredMain.Length == 1 ? restoredMain[0].Instructions : null;
            bool complete = instructions is not null;
            if (instructions is not null)
            {
                for (int offset = 0; offset < instructions.Length;)
                {
                    byte opcode = instructions[offset];
                    if (!OpcodeFormats.TryGet(opcode, out var format) ||
                        format.OperandBytes > instructions.Length - offset - 1)
                    {
                        complete = false;
                        ops.Add(new { offset, opcode, invalid = true });
                        break;
                    }

                    ops.Add(new { offset, opcode, operand = format.Operand.ToString(), format.OperandBytes });
                    if (opcode == Opcodes.LoadConstF64)
                    {
                        if (format.Operand == OperandKind.Float64 && format.OperandBytes == sizeof(double))
                            loads.Add(Hex(BinaryPrimitives.ReadInt64LittleEndian(
                                instructions.AsSpan(offset + 1, sizeof(double)))));
                        else
                            complete = false;
                    }
                    offset += 1 + format.OperandBytes;
                }
            }

            bytecode = new
            {
                mainCount = restoredMain.Length, serializedBytes = buffer.Length, complete,
                constantCount = loads.Count, operandBits = loads.ToArray(), ops,
                instructionBytes = instructions is null ? null : Convert.ToHexString(instructions)
            };
            Check(complete && loads.Count == 1 && loads[0] == HalfBits,
                "restored main bytecode LoadConstF64 operand bits");
            var result = new TypeSharp.VM.Interpreter.Interpreter().Execute(restored, "main", []);
            var actual = result as TsFloat64Value;
            vm = new { type = result?.GetType().FullName, valueType = result?.ValueType.ToString(),
                bits = actual is null ? null : DoubleBits(actual.Value) };
            Check(result?.GetType() == typeof(TsFloat64Value) && actual?.ValueType == TsValueType.Float64 &&
                DoubleBits(actual?.Value) == HalfBits, "restored VM main exact TsFloat64Value bits");

            Probe(".5.member", Expected(TokenKind.FloatLiteral, ".5", 0, 0.5d),
                Expected(TokenKind.Dot, ".", 2), Expected(TokenKind.Identifier, "member", 3), End(9));
            Probe(".5_0E+0_1;", Expected(TokenKind.FloatLiteral, ".5_0E+0_1", 0, 5d),
                Expected(TokenKind.Semicolon, ";", 9), End(10));
            foreach (string candidate in new[] { ".5n", ".5m", ".5i64", ".5u64", ".5_0u64",
                         ".5e+", ".5_", ".0x5", ".5\u0665", ".\u0665" })
            {
                Probe(candidate + ";", Expected(TokenKind.Error, candidate, 0),
                    Expected(TokenKind.Semicolon, ";", candidate.Length), End(candidate.Length + 1));
            }

            Probe(".5n-.6", Expected(TokenKind.Error, ".5n", 0), Expected(TokenKind.Minus, "-", 3),
                Expected(TokenKind.FloatLiteral, ".6", 4, 0.6d), End(6));
            Probe(".5e+-1", Expected(TokenKind.Error, ".5e+", 0), Expected(TokenKind.Minus, "-", 4),
                Expected(TokenKind.IntegerLiteral, "1", 5, 1L), End(6));
            Probe("...5", Expected(TokenKind.DotDotDot, "...", 0),
                Expected(TokenKind.IntegerLiteral, "5", 3, 5L), End(4));
        }

        output.WriteLine("C199_LEADING_DOT " + JsonSerializer.Serialize(new
        {
            source = Source, tokenCount = tokens.Count, tokens = tokens.Select(DescribeToken).ToArray(),
            lexerErrors, parserErrors, parserDiagnosticCount = parser.Diagnostics.Count,
            parserDiagnostics = parser.Diagnostics.Select(DescribeDiagnostic).ToArray(), baselineWitness,
            pipelineReached = parserErrors == 0 ? "restored-vm" : "parser",
            leadingDotProbes = parserErrors == 0 ? "run" : notRun,
            ast, bound, ir, bytecode, vm, probes, mismatches
        }));
        Assert.True(mismatches.Count == 0, "C199_LEADING_DOT_CONTRACT: " + string.Join("; ", mismatches));

        void Check(bool condition, string mismatch)
        {
            if (!condition)
                mismatches.Add(mismatch);
        }

        void Probe(string source, params Token[] expected)
        {
            var actual = new Lexer(source, FileName).Tokenize();
            bool matches = MatchTokens(actual, expected);
            probes.Add(new { source, tokenCount = actual.Count, tokens = actual.Select(DescribeToken).ToArray(),
                lexerErrors = actual.Count(token => token.Kind == TokenKind.Error), matches });
            Check(matches, "lexical probe: " + source);
        }
    }

    private static SourceLocation Location(int offset) => new(FileName, 1, offset + 1, offset);

    private static Token Expected(TokenKind kind, string text, int offset, object? value = null) =>
        new(kind, text, Location(offset), value);

    private static Token End(int offset) => Expected(TokenKind.EOF, "", offset);

    private static bool MatchTokens(IReadOnlyList<Token> actual, IReadOnlyList<Token> expected) =>
        actual.Count == expected.Count && actual.Zip(expected, MatchToken).All(matches => matches);

    private static bool MatchToken(Token actual, Token expected) =>
        actual.Kind == expected.Kind && actual.Text == expected.Text &&
        MatchLocation(actual.Location, expected.Location) && (expected.Value switch
        {
            double value => actual.Value is double found &&
                BitConverter.DoubleToInt64Bits(found) == BitConverter.DoubleToInt64Bits(value),
            long value => actual.Value is long found && found == value,
            null => actual.Value is null,
            _ => false
        });

    private static bool MatchLocation(SourceLocation actual, SourceLocation expected) =>
        actual.Source == expected.Source && actual.Line == expected.Line &&
        actual.Column == expected.Column && actual.Offset == expected.Offset;

    private static object DescribeToken(Token token) =>
        new { kind = token.Kind.ToString(), text = token.Text, location = DescribeLocation(token.Location),
            value = DescribeValue(token.Value) };

    private static object DescribeDiagnostic(Diagnostic diagnostic) =>
        new { severity = diagnostic.Severity.ToString(), code = diagnostic.Code.ToString(),
            message = diagnostic.Message, location = DescribeLocation(diagnostic.Location), diagnostic.ModuleId };

    private static object DescribeLocation(SourceLocation location) =>
        new { source = location.Source, line = location.Line, column = location.Column, offset = location.Offset };

    private static object DescribeValue(object? value) =>
        new { type = value?.GetType().FullName,
            representation = value is IFormattable formattable
                ? formattable.ToString(value is double ? "R" : null, CultureInfo.InvariantCulture)
                : value?.ToString(),
            bits = DoubleBits(value) };

    private static string? DoubleBits(object? value) =>
        value is double number ? Hex(BitConverter.DoubleToInt64Bits(number)) : null;

    private static string Hex(long bits) => bits.ToString("x16", CultureInfo.InvariantCulture);
}
