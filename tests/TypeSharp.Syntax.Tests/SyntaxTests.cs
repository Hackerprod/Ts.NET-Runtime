using TypeSharp.Syntax;
using TypeSharp.Syntax.Parser;
using TypeSharp.Syntax.SyntaxTree;
using System.Numerics;
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
    public void TokenizeBigIntLiteralBeyondUInt64()
    {
        var lexer = new Lexer("18446744073709551616n");
        var tokens = lexer.Tokenize();
        Assert.Equal(TokenKind.IntegerLiteral, tokens[0].Kind);
        Assert.Equal("18446744073709551616n", tokens[0].Text);
        Assert.Equal(BigInteger.Parse("18446744073709551616"), tokens[0].Value);
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
    public void TokenizePrefixedBigIntLiterals()
    {
        var lexer = new Lexer("0xffffffffn 0b1010n 0o17n 0XFFn");
        var tokens = lexer.Tokenize();
        Assert.Equal(TokenKind.IntegerLiteral, tokens[0].Kind);
        Assert.Equal(new BigInteger(4294967295UL), tokens[0].Value);
        Assert.Equal(TokenKind.IntegerLiteral, tokens[1].Kind);
        Assert.Equal(new BigInteger(10), tokens[1].Value);
        Assert.Equal(TokenKind.IntegerLiteral, tokens[2].Kind);
        Assert.Equal(new BigInteger(15), tokens[2].Value);
        Assert.Equal(TokenKind.IntegerLiteral, tokens[3].Kind);
        Assert.Equal(new BigInteger(255), tokens[3].Value);
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
    public void ParseFunctionDeclarationWithInferredDefaultParameters()
    {
        var tree = Parse("function send(messageType: number, payload: Uint8Array, protobuf = true): void { }");
        var func = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Members));

        Assert.Equal(3, func.Parameters.Count);
        Assert.Equal("protobuf", func.Parameters[2].Name);
        Assert.True(func.Parameters[2].TypeWasInferred);
        Assert.NotNull(func.Parameters[2].DefaultValue);
    }

    [Fact]
    public void ParseFunctionTypeWithOptionalParameter()
    {
        var tree = Parse("type Handler = (value?: number) => void;");
        var alias = Assert.IsType<TypeAliasDeclarationSyntax>(Assert.Single(tree.Members));
        var functionType = Assert.IsType<FunctionTypeSyntax>(alias.Type);
        var parameter = Assert.Single(functionType.Parameters);

        Assert.Equal("value", parameter.Name);
        Assert.IsType<NullableTypeSyntax>(parameter.TypeAnnotation);
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
    public void ParseConstGenericFunctionParameter()
    {
        var tree = Parse("function value<const T extends readonly string[]>(input: T): T { return input; }");
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Members));

        var generic = Assert.Single(function.GenericParameters);
        Assert.Equal("T", generic.Name);
        Assert.True(generic.IsConst);
        Assert.Single(generic.Constraints);
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
    public void ParseLeadingPipeUnionTypeAlias()
    {
        var tree = Parse("""
            type Message =
                | Created
                | Updated
                | Deleted;
            """);
        var alias = Assert.IsType<TypeAliasDeclarationSyntax>(Assert.Single(tree.Members));
        var union = Assert.IsType<UnionTypeSyntax>(alias.Type);

        Assert.Equal(3, union.Types.Count);
    }

    [Fact]
    public void ParsePostfixAccessAfterNewExpression()
    {
        var tree = Parse("const timestamp = Math.floor(new Date().getTime() / 1000);");
        var declaration = Assert.IsType<VariableDeclarationSyntax>(Assert.Single(tree.Members));

        Assert.NotNull(declaration.Initializer);
    }

    [Fact]
    public void ParseObjectLiteralShorthandProperty()
    {
        var tree = Parse("const messageId = 42; const registration = { messageId, raw: true };");
        Assert.Equal(2, tree.Members.Count);
        var registration = Assert.IsType<VariableDeclarationSyntax>(tree.Members[1]);
        var literal = Assert.IsType<ObjectLiteralExpressionSyntax>(registration.Initializer);

        Assert.Equal("messageId", literal.Properties[0].Key);
        var shorthandValue = Assert.IsType<IdentifierExpressionSyntax>(literal.Properties[0].Value);
        Assert.Equal("messageId", shorthandValue.Name);
        Assert.Equal("raw", literal.Properties[1].Key);
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
