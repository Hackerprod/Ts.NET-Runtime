using TypeSharp.Syntax.Diagnostics;
using TypeSharp.Syntax.SyntaxTree;
using Xunit;

namespace TypeSharp.Syntax.Tests;

public sealed class ThrowLineTerminatorTests
{
    private const string File = "throw-boundary.ts";
    private const string Restricted = "Line break not permitted after 'throw'.";
    private const string UnexpectedSemicolon = "Unexpected token: Semicolon ';'";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void ImmediateBoundary(string newline) => CheckSplit(newline, 1, 5 + newline.Length);

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void BlockCommentBoundary(string newline) => CheckSplit("/*a" + newline + "b*/", 4, 11 + newline.Length);

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void LineCommentBoundary(string newline) => CheckSplit("// a " + newline + " ", 2, 11 + newline.Length);

    private static void CheckSplit(string trivia, int column, int offset)
    {
        var (members, diagnostics) = Parse("throw" + trivia + "value; let tail = 1;");
        var operand = Assert.IsType<ThrowStatementSyntax>(members[0]).Expression;
        var attached = operand is IdentifierExpressionSyntax name ? name.Name : operand.GetType().Name;
        Assert.Equal("diagnostics=1; members=3; attached=LiteralExpressionSyntax",
            $"diagnostics={diagnostics.Count}; members={members.Count}; attached={attached}");
        VerifyDiagnostic(Assert.Single(diagnostics), Restricted, 2, column, offset);
        Assert.Equal(3, members.Count);
        VerifyRecovered(members[0], 1, 0);
        VerifyIdentifier(Assert.IsType<ExpressionStatementSyntax>(members[1]).Expression, "value");
        VerifyTail(members[2]);
    }

    [Fact]
    public void BlockKeepsClosingBrace()
    {
        var (members, diagnostics) = Parse("{ throw\n}\nlet tail = 1;");
        VerifyDiagnostic(Assert.Single(diagnostics), Restricted, 2, 1, 8);
        Assert.Equal(2, members.Count);
        var block = Assert.IsType<BlockStatementSyntax>(members[0]);
        VerifyLocation(block.Range.End, 2, 1, 8);
        VerifyRecovered(Assert.Single(block.Statements), 3, 2);
        VerifyTail(members[1]);
    }

    [Fact]
    public void EofIsNotAnOperand()
    {
        var (members, diagnostics) = Parse("throw\r\n");
        VerifyDiagnostic(Assert.Single(diagnostics), Restricted, 2, 1, 7);
        VerifyRecovered(Assert.Single(members), 1, 0);
    }

    [Fact]
    public void SemicolonRemainsSeparateStatement()
    {
        var (members, diagnostics) = Parse("throw\n; let tail = 1;");
        VerifyDiagnostic(Assert.Single(diagnostics), Restricted, 2, 1, 6);
        Assert.Equal(3, members.Count);
        VerifyRecovered(members[0], 1, 0);
        var empty = Assert.IsType<LiteralExpressionSyntax>(Assert.IsType<ExpressionStatementSyntax>(members[1]).Expression);
        Assert.Equal(TokenKind.Semicolon, empty.Token.Kind);
        Assert.Equal(";", empty.Token.Text);
        VerifyLocation(empty.Token.Location, 2, 1, 6);
        VerifyTail(members[2]);
    }

    [Fact]
    public void ElseRemainsWithItsIf()
    {
        var (members, diagnostics) = Parse("if (flag) throw\nelse fallback; let tail = 1;");
        VerifyDiagnostic(Assert.Single(diagnostics), Restricted, 2, 1, 16);
        Assert.Equal(2, members.Count);
        var conditional = Assert.IsType<IfStatementSyntax>(members[0]);
        VerifyIdentifier(conditional.Condition, "flag");
        VerifyRecovered(conditional.ThenBranch, 11, 10);
        VerifyIdentifier(Assert.IsType<ExpressionStatementSyntax>(conditional.ElseBranch).Expression, "fallback");
        VerifyTail(members[1]);
    }

    [Fact]
    public void FollowingErrorIsNotSwallowed()
    {
        var (members, diagnostics) = Parse("throw\nvalue; throw ; let tail = 1;");
        Assert.Equal(2, diagnostics.Count);
        VerifyDiagnostic(diagnostics[0], Restricted, 2, 1, 6);
        VerifyDiagnostic(diagnostics[1], UnexpectedSemicolon, 2, 14, 19);
        Assert.Equal(4, members.Count);
        VerifyRecovered(members[0], 1, 0);
        VerifyIdentifier(Assert.IsType<ExpressionStatementSyntax>(members[1]).Expression, "value");
        Assert.Equal(TokenKind.Semicolon, Assert.IsType<LiteralExpressionSyntax>(Assert.IsType<ThrowStatementSyntax>(members[2]).Expression).Token.Kind);
        VerifyTail(members[3]);
    }

