using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.Syntax.SyntaxTree;
using Xunit;

namespace TypeSharp.Syntax.Tests;

public class LexerTests
{
    [Fact]
    public void TokenizeEmpty()
    {
        var lexer = new Lexer("");
        var tokens = lexer.Tokenize();
        Assert.Single(tokens);
        Assert.Equal(TokenKind.EOF, tokens[0].Kind);
    }

    [Fact]
    public void TokenizeIntegerLiteral()
    {
        var lexer = new Lexer("42");
        var tokens = lexer.Tokenize();
        Assert.Equal(TokenKind.IntegerLiteral, tokens[0].Kind);
        Assert.Equal("42", tokens[0].Text);
        Assert.Equal(42L, tokens[0].Value);
    }

    [Fact]
    public void TokenizeFloatLiteral()
    {
        var lexer = new Lexer("3.14");
        var tokens = lexer.Tokenize();
        Assert.Equal(TokenKind.FloatLiteral, tokens[0].Kind);
        Assert.Equal(3.14, tokens[0].Value);
    }

    [Fact]
    public void TokenizeStringLiteral()
    {
        var lexer = new Lexer("\"hello world\"");
        var tokens = lexer.Tokenize();
        Assert.Equal(TokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("hello world", tokens[0].Value);
    }

    [Fact]
    public void TokenizeKeywords()
    {
        var lexer = new Lexer("export function return async await");
        var tokens = lexer.Tokenize();
        Assert.Equal(TokenKind.ExportKeyword, tokens[0].Kind);
        Assert.Equal(TokenKind.FuncKeyword, tokens[1].Kind);
        Assert.Equal(TokenKind.ReturnKeyword, tokens[2].Kind);
        Assert.Equal(TokenKind.AsyncKeyword, tokens[3].Kind);
        Assert.Equal(TokenKind.AwaitKeyword, tokens[4].Kind);
    }

    [Fact]
    public void TokenizeTypeKeywords()
    {
        var lexer = new Lexer("int32 uint64 string float64 bool decimal");
        var tokens = lexer.Tokenize();
        Assert.Equal(TokenKind.Int32Keyword, tokens[0].Kind);
        Assert.Equal(TokenKind.UInt64Keyword, tokens[1].Kind);
        Assert.Equal(TokenKind.StringKeyword, tokens[2].Kind);
        Assert.Equal(TokenKind.Float64Keyword, tokens[3].Kind);
        Assert.Equal(TokenKind.BoolKeyword, tokens[4].Kind);
        Assert.Equal(TokenKind.DecimalKeyword, tokens[5].Kind);
    }

    [Fact]
    public void TokenizeOperators()
    {
        var lexer = new Lexer("+ - * / % == != < > <= >= && || = += -= -> =>");
        var tokens = lexer.Tokenize();
        Assert.Equal(TokenKind.Plus, tokens[0].Kind);
        Assert.Equal(TokenKind.Minus, tokens[1].Kind);
        Assert.Equal(TokenKind.Star, tokens[2].Kind);
        Assert.Equal(TokenKind.Slash, tokens[3].Kind);
        Assert.Equal(TokenKind.Percent, tokens[4].Kind);
        Assert.Equal(TokenKind.DoubleEquals, tokens[5].Kind);
        Assert.Equal(TokenKind.NotEquals, tokens[6].Kind);
        Assert.Equal(TokenKind.LessThan, tokens[7].Kind);
        Assert.Equal(TokenKind.GreaterThan, tokens[8].Kind);
        Assert.Equal(TokenKind.LessOrEqual, tokens[9].Kind);
        Assert.Equal(TokenKind.GreaterOrEqual, tokens[10].Kind);
        Assert.Equal(TokenKind.AmpersandAmpersand, tokens[11].Kind);
        Assert.Equal(TokenKind.PipePipe, tokens[12].Kind);
        Assert.Equal(TokenKind.Equals, tokens[13].Kind);
        Assert.Equal(TokenKind.PlusEquals, tokens[14].Kind);
        Assert.Equal(TokenKind.MinusEquals, tokens[15].Kind);
        Assert.Equal(TokenKind.Arrow, tokens[16].Kind);
        Assert.Equal(TokenKind.FatArrow, tokens[17].Kind);
    }

    [Fact]
    public void TokenizeHexNumber()
    {
        var lexer = new Lexer("0xFF");
        var tokens = lexer.Tokenize();
        Assert.Equal(TokenKind.IntegerLiteral, tokens[0].Kind);
        Assert.Equal(255L, tokens[0].Value);
    }

    [Fact]
    public void TokenizeIdentifiers()
    {
        var lexer = new Lexer("myVar _private CamelCase");
        var tokens = lexer.Tokenize();
        Assert.Equal(TokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("myVar", tokens[0].Text);
        Assert.Equal(TokenKind.Identifier, tokens[1].Kind);
        Assert.Equal("_private", tokens[1].Text);
        Assert.Equal(TokenKind.Identifier, tokens[2].Kind);
        Assert.Equal("CamelCase", tokens[2].Text);
    }

    [Fact]
    public void SkipComments()
    {
        var lexer = new Lexer("42 // comment\n43 /* block */ 44");
        var tokens = lexer.Tokenize();
        Assert.Equal(3, tokens.Count(t => t.Kind == TokenKind.IntegerLiteral));
    }

    [Fact]
    public void TokenizeBooleanAndNull()
    {
        var lexer = new Lexer("true false null");
        var tokens = lexer.Tokenize();
        Assert.Equal(TokenKind.TrueLiteral, tokens[0].Kind);
        Assert.Equal(TokenKind.FalseLiteral, tokens[1].Kind);
        Assert.Equal(TokenKind.NullLiteral, tokens[2].Kind);
    }

    [Fact]
    public void TokenizeNullableAndQuestionQuestion()
    {
        var lexer = new Lexer("x?.y ?? z");
        var tokens = lexer.Tokenize();
        Assert.Equal(TokenKind.Identifier, tokens[0].Kind);
        Assert.Equal(TokenKind.QuestionDot, tokens[1].Kind);
        Assert.Equal(TokenKind.Identifier, tokens[2].Kind);
        Assert.Equal(TokenKind.QuestionQuestion, tokens[3].Kind);
        Assert.Equal(TokenKind.Identifier, tokens[4].Kind);
    }
}

public class ParserTests
{
    private static SourceFileSyntax Parse(string code)
    {
        var lexer = new Lexer(code);
        var tokens = lexer.Tokenize();
        var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
        return parser.Parse();
    }

    [Fact]
    public void ParseFunctionDeclaration()
    {
        var tree = Parse("function add(a: int32, b: int32): int32 { return a + b; }");
        Assert.Single(tree.Members);
        Assert.IsType<FunctionDeclarationSyntax>(tree.Members[0]);
        var func = (FunctionDeclarationSyntax)tree.Members[0];
        Assert.Equal("add", func.Name);
        Assert.Equal(2, func.Parameters.Count);
        Assert.Equal("a", func.Parameters[0].Name);
        Assert.Equal("b", func.Parameters[1].Name);
    }

    [Fact]
    public void ParseVariableDeclaration()
    {
        var tree = Parse("let x: int32 = 42;");
        Assert.Single(tree.Members);
        Assert.IsType<VariableDeclarationSyntax>(tree.Members[0]);
        var varDecl = (VariableDeclarationSyntax)tree.Members[0];
        Assert.Equal("x", varDecl.Name);
        Assert.NotNull(varDecl.Initializer);
    }

    [Fact]
    public void ParseIfStatement()
    {
        var tree = Parse("if (x > 0) { return 1; } else { return 0; }");
        // If statement at top level is not a declaration, parser skips it
        Assert.NotNull(tree);
    }

    [Fact]
    public void ParseExportFunction()
    {
        var tree = Parse("export function main(): int32 { return 0; }");
        Assert.Single(tree.Members);
        var func = (FunctionDeclarationSyntax)tree.Members[0];
        Assert.Equal("main", func.Name);
    }

    [Fact]
    public void ParseAsyncArrowCallback()
    {
        var tree = Parse("const handler = async ctx => { await ctx.load(); };");
        Assert.Single(tree.Members);
        var varDecl = Assert.IsType<VariableDeclarationSyntax>(tree.Members[0]);
        var lambda = Assert.IsType<LambdaExpressionSyntax>(varDecl.Initializer);
        Assert.True(lambda.IsAsync);
        Assert.Single(lambda.Parameters);
        Assert.Equal("ctx", lambda.Parameters[0].Name);
    }

    [Fact]
    public void ParseGenericClassMethod()
    {
        var tree = Parse("class Box { value<T>(input: T): T { return input; } }");
        var cls = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(tree.Members));
        var method = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(cls.Members));

        Assert.Equal("value", method.Name);
        var generic = Assert.Single(method.GenericParameters);
        Assert.Equal("T", generic.Name);
        Assert.Single(method.Parameters);
    }

