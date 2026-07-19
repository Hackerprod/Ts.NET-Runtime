using TypeSharp.Syntax.SyntaxTree;
using TypeSharp.Syntax.Diagnostics;

namespace TypeSharp.Syntax.Parser;

public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _position;

    public List<Diagnostic> Diagnostics { get; } = new();

    public Parser(List<Token> tokens)
    {
        _tokens = tokens;
    }

    public SourceFileSyntax Parse(string fileName = "<input>")
    {
        var members = new List<SyntaxNode>();
        var start = Peek().Location;

        while (!IsAtEnd())
        {
            int loopStart = _position;
            var decl = ParseTopLevelDeclaration();
            if (decl != null)
                members.Add(decl);
            else
                members.Add(ParseStatement());
            if (_position == loopStart)
                Advance();
        }

        var end = Previous().Location;
        return new SourceFileSyntax(fileName, members, new SourceRange(start, end));
    }

    private SyntaxNode? ParseTopLevelDeclaration()
    {
        if (Check(TokenKind.ExportKeyword))
        {
            Advance();
            if (Check(TokenKind.DefaultKeyword))
                Advance();
            var decl = ParseDeclaration();
            if (decl is DeclarationSyntax declSyntax)
            {
                declSyntax.Modifiers.Add(new SyntaxToken(new Token(TokenKind.ExportKeyword, "export", Peek().Location)));
            }
            else if (decl is VariableDeclarationSyntax varDecl)
            {
                varDecl.IsExported = true;
            }
            else if (decl is VariableDeclarationListSyntax varList)
            {
                foreach (var d in varList.Declarations)
                    d.IsExported = true;
            }
            return decl;
        }

        var declaration = ParseDeclaration();
        return declaration ?? ParseStatement();
    }

    private SyntaxNode? ParseDeclaration()
    {
        var modifiers = ParseModifiers();

        return Peek().Kind switch
        {
            TokenKind.FuncKeyword => ParseFunctionDeclaration(modifiers),
            TokenKind.ClassKeyword => ParseClassDeclaration(modifiers),
            TokenKind.InterfaceKeyword => ParseInterfaceDeclaration(modifiers),
            TokenKind.EnumKeyword => ParseEnumDeclaration(modifiers),
            TokenKind.TypeKeyword => ParseTypeAlias(modifiers),
            TokenKind.ImportKeyword => ParseImportDeclaration(),
            TokenKind.LetKeyword => ParseVariableStatement(false),
            TokenKind.ConstKeyword => ParseVariableStatement(true),
            _ => null
        };
    }

    private List<SyntaxToken> ParseModifiers()
    {
        var modifiers = new List<SyntaxToken>();
        while (Check(TokenKind.PublicKeyword) || Check(TokenKind.PrivateKeyword) ||
               Check(TokenKind.ProtectedKeyword) || Check(TokenKind.StaticKeyword) ||
               Check(TokenKind.ReadonlyKeyword) || Check(TokenKind.AsyncKeyword))
        {
            modifiers.Add(new SyntaxToken(Advance()));
        }
        return modifiers;
    }

    private FunctionDeclarationSyntax ParseFunctionDeclaration(List<SyntaxToken> modifiers)
    {
        var funcKw = Advance();
        bool isAsync = modifiers.Any(m => m.Token.Kind == TokenKind.AsyncKeyword);

        var name = ExpectIdentifier();
        var genericParams = ParseGenericParameters();
        var parameters = ParseParameterList();
        var returnType = Check(TokenKind.Colon) ? (TypeSyntax?)ParseTypeAnnotation() : null;

        SyntaxNode body;
        if (Check(TokenKind.OpenBrace))
        {
            body = ParseBlock();
        }
        else
        {
            if (Check(TokenKind.Arrow) || Check(TokenKind.FatArrow)) Advance();
            else Expect(TokenKind.Arrow);
            body = new ExpressionStatementSyntax(ParseExpression(), Peek().Location);
            if (Check(TokenKind.Semicolon)) Advance();
        }

        var funcDecl = new FunctionDeclarationSyntax(name, parameters, returnType, body, isAsync,
            new SourceRange(funcKw.Location, body.Range.End));
        funcDecl.GenericParameters.AddRange(genericParams);
        return funcDecl;
    }

    private ClassDeclarationSyntax ParseClassDeclaration(List<SyntaxToken> modifiers)
    {
        var classKw = Advance();
        var name = ExpectIdentifier();
        var genericParams = ParseGenericParameters();

        TypeSyntax? baseType = null;
        if (Check(TokenKind.ExtendsKeyword))
        {
            Advance();
            baseType = ParseType();
        }
        else if (Check(TokenKind.Colon))
        {
            Advance();
            baseType = ParseType();
        }

        var implemented = new List<TypeSyntax>();
        if (Check(TokenKind.Identifier) && Peek().Text == "implements")
        {
            Advance();
            implemented.Add(ParseType());
            while (Check(TokenKind.Comma))
            {
                Advance();
                implemented.Add(ParseType());
            }
        }

        var members = ParseClassBody();
        var classDecl = new ClassDeclarationSyntax(name, members,
            new SourceRange(classKw.Location, Peek(-1).Location))
        {
            GenericParameters = genericParams,
            BaseType = baseType,
        };
        classDecl.ImplementedInterfaces.AddRange(implemented);
        return classDecl;
    }

    private InterfaceDeclarationSyntax ParseInterfaceDeclaration(List<SyntaxToken> modifiers)
    {
        var ifaceKw = Advance();
        var name = ExpectIdentifier();
        var genericParams = ParseGenericParameters();

        if (Check(TokenKind.ExtendsKeyword))
        {
            Advance();
        }

        var members = ParseClassBody();
        return new InterfaceDeclarationSyntax(name, members,
            new SourceRange(ifaceKw.Location, Peek(-1).Location))
        {
            GenericParameters = genericParams,
        };
    }

    private EnumDeclarationSyntax ParseEnumDeclaration(List<SyntaxToken> modifiers)
    {
        var enumKw = Advance();
        var name = ExpectIdentifier();
        Expect(TokenKind.OpenBrace);
        var members = new List<EnumMemberSyntax>();

        while (!Check(TokenKind.CloseBrace) && !IsAtEnd())
        {
            int loopStart = _position;
            var memberName = ExpectIdentifier();
            ExpressionSyntax? value = null;
            if (Check(TokenKind.Equals))
            {
                Advance();
                value = ParseExpression();
            }
            var loc = new SourceRange(Peek(-1).Location, Peek().Location);
            members.Add(new EnumMemberSyntax(memberName, value, loc));
            if (Check(TokenKind.Comma)) Advance();
            if (_position == loopStart) Advance();
        }

        Expect(TokenKind.CloseBrace);
        return new EnumDeclarationSyntax(name, members,
            new SourceRange(enumKw.Location, Peek(-1).Location));
    }

    private TypeAliasDeclarationSyntax ParseTypeAlias(List<SyntaxToken> modifiers)
    {
        var typeKw = Advance();
        var name = ExpectIdentifier();
        var genericParams = ParseGenericParameters();
        Expect(TokenKind.Equals);
        var type = ParseType();
        Expect(TokenKind.Semicolon);
        var alias = new TypeAliasDeclarationSyntax(name, type,
            new SourceRange(typeKw.Location, Peek(-1).Location));
        alias.GenericParameters.AddRange(genericParams);
        return alias;
    }

    private ImportDeclarationSyntax ParseImportDeclaration()
    {
        var importKw = Advance();

        bool isWildcard = false;
        var imports = new List<NamedImportSyntax>();

        if (Check(TokenKind.Star))
        {
            Advance();
            Expect(TokenKind.AsKeyword);
            var alias = ExpectIdentifier();
            imports.Add(new NamedImportSyntax("*", alias, new SourceRange(Peek(-1).Location, Peek(-1).Location)));
            isWildcard = true;
        }
        else
        {
            Expect(TokenKind.OpenBrace);
            while (!Check(TokenKind.CloseBrace) && !IsAtEnd())
            {
                int loopStart = _position;
                var impName = ExpectIdentifier();
                string? alias = null;
                if (Check(TokenKind.AsKeyword))
                {
                    Advance();
                    alias = ExpectIdentifier();
                }
                imports.Add(new NamedImportSyntax(impName, alias,
                    new SourceRange(Peek(-1).Location, Peek().Location)));
                if (Check(TokenKind.Comma)) Advance();
                if (_position == loopStart) Advance();
            }
            Expect(TokenKind.CloseBrace);
        }

        Expect(TokenKind.FromKeyword);
        var modulePath = ExpectStringLiteral();

        return new ImportDeclarationSyntax(modulePath,
            new SourceRange(importKw.Location, Peek(-1).Location))
        {
            NamedImports = imports,
            IsWildcard = isWildcard,
        };
    }

    private List<SyntaxNode> ParseClassBody()
    {
        Expect(TokenKind.OpenBrace);
        var members = new List<SyntaxNode>();

        while (!Check(TokenKind.CloseBrace) && !IsAtEnd())
        {
            int loopStart = _position;
            var member = ParseClassMember();
            if (member != null) members.Add(member);
            if (_position == loopStart) Advance();
        }

        Expect(TokenKind.CloseBrace);
        return members;
    }

    private SyntaxNode? ParseClassMember()
    {
        var mods = ParseModifiers();

        if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.OpenParen)
        {
            if (Peek().Text == "constructor")
                return ParseConstructor(mods);
            return ParseMethodOrProperty(mods);
        }

        if (IsMemberNameToken(Peek().Kind))
        {
            return ParseMethodOrProperty(mods);
        }

        if (mods.Count > 0)
            return null;

        Advance();
        return null;
    }

    private ConstructorDeclarationSyntax ParseConstructor(List<SyntaxToken> mods)
    {
        var name = ExpectIdentifier();
        var parameters = ParseParameterList();
        var body = ParseBlock();
        return new ConstructorDeclarationSyntax(parameters, body, body.Range);
    }

    private SyntaxNode ParseMethodOrProperty(List<SyntaxToken> mods)
    {
        var name = ExpectMemberName();
        var genericParams = ParseGenericParameters();

        if (Check(TokenKind.OpenParen))
        {
            var parameters = ParseParameterList();
            var returnType = Check(TokenKind.Colon) ? (TypeSyntax?)ParseTypeAnnotation() : null;
            SyntaxNode? body = Check(TokenKind.OpenBrace) ? ParseBlock() : null;
            if (body == null && Check(TokenKind.Semicolon)) Advance();

            bool isAsync = mods.Any(m => m.Token.Kind == TokenKind.AsyncKeyword);
            var range = new SourceRange(Peek(-1).Location, Peek().Location);
            var method = new MethodDeclarationSyntax(name, parameters, returnType, body, isAsync, range);
            method.GenericParameters.AddRange(genericParams);
            return method;
        }

        if (genericParams.Count > 0)
        {
            Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "Generic parameters are only valid on methods",
                Peek(-1).Location));
        }

        bool fieldOptional = false;
        if (Check(TokenKind.Question) && Peek(1).Kind == TokenKind.Colon)
        {
            fieldOptional = true;
            Advance();
        }
        TypeSyntax? type = null;
        if (Check(TokenKind.Colon))
        {
            Advance();
            type = ParseType();
            if (fieldOptional && type is not NullableTypeSyntax)
                type = new NullableTypeSyntax(type, type.Range);
        }
        ExpressionSyntax? initializer = null;
        if (Check(TokenKind.Equals))
        {
            Advance();
            initializer = ParseExpression();
        }
        if (Check(TokenKind.Semicolon)) Advance();
        var fieldRange = new SourceRange(Peek(-1).Location, Peek().Location);

        bool isReadonly = mods.Any(m => m.Token.Kind == TokenKind.ReadonlyKeyword);
        return new FieldDeclarationSyntax(name, type!, initializer, fieldRange, isReadonly);
    }

    private List<ParameterSyntax> ParseParameterList()
    {
        Expect(TokenKind.OpenParen);
        var parameters = new List<ParameterSyntax>();

        while (!Check(TokenKind.CloseParen) && !IsAtEnd())
        {
            int loopStart = _position;
            bool isPropertyParameter = false;
            while (Check(TokenKind.PrivateKeyword) || Check(TokenKind.PublicKeyword) ||
                   Check(TokenKind.ProtectedKeyword) || Check(TokenKind.ReadonlyKeyword))
            {
                isPropertyParameter = true;
                Advance();
            }
            var name = ExpectMemberName();
            bool optional = false;
            if (Check(TokenKind.Question) && Peek(1).Kind == TokenKind.Colon)
            {
                optional = true;
                Advance();
            }
            var type = ParseTypeAnnotation();
            if (optional && type is not NullableTypeSyntax)
                type = new NullableTypeSyntax(type, type.Range);
            ExpressionSyntax? defaultVal = null;
            if (Check(TokenKind.Equals))
            {
                Advance();
                defaultVal = ParseExpression();
            }
            var paramRange = new SourceRange(Peek(-1).Location, Peek().Location);
            parameters.Add(new ParameterSyntax(name, type, defaultVal, paramRange)
            {
                IsPropertyParameter = isPropertyParameter
            });
            if (Check(TokenKind.Comma)) Advance();
            // Malformed input must not stall the cursor; skip the bad token.
            if (_position == loopStart) Advance();
        }

        Expect(TokenKind.CloseParen);
        return parameters;
    }

    private List<GenericParameterSyntax> ParseGenericParameters()
    {
        var result = new List<GenericParameterSyntax>();
        if (!Check(TokenKind.LessThan)) return result;

        Advance();
        while (!Check(TokenKind.GreaterThan) && !IsAtEnd())
        {
            int loopStart = _position;
            var name = ExpectIdentifier();
            var param = new GenericParameterSyntax(name, Peek().Location);
            if (Check(TokenKind.ExtendsKeyword))
            {
                Advance();
                param.Constraints.Add(ParseType());
            }
            result.Add(param);
            if (Check(TokenKind.Comma)) Advance();
            if (_position == loopStart) Advance();
        }
        Expect(TokenKind.GreaterThan);
        return result;
    }

    // Type parsing
    public TypeSyntax ParseType()
    {
        var type = ParseTypeWithSuffixes();

        if (Check(TokenKind.Pipe))
        {
            var members = new List<TypeSyntax> { type };
            while (Check(TokenKind.Pipe))
            {
                Advance();
                members.Add(ParseTypeWithSuffixes());
            }
            type = new UnionTypeSyntax(members,
                new SourceRange(members[0].Range.Start, members[^1].Range.End));
        }

        if (Check(TokenKind.Question))
        {
            Advance();
            type = new NullableTypeSyntax(type, new SourceRange(type.Range.Start, Peek(-1).Location));
        }

        return type;
    }

    private TypeSyntax ParseTypeWithSuffixes()
    {
        var type = ParsePrimaryType();

        while (Check(TokenKind.OpenBracket) && Peek(1).Kind == TokenKind.CloseBracket)
        {
            Advance();
            Advance();
            type = new ArrayTypeSyntax(type, new SourceRange(type.Range.Start, Peek(-1).Location));
        }

        return type;
    }

    private TypeSyntax ParsePrimaryType()
    {
        if (Check(TokenKind.OpenBrace))
        {
            return ParseMapType();
        }

        if (Check(TokenKind.OpenParen))
        {
            if (IsFunctionTypeAhead())
                return ParseFunctionType();

            Advance();
            var innerType = ParseType();
            Expect(TokenKind.CloseParen);
            return innerType;
        }

        if (Check(TokenKind.OpenBracket))
        {
            // [T] (array shorthand) or tuple [A, B, …]. Tuples surface as
            // arrays: homogeneous element type when members agree, any otherwise.
            Advance();
            var members = new List<TypeSyntax> { ParseType() };
            while (Check(TokenKind.Comma))
            {
                Advance();
                members.Add(ParseType());
            }
            Expect(TokenKind.CloseBracket);
            if (members.Count == 1)
                return new ArrayTypeSyntax(members[0], members[0].Range);
            return new TupleTypeSyntax(members,
                new SourceRange(members[0].Range.Start, members[^1].Range.End));
        }

        if (Check(TokenKind.NullLiteral))
        {
            var nullTok = Advance();
            return new NamedTypeSyntax("null", nullTok.Location);
        }

        if (Check(TokenKind.ConstKeyword))
        {
            var constTok = Advance();
            return new NamedTypeSyntax("const", constTok.Location);
        }

        // `new (…) => T` constructor types parse as plain function types.
        if (Check(TokenKind.NewKeyword) && Peek(1).Kind == TokenKind.OpenParen)
        {
            Advance();
            return ParseFunctionType();
        }

        if (Check(TokenKind.AwaitKeyword) || Check(TokenKind.PromiseKeyword))
        {
            Advance();
            Expect(TokenKind.LessThan);
            var elemType = ParseType();
            Expect(TokenKind.GreaterThan);
            return new PromiseTypeSyntax(elemType, elemType.Range);
        }

        if (IsTypeKeyword(Peek().Kind))
        {
            var token = Advance();
            return new PrimitiveTypeSyntax(token, new SourceRange(token.Location, token.Location));
        }

        return ParseNamedType();
    }

    private static bool IsTypeKeyword(TokenKind kind) => kind switch
    {
        TokenKind.VoidKeyword or TokenKind.BoolKeyword or
        TokenKind.Int8Keyword or TokenKind.UInt8Keyword or
        TokenKind.Int16Keyword or TokenKind.UInt16Keyword or
        TokenKind.Int32Keyword or TokenKind.UInt32Keyword or
        TokenKind.Int64Keyword or TokenKind.UInt64Keyword or
        TokenKind.Float32Keyword or TokenKind.Float64Keyword or
        TokenKind.DecimalKeyword or TokenKind.BigintKeyword or
        TokenKind.StringKeyword or TokenKind.BytesKeyword or
        TokenKind.DateTimeKeyword or TokenKind.GuidKeyword or
        TokenKind.NumberKeyword or TokenKind.AnyKeyword => true,
        _ => false
    };

    private TypeSyntax ParseFunctionType()
    {
        // (a: T, b: U) => R
        var open = Expect(TokenKind.OpenParen);
        var parameters = new List<ParameterSyntax>();
        while (!Check(TokenKind.CloseParen) && !IsAtEnd())
        {
            int loopStart = _position;
            var name = ExpectMemberName();
            bool typeWasInferred = !Check(TokenKind.Colon);
            TypeSyntax paramType = !typeWasInferred
                ? ParseTypeAnnotation()
                : AnyType(Peek().Location);
            parameters.Add(new ParameterSyntax(name, paramType, null,
                new SourceRange(Peek(-1).Location, Peek().Location),
                typeWasInferred));
            if (Check(TokenKind.Comma)) Advance();
            if (_position == loopStart) Advance();
        }
        Expect(TokenKind.CloseParen);
        Expect(TokenKind.FatArrow);
        var returnType = ParseType();
        return new FunctionTypeSyntax(parameters, returnType,
            new SourceRange(open.Location, Peek(-1).Location));
    }

    private bool IsFunctionTypeAhead()
    {
        if (!Check(TokenKind.OpenParen))
            return false;

        var depth = 0;
        for (var i = _position; i < _tokens.Count; i++)
        {
            var kind = _tokens[i].Kind;
            if (kind == TokenKind.OpenParen)
            {
                depth++;
                continue;
            }

            if (kind == TokenKind.CloseParen)
            {
                depth--;
                if (depth == 0)
                    return i + 1 < _tokens.Count && _tokens[i + 1].Kind == TokenKind.FatArrow;
            }
        }

        return false;
    }

    private static PrimitiveTypeSyntax AnyType(SourceLocation location) =>
        new(new Token(TokenKind.AnyKeyword, "any", location), new SourceRange(location, location));


    private TypeSyntax ParseMapType()
    {
        // `{K: V}` (legacy map shorthand, single type-keyword key) or an
        // anonymous object type `{ a: T; b?: U }`.
        var open = Expect(TokenKind.OpenBrace);

        if (IsTypeKeyword(Peek().Kind) && Peek(1).Kind == TokenKind.Colon)
        {
            var keyType = ParseType();
            Expect(TokenKind.Colon);
            var valueType = ParseType();
            Expect(TokenKind.CloseBrace);
            return new MapTypeSyntax(keyType, valueType, new SourceRange(keyType.Range.Start, Peek(-1).Location));
        }

        var members = new List<ObjectTypeMemberSyntax>();
        while (!Check(TokenKind.CloseBrace) && !IsAtEnd())
        {
            int loopStart = _position;
            var name = ExpectMemberName();
            bool optional = false;
            if (Check(TokenKind.Question))
            {
                optional = true;
                Advance();
            }
            Expect(TokenKind.Colon);
            var memberType = ParseType();
            members.Add(new ObjectTypeMemberSyntax(name, memberType, optional));
            if (Check(TokenKind.Semicolon) || Check(TokenKind.Comma)) Advance();
            if (_position == loopStart) Advance();
        }
        Expect(TokenKind.CloseBrace);
        return new ObjectTypeSyntax(members, new SourceRange(open.Location, Peek(-1).Location));
    }

    private TypeSyntax ParseNamedType()
    {
        var name = ExpectIdentifier();
        var type = new NamedTypeSyntax(name, Peek(-1).Location);

        if (Check(TokenKind.LessThan))
        {
            Advance();
            while (!Check(TokenKind.GreaterThan) && !IsAtEnd())
            {
                int loopStart = _position;
                type.TypeArguments.Add(ParseType());
                if (Check(TokenKind.Comma)) Advance();
                if (_position == loopStart) Advance();
            }
            Expect(TokenKind.GreaterThan);
        }

        return type;
    }

    private TypeSyntax ParseTypeAnnotation()
    {
        Expect(TokenKind.Colon);
        return ParseType();
    }

    // Statement parsing
    public SyntaxNode ParseStatement()
    {
        return Peek().Kind switch
        {
            TokenKind.OpenBrace => ParseBlock(),
            TokenKind.FuncKeyword => ParseFunctionDeclaration(new List<SyntaxToken>()),
            TokenKind.ReturnKeyword => ParseReturnStatement(),
            TokenKind.IfKeyword => ParseIfStatement(),
            TokenKind.WhileKeyword => ParseWhileStatement(),
            TokenKind.ForKeyword => ParseForStatement(),
            TokenKind.ThrowKeyword => ParseThrowStatement(),
            TokenKind.TryKeyword => ParseTryStatement(),
            TokenKind.LetKeyword => ParseVariableStatement(false),
            TokenKind.ConstKeyword => ParseVariableStatement(true),
            TokenKind.Semicolon => new ExpressionStatementSyntax(
                new LiteralExpressionSyntax(Advance(), Peek().Location), Peek().Location),
            _ => ParseExpressionStatement(),
        };
    }

    private BlockStatementSyntax ParseBlock()
    {
        var openBrace = Advance();
        var statements = new List<SyntaxNode>();

        while (!Check(TokenKind.CloseBrace) && !IsAtEnd())
        {
            int loopStart = _position;
            statements.Add(ParseStatement());
            if (_position == loopStart) Advance();
        }

        var closeBrace = Expect(TokenKind.CloseBrace);
        return new BlockStatementSyntax(statements, new SourceRange(openBrace.Location, closeBrace.Location));
    }

    private ReturnStatementSyntax ParseReturnStatement()
    {
        var returnKw = Advance();
        ExpressionSyntax? value = null;
        // ASI: a line break after `return` terminates the statement.
        if (!Check(TokenKind.Semicolon) && !Check(TokenKind.CloseBrace) && !IsAtEnd() &&
            Peek().Location.Line == returnKw.Location.Line)
        {
            value = ParseExpression();
        }
        if (Check(TokenKind.Semicolon)) Advance();
        return new ReturnStatementSyntax(value, new SourceRange(returnKw.Location, Peek(-1).Location));
    }

    private IfStatementSyntax ParseIfStatement()
    {
        var ifKw = Advance();
        Expect(TokenKind.OpenParen);
        var condition = ParseExpression();
        Expect(TokenKind.CloseParen);
        var thenBranch = ParseStatement();
        StatementSyntax? elseBranch = null;
        if (Check(TokenKind.ElseKeyword))
        {
            Advance();
            var elseStmt = ParseStatement();
            elseBranch = ToStatement(elseStmt);
        }
        return new IfStatementSyntax(condition, ToStatement(thenBranch), elseBranch,
            new SourceRange(ifKw.Location, Peek(-1).Location));
    }

    private WhileStatementSyntax ParseWhileStatement()
    {
        var whileKw = Advance();
        Expect(TokenKind.OpenParen);
        var condition = ParseExpression();
        Expect(TokenKind.CloseParen);
        var body = ToStatement(ParseStatement());
        return new WhileStatementSyntax(condition, body, new SourceRange(whileKw.Location, Peek(-1).Location));
    }

    private StatementSyntax ParseForStatement()
    {
        var forKw = Advance();
        Expect(TokenKind.OpenParen);

        // for (const x of iterable) { ... }
        if ((Check(TokenKind.LetKeyword) || Check(TokenKind.ConstKeyword)) &&
            IsMemberNameToken(Peek(1).Kind) && Peek(2).Kind == TokenKind.OfKeyword)
        {
            bool isConst = Check(TokenKind.ConstKeyword);
            Advance();
            var variable = Advance().Text;
            Advance(); // of
            var iterable = ParseExpression();
            Expect(TokenKind.CloseParen);
            var ofBody = ToStatement(ParseStatement());
            return new ForOfStatementSyntax(variable, isConst, iterable, ofBody,
                new SourceRange(forKw.Location, Peek(-1).Location));
        }

        SyntaxNode? initializer = null;
        if (!Check(TokenKind.Semicolon))
        {
            initializer = ParseVariableStatement(false);
        }
        else
        {
            Advance();
        }

        ExpressionSyntax? condition = null;
        if (!Check(TokenKind.Semicolon))
        {
            condition = ParseExpression();
        }
        Advance();

        ExpressionSyntax? iterator = null;
        if (!Check(TokenKind.CloseParen))
        {
            iterator = ParseExpression();
        }
        Expect(TokenKind.CloseParen);

        var body = ToStatement(ParseStatement());
        return new ForStatementSyntax(initializer, condition, iterator, body,
            new SourceRange(forKw.Location, Peek(-1).Location));
    }

    private ThrowStatementSyntax ParseThrowStatement()
    {
        var throwKw = Advance();
        var expr = ParseExpression();
        if (Check(TokenKind.Semicolon)) Advance();
        return new ThrowStatementSyntax(expr, new SourceRange(throwKw.Location, Peek(-1).Location));
    }

    private TryStatementSyntax ParseTryStatement()
    {
        var tryKw = Advance();
        var tryBlock = ParseBlock();

        string? catchVar = null;
        TypeSyntax? catchType = null;
        BlockStatementSyntax? catchBlock = null;

        if (Check(TokenKind.CatchKeyword))
        {
            Advance();
            if (Check(TokenKind.OpenParen))
            {
                Advance();
                catchVar = ExpectIdentifier();
                if (Check(TokenKind.Colon))
                {
                    Advance();
                    catchType = ParseType();
                }
                Expect(TokenKind.CloseParen);
            }
            catchBlock = ParseBlock();
        }

        BlockStatementSyntax? finallyBlock = null;
        if (Check(TokenKind.FinallyKeyword))
        {
            Advance();
            finallyBlock = ParseBlock();
        }

        return new TryStatementSyntax(tryBlock, catchVar, catchType, catchBlock, finallyBlock,
            new SourceRange(tryKw.Location, Peek(-1).Location));
    }

    private SyntaxNode ParseVariableStatement(bool isConst)
    {
        var letKw = Advance();
        var declarations = new List<VariableDeclarationSyntax>();

        while (true)
        {
            var name = ExpectMemberName();
            TypeSyntax? typeAnn = null;
            if (Check(TokenKind.Colon))
            {
                typeAnn = ParseTypeAnnotation();
            }

            ExpressionSyntax? initializer = null;
            if (Check(TokenKind.Equals))
            {
                Advance();
                initializer = ParseExpression();
            }

            declarations.Add(new VariableDeclarationSyntax(name, typeAnn, initializer, isConst,
                new SourceRange(letKw.Location, Peek(-1).Location)));

            if (Check(TokenKind.Comma))
            {
                Advance();
                continue;
            }
            break;
        }

        if (Check(TokenKind.Semicolon)) Advance();

        if (declarations.Count == 1)
            return declarations[0];
        return new VariableDeclarationListSyntax(declarations,
            new SourceRange(letKw.Location, Peek(-1).Location));
    }

    private ExpressionStatementSyntax ParseExpressionStatement()
    {
        var expr = ParseExpression();
        if (Check(TokenKind.Semicolon)) Advance();
        return new ExpressionStatementSyntax(expr, expr.Range);
    }

    private static StatementSyntax ToStatement(SyntaxNode node)
    {
        if (node is StatementSyntax stmt) return stmt;
        return new ExpressionStatementSyntax(
            new LiteralExpressionSyntax(new Token(TokenKind.Nop, "", node.Range.Start), node.Range),
            node.Range);
    }

    // Expression parsing
    public ExpressionSyntax ParseExpression()
    {
        return ParseAssignment();
    }

    private ExpressionSyntax ParseAssignment()
    {
        var left = ParseTernary();

        if (Check(TokenKind.Equals) || Check(TokenKind.PlusEquals) ||
            Check(TokenKind.MinusEquals) || Check(TokenKind.StarEquals) ||
            Check(TokenKind.SlashEquals) || Check(TokenKind.PercentEquals) ||
            Check(TokenKind.AmpersandEquals) || Check(TokenKind.PipeEquals) ||
            Check(TokenKind.CaretEquals) || Check(TokenKind.ShiftLeftEquals) ||
            Check(TokenKind.ShiftRightEquals) || Check(TokenKind.StarStarEquals) ||
            Check(TokenKind.AmpersandAmpersandEquals) || Check(TokenKind.PipePipeEquals) ||
            Check(TokenKind.QuestionQuestionEquals))
        {
            var op = Advance();
            var right = ParseAssignment();
            return new AssignmentExpressionSyntax(left, op, right, new SourceRange(left.Range.Start, right.Range.End));
        }

        return left;
    }

    private ExpressionSyntax ParseTernary()
    {
        var cond = ParseNullishCoalescing();

        if (Check(TokenKind.Question))
        {
            Advance();
            var whenTrue = ParseExpression();
            Expect(TokenKind.Colon);
            var whenFalse = ParseExpression();
            return new ConditionalExpressionSyntax(cond, whenTrue, whenFalse,
                new SourceRange(cond.Range.Start, whenFalse.Range.End));
        }

        return cond;
    }

    private ExpressionSyntax ParseNullishCoalescing()
    {
        var left = ParseOr();
        while (Check(TokenKind.QuestionQuestion))
        {
            var op = Advance();
            var right = ParseOr();
            left = new BinaryExpressionSyntax(left, op, right, new SourceRange(left.Range.Start, right.Range.End));
        }
        return left;
    }

    private ExpressionSyntax ParseOr()
    {
        var left = ParseAnd();
        while (Check(TokenKind.PipePipe))
        {
            var op = Advance();
            var right = ParseAnd();
            left = new BinaryExpressionSyntax(left, op, right, new SourceRange(left.Range.Start, right.Range.End));
        }
        return left;
    }

    private ExpressionSyntax ParseAnd()
    {
        var left = ParseBitwiseOr();
        while (Check(TokenKind.AmpersandAmpersand))
        {
            var op = Advance();
            var right = ParseBitwiseOr();
            left = new BinaryExpressionSyntax(left, op, right, new SourceRange(left.Range.Start, right.Range.End));
        }
        return left;
    }

    private ExpressionSyntax ParseBitwiseOr()
    {
        var left = ParseBitwiseXor();
        while (Check(TokenKind.Pipe))
        {
            var op = Advance();
            var right = ParseBitwiseXor();
            left = new BinaryExpressionSyntax(left, op, right, new SourceRange(left.Range.Start, right.Range.End));
        }
        return left;
    }

    private ExpressionSyntax ParseBitwiseXor()
    {
        var left = ParseBitwiseAnd();
        while (Check(TokenKind.Caret))
        {
            var op = Advance();
            var right = ParseBitwiseAnd();
            left = new BinaryExpressionSyntax(left, op, right, new SourceRange(left.Range.Start, right.Range.End));
        }
        return left;
    }

    private ExpressionSyntax ParseBitwiseAnd()
    {
        var left = ParseEquality();
        while (Check(TokenKind.Ampersand))
        {
            var op = Advance();
            var right = ParseEquality();
            left = new BinaryExpressionSyntax(left, op, right, new SourceRange(left.Range.Start, right.Range.End));
        }
        return left;
    }

    private ExpressionSyntax ParseEquality()
    {
        var left = ParseComparison();
        while (Check(TokenKind.DoubleEquals) || Check(TokenKind.TripleEquals) ||
               Check(TokenKind.StrictNotEquals) || Check(TokenKind.NotEquals))
        {
            var op = Advance();
            var right = ParseComparison();
            left = new BinaryExpressionSyntax(left, op, right, new SourceRange(left.Range.Start, right.Range.End));
        }
        return left;
    }

    private ExpressionSyntax ParseComparison()
    {
        var left = ParseShift();
        while (Check(TokenKind.LessThan) || Check(TokenKind.GreaterThan) ||
               Check(TokenKind.LessOrEqual) || Check(TokenKind.GreaterOrEqual) ||
               Check(TokenKind.InKeyword) || Check(TokenKind.InstanceofKeyword))
        {
            var op = Advance();
            var right = ParseShift();
            left = new BinaryExpressionSyntax(left, op, right, new SourceRange(left.Range.Start, right.Range.End));
        }
        return left;
    }

    private ExpressionSyntax ParseShift()
    {
        var left = ParseAdditive();
        while (Check(TokenKind.ShiftLeft) || Check(TokenKind.ShiftRight) ||
               Check(TokenKind.ShiftRightUnsigned))
        {
            var op = Advance();
            var right = ParseAdditive();
            left = new BinaryExpressionSyntax(left, op, right, new SourceRange(left.Range.Start, right.Range.End));
        }
        return left;
    }

    private ExpressionSyntax ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Check(TokenKind.Plus) || Check(TokenKind.Minus))
        {
            var op = Advance();
            var right = ParseMultiplicative();
            left = new BinaryExpressionSyntax(left, op, right, new SourceRange(left.Range.Start, right.Range.End));
        }
        return left;
    }

    private ExpressionSyntax ParseMultiplicative()
    {
        var left = ParseExponent();
        while (Check(TokenKind.Star) || Check(TokenKind.Slash) || Check(TokenKind.Percent))
        {
            var op = Advance();
            var right = ParseExponent();
            left = new BinaryExpressionSyntax(left, op, right, new SourceRange(left.Range.Start, right.Range.End));
        }
        return left;
    }

    private ExpressionSyntax ParseExponent()
    {
        var left = ParseUnary();
        if (Check(TokenKind.StarStar))
        {
            var op = Advance();
            var right = ParseExponent(); // right-associative
            return new BinaryExpressionSyntax(left, op, right, new SourceRange(left.Range.Start, right.Range.End));
        }
        return left;
    }

    private ExpressionSyntax ParseUnary()
    {
        if (Check(TokenKind.Minus) || Check(TokenKind.Plus) ||
            Check(TokenKind.Bang) || Check(TokenKind.Tilde) ||
            Check(TokenKind.PlusPlus) || Check(TokenKind.MinusMinus))
        {
            var op = Advance();
            var operand = ParseUnary();
            return new UnaryExpressionSyntax(op, operand, true, new SourceRange(op.Location, operand.Range.End));
        }

        if (Check(TokenKind.Identifier) && Peek().Text == "typeof")
        {
            var typeofTok = Advance();
            var operand = ParseUnary();
            return new TypeofExpressionSyntax(operand, new SourceRange(typeofTok.Location, operand.Range.End));
        }

        if (Check(TokenKind.AwaitKeyword))
        {
            var awaitKw = Advance();
            var expr = ParseUnary();
            return new AwaitExpressionSyntax(expr, new SourceRange(awaitKw.Location, expr.Range.End));
        }

        if (Check(TokenKind.NewKeyword))
        {
            var newKw = Advance();
            var type = ParseType();
            Expect(TokenKind.OpenParen);
            var args = ParseArguments();
            Expect(TokenKind.CloseParen);
            return new NewExpressionSyntax(type, args, new SourceRange(newKw.Location, Peek(-1).Location));
        }

        return ParsePostfix();
    }

    private ExpressionSyntax ParsePostfix()
    {
        var expr = ParsePrimary();

        while (true)
        {
            if (Check(TokenKind.OpenParen))
            {
                Advance();
                var args = ParseArguments();
                Expect(TokenKind.CloseParen);
                expr = new CallExpressionSyntax(expr, args, new SourceRange(expr.Range.Start, Peek(-1).Location));
            }
            else if (Check(TokenKind.Dot))
            {
                Advance();
                var member = ExpectMemberName();
                expr = new MemberAccessExpressionSyntax(expr, member, false,
                    new SourceRange(expr.Range.Start, Peek(-1).Location));
            }
            else if (Check(TokenKind.QuestionDot))
            {
                Advance();
                var member = ExpectMemberName();
                expr = new MemberAccessExpressionSyntax(expr, member, true,
                    new SourceRange(expr.Range.Start, Peek(-1).Location));
            }
            else if (Check(TokenKind.OpenBracket))
            {
                Advance();
                var index = ParseExpression();
                Expect(TokenKind.CloseBracket);
                expr = new IndexExpressionSyntax(expr, index,
                    new SourceRange(expr.Range.Start, Peek(-1).Location));
            }
            else if (Check(TokenKind.PlusPlus) || Check(TokenKind.MinusMinus))
            {
                var op = Advance();
                expr = new UnaryExpressionSyntax(op, expr, false,
                    new SourceRange(expr.Range.Start, Peek(-1).Location));
            }
            else if (Check(TokenKind.AsKeyword))
            {
                Advance();
                var castType = ParseType();
                expr = new AsExpressionSyntax(expr, castType,
                    new SourceRange(expr.Range.Start, Peek(-1).Location));
            }
            else if (Check(TokenKind.Bang))
            {
                // Postfix non-null assertion `expr!`. A bare Bang can never
                // continue a binary expression (`!=`/`!==` lex as one token).
                Advance();
                expr = new NonNullAssertionSyntax(expr,
                    new SourceRange(expr.Range.Start, Peek(-1).Location));
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    private ExpressionSyntax ParsePrimary()
    {
        if (Check(TokenKind.AsyncKeyword))
        {
            var asyncArrow = TryParseAsyncArrowFunction();
            if (asyncArrow != null)
                return asyncArrow;
        }

        if (Check(TokenKind.IntegerLiteral) || Check(TokenKind.FloatLiteral) ||
            Check(TokenKind.StringLiteral) || Check(TokenKind.TrueLiteral) ||
            Check(TokenKind.FalseLiteral) || Check(TokenKind.NullLiteral))
        {
            var token = Advance();
            return new LiteralExpressionSyntax(token, new SourceRange(token.Location, Peek(-1).Location));
        }

        if (Check(TokenKind.TemplateLiteral))
        {
            return ParseTemplateLiteral(Advance());
        }

        if (Check(TokenKind.ThisKeyword))
        {
            var token = Advance();
            return new ThisExpressionSyntax(new SourceRange(token.Location, Peek(-1).Location));
        }

        if (Check(TokenKind.SuperKeyword))
        {
            var token = Advance();
            return new SuperExpressionSyntax(new SourceRange(token.Location, Peek(-1).Location));
        }

        if (Check(TokenKind.Identifier))
        {
            // Single-parameter arrow shorthand: x => body
            if (Peek(1).Kind == TokenKind.FatArrow)
            {
                var paramToken = Advance();
                Advance(); // =>
                var lambdaParams = new List<ParameterSyntax>
                {
                    new(paramToken.Text, AnyType(paramToken.Location), null,
                        new SourceRange(paramToken.Location, paramToken.Location),
                        typeWasInferred: true)
                };
                var lambdaBody = ParseArrowBody();
                return new LambdaExpressionSyntax(lambdaParams, null, lambdaBody, false,
                    new SourceRange(paramToken.Location, lambdaBody.Range.End));
            }

            var name = ExpectIdentifier();
            return new IdentifierExpressionSyntax(name, new SourceRange(Peek(-1).Location, Peek(-1).Location));
        }

        if (Check(TokenKind.OpenParen))
        {
            var arrow = TryParseArrowFunction();
            if (arrow != null)
                return arrow;

            Advance();
            var expr = ParseExpression();
            Expect(TokenKind.CloseParen);
            return expr;
        }

        // Generic arrow: `<T>(arr: T[]) => …`. `<` can never start any other
        // primary expression, so this probe is unambiguous.
        if (Check(TokenKind.LessThan))
        {
            var genericArrow = TryParseGenericArrowFunction();
            if (genericArrow != null)
                return genericArrow;
        }

        if (Check(TokenKind.OpenBracket))
        {
            return ParseArrayLiteral();
        }

        if (Check(TokenKind.OpenBrace))
        {
            return ParseObjectLiteral();
        }

        // Keyword-shaped names (`number`, `type`, …) used as plain identifiers.
        if (IsMemberNameToken(Peek().Kind))
        {
            var keywordTok = Advance();
            return new IdentifierExpressionSyntax(keywordTok.Text,
                new SourceRange(keywordTok.Location, keywordTok.Location));
        }

        var errorToken = Advance();
        Diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error,
            $"Unexpected token: {errorToken.Kind} '{errorToken.Text}'",
            errorToken.Location));
        return new LiteralExpressionSyntax(errorToken, new SourceRange(errorToken.Location, errorToken.Location));
    }

    private ExpressionSyntax ParseArrayLiteral()
    {
        var openBracket = Advance();
        var elements = new List<ExpressionSyntax>();

        while (!Check(TokenKind.CloseBracket) && !IsAtEnd())
        {
            int loopStart = _position;
            elements.Add(ParseExpression());
            if (Check(TokenKind.Comma)) Advance();
            if (_position == loopStart) Advance();
        }

        Expect(TokenKind.CloseBracket);
        return new ArrayLiteralExpressionSyntax(elements,
            new SourceRange(openBracket.Location, Peek(-1).Location));
    }

    // Attempts `(params) [: ReturnType] => body`. Restores position and drops
    // any speculative diagnostics when the parenthesized run is not an arrow.
    private ExpressionSyntax? TryParseArrowFunction()
        => TryParseArrowFunction(isAsync: false);

    private ExpressionSyntax? TryParseArrowFunction(bool isAsync)
    {
        int savedPosition = _position;
        int savedDiagnostics = Diagnostics.Count;

        var open = Advance(); // (
        var parameters = new List<ParameterSyntax>();
        bool wellFormed = true;

        while (!Check(TokenKind.CloseParen) && !IsAtEnd())
        {
            if (!IsMemberNameToken(Peek().Kind))
            {
                wellFormed = false;
                break;
            }
            var nameToken = Advance();
            bool typeWasInferred = !Check(TokenKind.Colon);
            TypeSyntax paramType = !typeWasInferred
                ? ParseTypeAnnotation()
                : AnyType(nameToken.Location);
            ExpressionSyntax? defaultVal = null;
            if (Check(TokenKind.Equals))
            {
                Advance();
                defaultVal = ParseExpression();
            }
            parameters.Add(new ParameterSyntax(nameToken.Text, paramType, defaultVal,
                new SourceRange(nameToken.Location, Peek(-1).Location),
                typeWasInferred));
            if (Check(TokenKind.Comma))
            {
                Advance();
                continue;
            }
            break;
        }

        if (wellFormed && Check(TokenKind.CloseParen))
        {
            Advance();
            TypeSyntax? returnType = null;
            if (Check(TokenKind.Colon))
                returnType = ParseTypeAnnotation();

            if (Check(TokenKind.FatArrow))
            {
                Advance();
                var body = ParseArrowBody();
                return new LambdaExpressionSyntax(parameters, returnType, body, isAsync,
                    new SourceRange(open.Location, body.Range.End));
            }
        }

        _position = savedPosition;
        if (Diagnostics.Count > savedDiagnostics)
            Diagnostics.RemoveRange(savedDiagnostics, Diagnostics.Count - savedDiagnostics);
        return null;
    }

    private ExpressionSyntax? TryParseAsyncArrowFunction()
    {
        int savedPosition = _position;
        int savedDiagnostics = Diagnostics.Count;
        var asyncToken = Advance();

        if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.FatArrow)
        {
            var paramToken = Advance();
            Advance(); // =>
            var lambdaParams = new List<ParameterSyntax>
            {
                new(paramToken.Text, AnyType(paramToken.Location), null,
                    new SourceRange(paramToken.Location, paramToken.Location),
                    typeWasInferred: true)
            };
            var lambdaBody = ParseArrowBody();
            return new LambdaExpressionSyntax(lambdaParams, null, lambdaBody, true,
                new SourceRange(asyncToken.Location, lambdaBody.Range.End));
        }

        if (Check(TokenKind.OpenParen))
        {
            var arrow = TryParseArrowFunction(isAsync: true);
            if (arrow != null)
                return arrow;
        }

        if (Check(TokenKind.LessThan))
        {
            var genericArrow = TryParseGenericArrowFunction(isAsync: true);
            if (genericArrow != null)
                return genericArrow;
        }

        _position = savedPosition;
        if (Diagnostics.Count > savedDiagnostics)
            Diagnostics.RemoveRange(savedDiagnostics, Diagnostics.Count - savedDiagnostics);
        return null;
    }

    // Splits a template literal into text/`${expr}` segments and lowers it to
    // a string concatenation chain. Placeholder expressions are lexed and
    // parsed with the real pipeline, so any expression form works inside.
    private ExpressionSyntax ParseTemplateLiteral(Token template)
    {
        string raw = template.Value as string ?? template.Text;
        var range = new SourceRange(template.Location, template.Location);

        ExpressionSyntax? result = null;
        void Append(ExpressionSyntax part)
        {
            result = result == null
                ? part
                : new BinaryExpressionSyntax(result,
                    new Token(TokenKind.Plus, "+", template.Location), part, range);
        }

        int position = 0;
        while (position < raw.Length)
        {
            int placeholder = raw.IndexOf("${", position, StringComparison.Ordinal);
            if (placeholder < 0)
            {
                Append(TextPart(raw[position..]));
                break;
            }

            if (placeholder > position)
                Append(TextPart(raw[position..placeholder]));

            int depth = 1;
            int scan = placeholder + 2;
            while (scan < raw.Length && depth > 0)
            {
                if (raw[scan] == '{') depth++;
                else if (raw[scan] == '}') depth--;
                if (depth > 0) scan++;
            }

            string inner = raw[(placeholder + 2)..scan];
            var innerTokens = new Lexer(inner, template.Location.Source).Tokenize();
            var innerParser = new Parser(innerTokens);
            var innerExpr = innerParser.ParseExpression();
            Diagnostics.AddRange(innerParser.Diagnostics);
            Append(new TemplatePartSyntax(innerExpr, range));

            position = scan + 1;
        }

        return result ?? TextPart("");

        ExpressionSyntax TextPart(string text) =>
            new LiteralExpressionSyntax(
                new Token(TokenKind.StringLiteral, text, template.Location, text), range);
    }

    private ExpressionSyntax? TryParseGenericArrowFunction()
        => TryParseGenericArrowFunction(isAsync: false);

    private ExpressionSyntax? TryParseGenericArrowFunction(bool isAsync)
    {
        int savedPosition = _position;
        int savedDiagnostics = Diagnostics.Count;

        var generics = ParseGenericParameters();
        if (generics.Count > 0 && Check(TokenKind.OpenParen))
        {
            var arrow = TryParseArrowFunction(isAsync);
            if (arrow is LambdaExpressionSyntax lambda)
            {
                lambda.GenericParameters.AddRange(generics);
                return lambda;
            }
        }

        _position = savedPosition;
        if (Diagnostics.Count > savedDiagnostics)
            Diagnostics.RemoveRange(savedDiagnostics, Diagnostics.Count - savedDiagnostics);
        return null;
    }

    private SyntaxNode ParseArrowBody()
    {
        if (Check(TokenKind.OpenBrace))
            return ParseBlock();
        var expr = ParseExpression();
        return new ReturnStatementSyntax(expr, expr.Range);
    }

    private ObjectLiteralExpressionSyntax ParseObjectLiteral()
    {
        var openBrace = Advance();
        var properties = new List<ObjectPropertySyntax>();

        while (!Check(TokenKind.CloseBrace) && !IsAtEnd())
        {
            int loopStart = _position;
            var key = ExpectMemberName();
            Expect(TokenKind.Colon);
            var value = ParseExpression();
            var propRange = new SourceRange(Peek(-1).Location, Peek().Location);
            properties.Add(new ObjectPropertySyntax(key, value, propRange));
            if (Check(TokenKind.Comma)) Advance();
            if (_position == loopStart) Advance();
        }

        var closeBrace = Expect(TokenKind.CloseBrace);
        return new ObjectLiteralExpressionSyntax(properties,
            new SourceRange(openBrace.Location, closeBrace.Location));
    }

    private List<ExpressionSyntax> ParseArguments()
    {
        var args = new List<ExpressionSyntax>();
        if (Check(TokenKind.CloseParen)) return args;

        while (!IsAtEnd())
        {
            args.Add(ParseExpression());
            if (Check(TokenKind.Comma)) Advance();
            else break;
        }
        return args;
    }

    // Helpers
    private bool Check(TokenKind kind) => _position < _tokens.Count && Peek().Kind == kind;

    private Token Peek(int offset = 0)
    {
        int idx = _position + offset;
        return idx >= 0 && idx < _tokens.Count ? _tokens[idx] : new Token(TokenKind.EOF, "", new SourceLocation("", 0, 0, 0));
    }

    private Token Advance()
    {
        var token = Peek();
        if (_position < _tokens.Count) _position++;
        return token;
    }

    private bool IsAtEnd() => _position >= _tokens.Count || Peek().Kind == TokenKind.EOF;

    private Token Previous() => _position > 0 ? _tokens[_position - 1] : _tokens[0];

    private string ExpectIdentifier()
    {
        if (!Check(TokenKind.Identifier))
        {
            Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"Expected identifier, got {Peek().Kind}",
                Peek().Location));
            return Peek().Text;
        }
        return Advance().Text;
    }

    // Member positions (object keys, `.name`, class/interface members) accept
    // keyword-shaped names like `number` or `type` the way TypeScript does.
    private static bool IsMemberNameToken(TokenKind kind) =>
        kind == TokenKind.Identifier || IsTypeKeyword(kind) ||
        kind is TokenKind.TypeKeyword or TokenKind.FromKeyword or TokenKind.AsKeyword or
        TokenKind.OfKeyword or TokenKind.MatchKeyword;

    private string ExpectMemberName()
    {
        if (!IsMemberNameToken(Peek().Kind))
        {
            Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"Expected identifier, got {Peek().Kind}",
                Peek().Location));
            return Peek().Text;
        }
        return Advance().Text;
    }

    private Token Expect(TokenKind kind)
    {
        if (!Check(kind))
        {
            Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"Expected {kind}, got {Peek().Kind} '{Peek().Text}'",
                Peek().Location));
            return Peek();
        }
        return Advance();
    }

    private string ExpectStringLiteral()
    {
        if (!Check(TokenKind.StringLiteral))
        {
            Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                $"Expected string literal, got {Peek().Kind}",
                Peek().Location));
            return "";
        }
        return (string)(Advance().Value ?? "");
    }
}