    [Theory]
    [InlineData("throw value; let tail = 1;")]
    [InlineData("throw \t\uFEFF value; let tail = 1;")]
    [InlineData("throw/* same line */value; let tail = 1;")]
    [InlineData("throw value\n; let tail = 1;")]
    [InlineData("throw value\nlet tail = 1;")]
    [InlineData("throw (\nvalue); let tail = 1;")]
    public void ValidOperandsRemainAttached(string source)
    {
        var (members, diagnostics) = Parse(source);
        Assert.Empty(diagnostics);
        Assert.Equal(2, members.Count);
        VerifyIdentifier(Assert.IsType<ThrowStatementSyntax>(members[0]).Expression, "value");
        VerifyTail(members[1]);
    }

    [Fact]
    public void OperandCanSpanLines()
    {
        var (members, diagnostics) = Parse("throw value +\nnext; let tail = 1;");
        Assert.Empty(diagnostics);
        Assert.Equal(2, members.Count);
        var sum = Assert.IsType<BinaryExpressionSyntax>(Assert.IsType<ThrowStatementSyntax>(members[0]).Expression);
        Assert.Equal(TokenKind.Plus, sum.OperatorToken.Kind);
        VerifyIdentifier(sum.Left, "value");
        VerifyIdentifier(sum.Right, "next");
        VerifyTail(members[1]);
    }

    [Fact]
    public void KeywordSpellingIsStillValidAsMemberName()
    {
        var (members, diagnostics) = Parse("obj.throw; let tail = 1;");
        Assert.Empty(diagnostics);
        Assert.Equal(2, members.Count);
        var access = Assert.IsType<MemberAccessExpressionSyntax>(Assert.IsType<ExpressionStatementSyntax>(members[0]).Expression);
        VerifyIdentifier(access.Object, "obj");
        Assert.Equal("throw", access.MemberName);
        VerifyTail(members[1]);
    }

    [Fact]
    public void SameLineMissingOperandKeepsExistingError()
    {
        var (members, diagnostics) = Parse("throw ; let tail = 1;");
        VerifyDiagnostic(Assert.Single(diagnostics), UnexpectedSemicolon, 1, 7, 6);
        Assert.Equal(2, members.Count);
        var literal = Assert.IsType<LiteralExpressionSyntax>(Assert.IsType<ThrowStatementSyntax>(members[0]).Expression);
        Assert.Equal(TokenKind.Semicolon, literal.Token.Kind);
        Assert.Equal(";", literal.Token.Text);
        VerifyTail(members[1]);
    }

    private static (List<SyntaxNode>, List<Diagnostic>) Parse(string source)
    {
        var lexer = new Lexer(source, File);
        var parser = new TypeSharp.Syntax.Parser.Parser(lexer.Tokenize());
        return (parser.Parse(File).Members, parser.Diagnostics);
    }

    private static void VerifyRecovered(SyntaxNode node, int column, int offset)
    {
        var recovered = Assert.IsType<ThrowStatementSyntax>(node);
        var placeholder = Assert.IsType<LiteralExpressionSyntax>(recovered.Expression);
        Assert.Equal(TokenKind.NullLiteral, placeholder.Token.Kind);
        Assert.Equal("", placeholder.Token.Text);
        Assert.Null(placeholder.Token.Value);
        VerifyLocation(placeholder.Token.Location, 1, column, offset);
        VerifyLocation(placeholder.Range.Start, 1, column, offset);
        VerifyLocation(placeholder.Range.End, 1, column, offset);
        VerifyLocation(recovered.Range.Start, 1, column, offset);
        VerifyLocation(recovered.Range.End, 1, column, offset);
    }

    private static void VerifyDiagnostic(Diagnostic item, string message, int line, int column, int offset)
    {
        Assert.Equal(DiagnosticSeverity.Error, item.Severity);
        Assert.Equal(DiagnosticCode.None, item.Code);
        Assert.Equal(message, item.Message);
        VerifyLocation(item.Location, line, column, offset);
    }

    private static void VerifyLocation(SourceLocation location, int line, int column, int offset)
    {
        Assert.Equal(File, location.Source);
        Assert.Equal(line, location.Line);
        Assert.Equal(column, location.Column);
        Assert.Equal(offset, location.Offset);
    }

    private static void VerifyIdentifier(ExpressionSyntax expression, string spelling)
        => Assert.Equal(spelling, Assert.IsType<IdentifierExpressionSyntax>(expression).Name);

    private static void VerifyTail(SyntaxNode node)
    {
        var declaration = Assert.IsType<VariableDeclarationSyntax>(node);
        Assert.Equal("tail", declaration.Name);
        var initializer = Assert.IsType<LiteralExpressionSyntax>(declaration.Initializer);
        Assert.Equal(TokenKind.IntegerLiteral, initializer.Token.Kind);
        Assert.Equal("1", initializer.Token.Text);
    }
}