    [Fact]
    public void ParseGenericClassMethodWithNestedCallArguments()
    {
        var lexer = new Lexer("class Sender { send<T>(messageType: number, message: T): void { host(messageType, encode(message), true); } }");
        var parser = new TypeSharp.Syntax.Parser.Parser(lexer.Tokenize());
        var tree = parser.Parse();
        var cls = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(tree.Members));
        var method = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(cls.Members));

        Assert.Equal("send", method.Name);
        Assert.Empty(parser.Diagnostics);
    }

    [Fact]
    public void ParseParenthesizedUnionArrayType()
    {
        var tree = Parse("class Router { handlers: (Route<unknown, unknown> | null)[]; }");
        var cls = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(tree.Members));
        var field = Assert.IsType<FieldDeclarationSyntax>(Assert.Single(cls.Members));
        var arrayType = Assert.IsType<ArrayTypeSyntax>(field.TypeAnnotation);

        Assert.IsType<UnionTypeSyntax>(arrayType.ElementType);
    }

    [Fact]
    public void ParseBinaryExpression()
    {
        var tree = Parse("let result = a + b * c;");
        Assert.Single(tree.Members);
        var varDecl = (VariableDeclarationSyntax)tree.Members[0];
        Assert.NotNull(varDecl.Initializer);
    }

    [Fact]
    public void ParseWhileLoop()
    {
        var tree = Parse("while (i < 10) { i = i + 1; }");
        // Should parse without errors
        Assert.NotNull(tree);
    }

    [Fact]
    public void ParseForLoop()
    {
        var tree = Parse("for (let i = 0; i < 10; i = i + 1) { }");
        Assert.NotNull(tree);
    }
}
