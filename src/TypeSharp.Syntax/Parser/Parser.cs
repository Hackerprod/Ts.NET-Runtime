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
        var decorators = ParseDecorators();
        var modifiers = ParseModifiers();

        return Peek().Kind switch
        {
            TokenKind.FuncKeyword => ParseFunctionDeclaration(modifiers),
            TokenKind.ClassKeyword => ParseClassDeclaration(modifiers, decorators),
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
               Check(TokenKind.ReadonlyKeyword) || Check(TokenKind.AsyncKeyword) ||
               Check(TokenKind.AbstractKeyword))
        {
            modifiers.Add(new SyntaxToken(Advance()));
        }
        return modifiers;
    }

    private List<DecoratorSyntax> ParseDecorators()
    {
        var decorators = new List<DecoratorSyntax>();
        while (Check(TokenKind.Identifier) && Peek().Text.StartsWith("@", StringComparison.Ordinal))
        {
            var start = Peek().Location;
            var name = Advance().Text[1..];
            ExpressionSyntax expression = new IdentifierExpressionSyntax(name, new SourceRange(start, start));
            while (Check(TokenKind.Dot))
            {
                Advance();
                var member = ExpectMemberName();
                expression = new MemberAccessExpressionSyntax(expression, member, false,
                    new SourceRange(start, Peek(-1).Location));
            }
            if (Check(TokenKind.OpenParen))
            {
                var args = ParseArguments();
                expression = new CallExpressionSyntax(expression, new List<TypeSyntax>(), args,
                    new SourceRange(start, Peek(-1).Location));
            }
            decorators.Add(new DecoratorSyntax(expression, new SourceRange(start, Peek(-1).Location)));
        }
        return decorators;
    }

    private FunctionDeclarationSyntax ParseFunctionDeclaration(List<SyntaxToken> modifiers)
    {
        var funcKw = Advance();
        bool isAsync = modifiers.Any(m => m.Token.Kind == TokenKind.AsyncKeyword);
        bool isGenerator = false;
        if (Check(TokenKind.Star))
        {
            Advance();
            isGenerator = true;
        }

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
            new SourceRange(funcKw.Location, body.Range.End), isGenerator);
        funcDecl.GenericParameters.AddRange(genericParams);
        return funcDecl;
    }

    private ClassDeclarationSyntax ParseClassDeclaration(List<SyntaxToken> modifiers, List<DecoratorSyntax>? decorators = null)
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
            IsAbstract = modifiers.Any(m => m.Token.Kind == TokenKind.AbstractKeyword),
        };
        if (decorators != null)
            classDecl.Decorators.AddRange(decorators);
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
        var decorators = ParseDecorators();
        var mods = ParseModifiers();
        bool isStatic = mods.Any(m => m.Token.Kind == TokenKind.StaticKeyword);
        bool isAbstract = mods.Any(m => m.Token.Kind == TokenKind.AbstractKeyword);

        if (isStatic && Check(TokenKind.OpenBrace))
        {
            var body = ParseBlock();
            return new StaticBlockSyntax(body, body.Range);
        }

        if (Check(TokenKind.OpenBracket))
            return ParseIndexSignature(mods);

        if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.OpenParen)
        {
            if (Peek().Text == "constructor")
                return ParseConstructor(mods, decorators);
            return ParseMethodOrProperty(mods, decorators);
        }

        if ((Check(TokenKind.GetKeyword) || Check(TokenKind.SetKeyword)) &&
            (IsMemberNameToken(Peek(1).Kind) || Peek(1).Kind == TokenKind.Hash))
        {
            return ParseAccessor(mods, decorators);
        }

        if (IsMemberNameToken(Peek().Kind) || Check(TokenKind.Hash))
        {
            return ParseMethodOrProperty(mods, decorators);
        }

        if (mods.Count > 0)
            return null;

        Advance();
        return null;
    }

    private ConstructorDeclarationSyntax ParseConstructor(List<SyntaxToken> mods, List<DecoratorSyntax>? decorators = null)
    {
        var name = ExpectIdentifier();
        var parameters = ParseParameterList();
        var body = ParseBlock();
        var ctor = new ConstructorDeclarationSyntax(parameters, body, body.Range);
        if (decorators != null)
            ctor.Decorators.AddRange(decorators);
        return ctor;
    }

    private (string Name, bool IsPrivateName) ParseClassMemberName()
    {
        if (Check(TokenKind.Hash))
        {
            Advance();
            var name = ExpectMemberName();
            return ($"#{name}", true);
        }
        return (ExpectMemberName(), false);
    }

    private SyntaxNode ParseAccessor(List<SyntaxToken> mods, List<DecoratorSyntax>? decorators)
    {
        var accessorToken = Advance();
        bool isGetter = accessorToken.Kind == TokenKind.GetKeyword;
        var (name, isPrivateName) = ParseClassMemberName();
        ParameterSyntax? parameter = null;
        if (Check(TokenKind.OpenParen))
        {
            var parameters = ParseParameterList();
            if (isGetter && parameters.Count != 0)
                Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "A getter cannot have parameters", accessorToken.Location));
            if (!isGetter && parameters.Count != 1)
                Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "A setter must have exactly one parameter", accessorToken.Location));
            parameter = parameters.FirstOrDefault();
        }
        else if (!isGetter)
        {
            Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "A setter requires one parameter", accessorToken.Location));
        }

        var type = Check(TokenKind.Colon) ? ParseTypeAnnotation() : null;
        SyntaxNode? body = Check(TokenKind.OpenBrace) ? ParseBlock() : null;
        if (body == null && Check(TokenKind.Semicolon)) Advance();
        var range = new SourceRange(accessorToken.Location, Peek(-1).Location);
        var accessor = new AccessorDeclarationSyntax(name, isGetter, parameter, type, body, range,
            mods.Any(m => m.Token.Kind == TokenKind.StaticKeyword),
            mods.Any(m => m.Token.Kind == TokenKind.AbstractKeyword),
            isPrivateName);
        if (decorators != null)
            accessor.Decorators.AddRange(decorators);
        return accessor;
    }

    private SyntaxNode ParseIndexSignature(List<SyntaxToken> mods)
    {
        var open = Expect(TokenKind.OpenBracket);
        var name = ExpectIdentifier();
        Expect(TokenKind.Colon);
        var keyType = ParseType();
        Expect(TokenKind.CloseBracket);
        Expect(TokenKind.Colon);
        var valueType = ParseType();
        if (Check(TokenKind.Semicolon)) Advance();
        return new IndexSignatureSyntax(name, keyType, valueType,
            mods.Any(m => m.Token.Kind == TokenKind.ReadonlyKeyword),
            new SourceRange(open.Location, Peek(-1).Location));
    }

    private SyntaxNode ParseMethodOrProperty(List<SyntaxToken> mods, List<DecoratorSyntax>? decorators = null)
    {
        var (name, isPrivateName) = ParseClassMemberName();
        var genericParams = ParseGenericParameters();

        if (Check(TokenKind.OpenParen))
        {
            var parameters = ParseParameterList();
            var returnType = Check(TokenKind.Colon) ? (TypeSyntax?)ParseTypeAnnotation() : null;
            SyntaxNode? body = Check(TokenKind.OpenBrace) ? ParseBlock() : null;
            if (body == null && Check(TokenKind.Semicolon)) Advance();

            bool isAsync = mods.Any(m => m.Token.Kind == TokenKind.AsyncKeyword);
            var range = new SourceRange(Peek(-1).Location, Peek().Location);
            var method = new MethodDeclarationSyntax(name, parameters, returnType, body, isAsync, range,
                mods.Any(m => m.Token.Kind == TokenKind.StaticKeyword),
                mods.Any(m => m.Token.Kind == TokenKind.AbstractKeyword),
                isPrivateName);
            method.GenericParameters.AddRange(genericParams);
            if (decorators != null)
                method.Decorators.AddRange(decorators);
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
        var field = new FieldDeclarationSyntax(name, type ?? AnyType(fieldRange.Start), initializer, fieldRange, isReadonly,
            mods.Any(m => m.Token.Kind == TokenKind.StaticKeyword),
            mods.Any(m => m.Token.Kind == TokenKind.AbstractKeyword),
            isPrivateName);
        if (decorators != null)
            field.Decorators.AddRange(decorators);
        return field;
    }

    private List<ParameterSyntax> ParseParameterList()
    {
        Expect(TokenKind.OpenParen);
        var parameters = new List<ParameterSyntax>();

        while (!Check(TokenKind.CloseParen) && !IsAtEnd())
        {
            int loopStart = _position;
            bool isPropertyParameter = false;
            bool isRest = false;
            while (Check(TokenKind.PrivateKeyword) || Check(TokenKind.PublicKeyword) ||
                   Check(TokenKind.ProtectedKeyword) || Check(TokenKind.ReadonlyKeyword))
            {
                isPropertyParameter = true;
                Advance();
            }
            if (Check(TokenKind.DotDotDot))
            {
                isRest = true;
                Advance();
            }
            var name = ExpectMemberName();
            var nameLocation = Peek(-1).Location;
            bool optional = false;
            if (Check(TokenKind.Question) && Peek(1).Kind == TokenKind.Colon)
            {
                optional = true;
                Advance();
            }
            bool typeWasInferred = !Check(TokenKind.Colon);
            TypeSyntax type = !typeWasInferred
                ? ParseTypeAnnotation()
                : AnyType(nameLocation);
            if (optional && type is not NullableTypeSyntax)
                type = new NullableTypeSyntax(type, type.Range);
            ExpressionSyntax? defaultVal = null;
            if (Check(TokenKind.Equals))
            {
                if (isRest)
                    Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                        "A rest parameter cannot have a default value", Peek().Location));
                Advance();
                defaultVal = ParseExpression();
            }
            var paramRange = new SourceRange(Peek(-1).Location, Peek().Location);
            parameters.Add(new ParameterSyntax(name, type, defaultVal, paramRange, typeWasInferred, isRest)
            {
                IsPropertyParameter = isPropertyParameter
            });
            if (isRest && !Check(TokenKind.CloseParen))
                Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                    "A rest parameter must be the last parameter", Peek().Location));
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
        var hasLeadingPipe = Check(TokenKind.Pipe);
        if (hasLeadingPipe)
        {
            Advance();
        }

        var type = ParseTypeWithSuffixes();

        if (hasLeadingPipe || Check(TokenKind.Pipe))
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
        var isReadonlyArray = false;
        var readonlyLocation = Peek().Location;
        if (Check(TokenKind.ReadonlyKeyword))
        {
            isReadonlyArray = true;
            readonlyLocation = Advance().Location;
        }

        var type = ParsePrimaryType();

        while (Check(TokenKind.OpenBracket))
        {
            if (Peek(1).Kind == TokenKind.CloseBracket)
            {
                Advance();
                Advance();
                type = new ArrayTypeSyntax(
                    type,
                    new SourceRange(isReadonlyArray ? readonlyLocation : type.Range.Start, Peek(-1).Location),
                    isReadonlyArray);
            }
            else
            {
                Advance();
                var indexType = ParseType();
                Expect(TokenKind.CloseBracket);
                type = new IndexedAccessTypeSyntax(
                    type,
                    indexType,
                    new SourceRange(isReadonlyArray ? readonlyLocation : type.Range.Start, Peek(-1).Location));
            }

            isReadonlyArray = false;
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

        if (Check(TokenKind.StringLiteral) ||
            Check(TokenKind.IntegerLiteral) ||
            Check(TokenKind.FloatLiteral) ||
            Check(TokenKind.TrueLiteral) ||
            Check(TokenKind.FalseLiteral))
        {
            var literal = Advance();
            return new LiteralTypeSyntax(literal, new SourceRange(literal.Location, literal.Location));
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
            bool optional = false;
            if (Check(TokenKind.Question) && Peek(1).Kind == TokenKind.Colon)
            {
                optional = true;
                Advance();
            }
            bool typeWasInferred = !Check(TokenKind.Colon);
            TypeSyntax paramType = !typeWasInferred
                ? ParseTypeAnnotation()
                : AnyType(Peek().Location);
            if (optional && paramType is not NullableTypeSyntax)
                paramType = new NullableTypeSyntax(paramType, paramType.Range);
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
            var isReadonly = false;
            if (Check(TokenKind.ReadonlyKeyword))
            {
                isReadonly = true;
                Advance();
            }
            var name = ExpectMemberName();
            bool optional = false;
            if (Check(TokenKind.Question))
            {
                optional = true;
                Advance();
            }
            Expect(TokenKind.Colon);
            var memberType = ParseType();
            members.Add(new ObjectTypeMemberSyntax(name, memberType, optional, isReadonly));
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
        if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Colon)
        {
            var label = Advance();
            Advance(); // colon
            var statement = ToStatement(ParseStatement());
            return new LabelledStatementSyntax(label.Text, statement,
                new SourceRange(label.Location, statement.Range.End));
        }

        return Peek().Kind switch
        {
            TokenKind.OpenBrace => ParseBlock(),
            TokenKind.FuncKeyword => ParseFunctionDeclaration(new List<SyntaxToken>()),
            TokenKind.ReturnKeyword => ParseReturnStatement(),
            TokenKind.YieldKeyword => ParseYieldStatement(),
            TokenKind.IfKeyword => ParseIfStatement(),
            TokenKind.SwitchKeyword => ParseSwitchStatement(),
            TokenKind.WhileKeyword => ParseWhileStatement(),
            TokenKind.DoKeyword => ParseDoWhileStatement(),
            TokenKind.ForKeyword => ParseForStatement(),
            TokenKind.BreakKeyword => ParseBreakStatement(),
            TokenKind.ContinueKeyword => ParseContinueStatement(),
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

    private YieldStatementSyntax ParseYieldStatement()
    {
        var yieldKw = Advance();
        ExpressionSyntax? value = null;
        if (!Check(TokenKind.Semicolon) && !Check(TokenKind.CloseBrace) && !IsAtEnd() &&
            Peek().Location.Line == yieldKw.Location.Line)
        {
            value = ParseExpression();
        }
        if (Check(TokenKind.Semicolon)) Advance();
        return new YieldStatementSyntax(value, new SourceRange(yieldKw.Location, Peek(-1).Location));
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

    private SwitchStatementSyntax ParseSwitchStatement()
    {
        var switchKeyword = Advance();
        Expect(TokenKind.OpenParen);
        var expression = ParseExpression();
        Expect(TokenKind.CloseParen);
        Expect(TokenKind.OpenBrace);

        var clauses = new List<SwitchClauseSyntax>();
        bool seenDefault = false;
        while (!Check(TokenKind.CloseBrace) && !IsAtEnd())
        {
            int clauseStart = _position;
            ExpressionSyntax? test;
            var start = Peek().Location;
            if (Check(TokenKind.CaseKeyword))
            {
                Advance();
                test = ParseExpression();
                Expect(TokenKind.Colon);
            }
            else if (Check(TokenKind.DefaultKeyword))
            {
                Advance();
                test = null;
                if (seenDefault)
                    Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                        "A switch statement can only contain one default clause", start));
                seenDefault = true;
                Expect(TokenKind.Colon);
            }
            else
            {
                Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                    $"Expected 'case' or 'default', got {Peek().Kind}", start));
                Advance();
                continue;
            }

            var statements = new List<SyntaxNode>();
            while (!Check(TokenKind.CaseKeyword) && !Check(TokenKind.DefaultKeyword) &&
                   !Check(TokenKind.CloseBrace) && !IsAtEnd())
            {
                int statementStart = _position;
                statements.Add(ParseStatement());
                if (_position == statementStart) Advance();
            }

            clauses.Add(new SwitchClauseSyntax(test, statements,
                new SourceRange(start, Previous().Location)));
            if (_position == clauseStart) Advance();
        }

        var closeBrace = Expect(TokenKind.CloseBrace);
        return new SwitchStatementSyntax(expression, clauses,
            new SourceRange(switchKeyword.Location, closeBrace.Location));
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

    private DoWhileStatementSyntax ParseDoWhileStatement()
    {
        var doKw = Advance();
        var body = ToStatement(ParseStatement());
        Expect(TokenKind.WhileKeyword);
        Expect(TokenKind.OpenParen);
        var condition = ParseExpression();
        Expect(TokenKind.CloseParen);
        Expect(TokenKind.Semicolon);
        return new DoWhileStatementSyntax(body, condition, new SourceRange(doKw.Location, Peek(-1).Location));
    }

    private StatementSyntax ParseForStatement()
    {
        var forKw = Advance();
        Expect(TokenKind.OpenParen);

        // for (const x of iterable) { ... } / for (const key in value) { ... }
        if ((Check(TokenKind.LetKeyword) || Check(TokenKind.ConstKeyword)) &&
            IsMemberNameToken(Peek(1).Kind) &&
            (Peek(2).Kind == TokenKind.OfKeyword || Peek(2).Kind == TokenKind.InKeyword))
        {
            bool isConst = Check(TokenKind.ConstKeyword);
            Advance();
            var variable = Advance().Text;
            var kind = Advance().Kind;
            var enumerable = ParseExpression();
            Expect(TokenKind.CloseParen);
            var loopBody = ToStatement(ParseStatement());
            return kind == TokenKind.OfKeyword
                ? new ForOfStatementSyntax(variable, isConst, enumerable, loopBody,
                    new SourceRange(forKw.Location, Peek(-1).Location))
                : new ForInStatementSyntax(variable, isConst, enumerable, loopBody,
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

    private BreakStatementSyntax ParseBreakStatement()
    {
        var keyword = Advance();
        string? label = null;
        if (Check(TokenKind.Identifier) && Peek().Location.Line == keyword.Location.Line)
            label = Advance().Text;
        if (Check(TokenKind.Semicolon)) Advance();
        return new BreakStatementSyntax(new SourceRange(keyword.Location, Peek(-1).Location), label);
    }

    private ContinueStatementSyntax ParseContinueStatement()
    {
        var keyword = Advance();
        string? label = null;
        if (Check(TokenKind.Identifier) && Peek().Location.Line == keyword.Location.Line)
            label = Advance().Text;
        if (Check(TokenKind.Semicolon)) Advance();
        return new ContinueStatementSyntax(new SourceRange(keyword.Location, Peek(-1).Location), label);
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
        if (Check(TokenKind.OpenBracket) || Check(TokenKind.OpenBrace))
            return ParseDestructuringVariableStatement(isConst, letKw);

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

    private SyntaxNode ParseDestructuringVariableStatement(bool isConst, Token keyword)
    {
        bool isArray = Check(TokenKind.OpenBracket);
        var pattern = isArray ? ParseArrayLiteral() : ParseObjectLiteral();
        ValidateBindingPattern(pattern);
        ExpressionSyntax? initializer = null;
        if (Check(TokenKind.Equals))
        {
            Advance();
            initializer = ParseExpression();
        }
        else
        {
            Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "A destructuring declaration requires an initializer", pattern.Range.Start));
        }
        if (Check(TokenKind.Semicolon)) Advance();
        return new DestructuringVariableDeclarationSyntax(isArray, new List<BindingElementSyntax>(), initializer, isConst,
            new SourceRange(keyword.Location, Peek(-1).Location), pattern);
    }

    private void ValidateBindingPattern(ExpressionSyntax pattern)
    {
        switch (pattern)
        {
            case ArrayLiteralExpressionSyntax array:
                for (int i = 0; i < array.Elements.Count; i++)
                {
                    var element = array.Elements[i];
                    if (element is SpreadExpressionSyntax spread)
                    {
                        if (i != array.Elements.Count - 1)
                            Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                                "A rest binding must be the last element in a binding pattern", spread.Range.Start));
                        if (spread.Expression is AssignmentExpressionSyntax)
                            Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                                "A rest binding cannot have an initializer", spread.Range.Start));
                    }
                    else
                    {
                        ValidateBindingPatternElement(element);
                    }
                }
                break;

            case ObjectLiteralExpressionSyntax obj:
                for (int i = 0; i < obj.Properties.Count; i++)
                {
                    var property = obj.Properties[i];
                    if (property is ObjectSpreadPropertySyntax spread)
                    {
                        if (i != obj.Properties.Count - 1)
                            Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
                                "A rest binding must be the last element in a binding pattern", spread.Range.Start));
                    }
                    else
                    {
                        ValidateBindingPatternElement(property.Value);
                    }
                }
                break;
        }
    }

    private void ValidateBindingPatternElement(ExpressionSyntax element)
    {
        if (element is AssignmentExpressionSyntax { OperatorToken.Kind: TokenKind.Equals } assignment)
        {
            ValidateBindingPatternElement(assignment.Target);
            return;
        }

        ValidateBindingPattern(element);
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

        if (Check(TokenKind.VoidKeyword))
        {
            var voidTok = Advance();
            var operand = ParseUnary();
            return new VoidExpressionSyntax(operand, new SourceRange(voidTok.Location, operand.Range.End));
        }

        if (Check(TokenKind.DeleteKeyword))
        {
            var deleteTok = Advance();
            var operand = ParseUnary();
            return new DeleteExpressionSyntax(operand, new SourceRange(deleteTok.Location, operand.Range.End));
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
            return ParsePostfixContinuation(
                new NewExpressionSyntax(type, args, new SourceRange(newKw.Location, Peek(-1).Location)));
        }

        return ParsePostfix();
    }

    private ExpressionSyntax ParsePostfix()
    {
        return ParsePostfixContinuation(ParsePrimary());
    }

    private ExpressionSyntax ParsePostfixContinuation(ExpressionSyntax expr)
    {
        while (true)
        {
            var typeArguments = TryParseCallTypeArguments();
            if (typeArguments != null)
            {
                Expect(TokenKind.OpenParen);
                var args = ParseArguments();
                Expect(TokenKind.CloseParen);
                expr = new CallExpressionSyntax(expr, typeArguments, args, new SourceRange(expr.Range.Start, Peek(-1).Location));
            }
            else if (Check(TokenKind.OpenParen))
            {
                Advance();
                var args = ParseArguments();
                Expect(TokenKind.CloseParen);
                expr = new CallExpressionSyntax(expr, new List<TypeSyntax>(), args, new SourceRange(expr.Range.Start, Peek(-1).Location));
            }
            else if (Check(TokenKind.Dot))
            {
                Advance();
                var member = Check(TokenKind.Hash)
                    ? ParseClassMemberName().Name
                    : ExpectMemberName();
                expr = new MemberAccessExpressionSyntax(expr, member, false,
                    new SourceRange(expr.Range.Start, Peek(-1).Location));
            }
            else if (Check(TokenKind.QuestionDot))
            {
                Advance();
                if (Check(TokenKind.OpenParen))
                {
                    Advance();
                    var args = ParseArguments();
                    Expect(TokenKind.CloseParen);
                    expr = new CallExpressionSyntax(expr, new List<TypeSyntax>(), args,
                        new SourceRange(expr.Range.Start, Peek(-1).Location), isNullConditional: true);
                }
                else if (Check(TokenKind.OpenBracket))
                {
                    Advance();
                    var index = ParseExpression();
                    Expect(TokenKind.CloseBracket);
                    expr = new IndexExpressionSyntax(expr, index,
                        new SourceRange(expr.Range.Start, Peek(-1).Location), isNullConditional: true);
                }
                else
                {
                    var member = ExpectMemberName();
                    expr = new MemberAccessExpressionSyntax(expr, member, true,
                        new SourceRange(expr.Range.Start, Peek(-1).Location));
                }
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

    private List<TypeSyntax>? TryParseCallTypeArguments()
    {
        if (!Check(TokenKind.LessThan))
            return null;

        int savedPosition = _position;
        int savedDiagnostics = Diagnostics.Count;
        var typeArguments = new List<TypeSyntax>();

        Advance();
        while (!Check(TokenKind.GreaterThan) && !IsAtEnd())
        {
            int loopStart = _position;
            typeArguments.Add(ParseType());
            if (Check(TokenKind.Comma)) Advance();
            if (_position == loopStart) Advance();
        }

        if (!Check(TokenKind.GreaterThan))
        {
            RestoreTentativeParse(savedPosition, savedDiagnostics);
            return null;
        }

        Advance();
        if (!Check(TokenKind.OpenParen))
        {
            RestoreTentativeParse(savedPosition, savedDiagnostics);
            return null;
        }

        return typeArguments;
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

        if (Check(TokenKind.Slash))
        {
            var regex = TryParseRegexLiteral();
            if (regex != null)
                return regex;
        }

        if (Check(TokenKind.DotDotDot))
        {
            var spread = Advance();
            var expression = ParseAssignment();
            return new SpreadExpressionSyntax(expression,
                new SourceRange(spread.Location, expression.Range.End));
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

        if (Check(TokenKind.FuncKeyword))
        {
            // Function expressions share the lambda semantic node. A supplied
            // name is parsed for source compatibility; closure-local recursion
            // is introduced by the binder when it lifts the expression.
            var functionKeyword = Advance();
            bool isGenerator = false;
            if (Check(TokenKind.Star))
            {
                Advance();
                isGenerator = true;
            }
            if (IsMemberNameToken(Peek().Kind) && Peek(1).Kind == TokenKind.OpenParen)
                Advance();
            var parameters = ParseParameterList();
            TypeSyntax? returnType = Check(TokenKind.Colon) ? ParseTypeAnnotation() : null;
            var body = ParseBlock();
            return new LambdaExpressionSyntax(parameters, returnType, body, false,
                new SourceRange(functionKeyword.Location, body.Range.End), isGenerator);
        }

        if (Check(TokenKind.ClassKeyword))
        {
            return ParseClassExpression();
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
            if (Check(TokenKind.DotDotDot))
            {
                var spread = Advance();
                var expression = ParseAssignment();
                elements.Add(new SpreadExpressionSyntax(expression,
                    new SourceRange(spread.Location, expression.Range.End)));
            }
            else
            {
                elements.Add(ParseExpression());
            }
            if (Check(TokenKind.Comma)) Advance();
            if (_position == loopStart) Advance();
        }

        Expect(TokenKind.CloseBracket);
        return new ArrayLiteralExpressionSyntax(elements,
            new SourceRange(openBracket.Location, Peek(-1).Location));
    }

    private ClassExpressionSyntax ParseClassExpression()
    {
        var classKw = Advance();
        string? name = null;
        if (Check(TokenKind.Identifier))
            name = ExpectIdentifier();

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
        var expression = new ClassExpressionSyntax(name, members,
            new SourceRange(classKw.Location, Peek(-1).Location))
        {
            BaseType = baseType
        };
        expression.GenericParameters.AddRange(genericParams);
        expression.ImplementedInterfaces.AddRange(implemented);
        return expression;
    }

    private RegexLiteralExpressionSyntax? TryParseRegexLiteral()
    {
        var slash = Advance();
        var pattern = new System.Text.StringBuilder();
        int classDepth = 0;

        while (!IsAtEnd())
        {
            var token = Advance();
            if (token.Kind == TokenKind.OpenBracket) classDepth++;
            else if (token.Kind == TokenKind.CloseBracket && classDepth > 0) classDepth--;
            else if (token.Kind == TokenKind.Slash && classDepth == 0)
            {
                var flags = "";
                if (Check(TokenKind.Identifier))
                    flags = Advance().Text;
                return new RegexLiteralExpressionSyntax(pattern.ToString(), flags,
                    new SourceRange(slash.Location, Peek(-1).Location));
            }

            pattern.Append(token.Text);
        }

        Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error,
            "Unterminated regular expression literal", slash.Location));
        return null;
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
            if (Check(TokenKind.DotDotDot))
            {
                var spread = Advance();
                var spreadValue = ParseExpression();
                properties.Add(new ObjectSpreadPropertySyntax(spreadValue, new SourceRange(spread.Location, Peek(-1).Location)));
                if (Check(TokenKind.Comma)) Advance();
                if (_position == loopStart) Advance();
                continue;
            }

            if (Check(TokenKind.OpenBracket))
            {
                var computedKeyStart = Advance().Location;
                var keyExpression = ParseExpression();
                Expect(TokenKind.CloseBracket);
                Expect(TokenKind.Colon);
                var computedValue = ParseExpression();
                properties.Add(new ComputedObjectPropertySyntax(keyExpression, computedValue,
                    new SourceRange(computedKeyStart, computedValue.Range.End)));
                if (Check(TokenKind.Comma)) Advance();
                if (_position == loopStart) Advance();
                continue;
            }

            var keyStart = Peek().Location;
            var key = ExpectObjectPropertyName();
            ExpressionSyntax value;
            if (Check(TokenKind.Colon))
            {
                Advance();
                value = ParseExpression();
            }
            else
            {
                value = new IdentifierExpressionSyntax(key, new SourceRange(keyStart, Peek(-1).Location));
            }
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
            if (Check(TokenKind.DotDotDot))
            {
                var spread = Advance();
                var expression = ParseAssignment();
                args.Add(new SpreadExpressionSyntax(expression,
                    new SourceRange(spread.Location, expression.Range.End)));
            }
            else
            {
                args.Add(ParseExpression());
            }
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

    private void RestoreTentativeParse(int position, int diagnosticCount)
    {
        _position = position;
        if (Diagnostics.Count > diagnosticCount)
            Diagnostics.RemoveRange(diagnosticCount, Diagnostics.Count - diagnosticCount);
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
        TokenKind.OfKeyword or TokenKind.MatchKeyword or TokenKind.DeleteKeyword or
        TokenKind.ReturnKeyword or TokenKind.ThrowKeyword or TokenKind.YieldKeyword or
        TokenKind.GetKeyword or TokenKind.SetKeyword or TokenKind.AbstractKeyword;

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

    private string ExpectObjectPropertyName()
    {
        if (Check(TokenKind.StringLiteral) || Check(TokenKind.IntegerLiteral) || Check(TokenKind.FloatLiteral))
            return Advance().Value?.ToString() ?? Peek(-1).Text;
        return ExpectMemberName();
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
