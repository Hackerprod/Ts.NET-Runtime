using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Diagnostics;
using TypeSharp.Syntax.SyntaxTree;

namespace TypeSharp.Semantics.Binder;

public sealed class Binder
{
    private readonly SymbolTable _symbolTable;
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<string, TsClassType> _classTypes = new();
    private readonly Dictionary<string, TsInterfaceType> _interfaceTypes = new();
    private readonly Dictionary<string, TsEnumType> _enumTypes = new();
    private TsClassType? _currentClassType;

    public DiagnosticBag Diagnostics => _diagnostics;

    public Binder()
    {
        _symbolTable = new SymbolTable();
        _diagnostics = new DiagnosticBag();
        RegisterBuiltins();
    }

    private void RegisterBuiltins()
    {
        var consoleType = new TsClassType("Console");
        consoleType.Methods["log"] = new TsMethod("log", TsType.Void, new List<TsParameter>
        {
            new("message", TsType.String)
        });
        _classTypes["Console"] = consoleType;

        var listType = new TsClassType("List");
        listType.Methods["add"] = new TsMethod("add", TsType.Void, new List<TsParameter>
        {
            new("item", TsType.Void)
        });
        listType.Methods["firstOrNull"] = new TsMethod("firstOrNull", TsType.Void, new List<TsParameter>());
        _classTypes["List"] = listType;

        var mapType = new TsClassType("Map");
        mapType.Methods["set"] = new TsMethod("set", TsType.Void, new List<TsParameter>
        {
            new("key", TsType.Void),
            new("value", TsType.Void)
        });
        mapType.Methods["get"] = new TsMethod("get", TsType.Void, new List<TsParameter>
        {
            new("key", TsType.Void)
        });
        _classTypes["Map"] = mapType;
    }

    public BoundSourceFile Bind(SourceFileSyntax sourceFile)
    {
        var members = new List<BoundNode>();

        foreach (var member in sourceFile.Members)
        {
            var bound = BindNode(member);
            if (bound != null)
                members.Add(bound);
        }

        return new BoundSourceFile(sourceFile.FileName, members);
    }

    private BoundNode? BindNode(SyntaxNode node)
    {
        return node switch
        {
            FunctionDeclarationSyntax func => BindFunction(func),
            ClassDeclarationSyntax cls => BindClass(cls),
            InterfaceDeclarationSyntax iface => BindInterface(iface),
            EnumDeclarationSyntax en => BindEnum(en),
            ImportDeclarationSyntax import => BindImport(import),
            TypeAliasDeclarationSyntax => null,
            StatementSyntax stmt => BindStatement(stmt),
            _ => null
        };
    }

    private BoundFunctionDeclaration BindFunction(FunctionDeclarationSyntax func)
    {
        var returnType = ResolveType(func.ReturnType) ?? TsType.Void;

        var sym = new FunctionSymbol(func.Name, returnType, func.Range)
        {
            IsAsync = func.IsAsync,
            IsExported = func.Modifiers.Any(m => m.Token.Kind == TokenKind.ExportKeyword)
        };

        foreach (var p in func.Parameters)
        {
            var paramType = ResolveType(p.TypeAnnotation);
            var paramSym = new ParameterSymbol(p.Name, paramType, p.Range);
            sym.Parameters.Add(paramSym);
        }

        _symbolTable.Define(sym);
        _symbolTable.PushScope();

        foreach (var p in sym.Parameters)
            _symbolTable.Define(p);

        var body = BindNode(func.Body);

        _symbolTable.PopScope();

        return new BoundFunctionDeclaration(sym, body!);
    }

    private BoundClassDeclaration BindClass(ClassDeclarationSyntax cls)
    {
        var classType = new TsClassType(cls.Name);
        _classTypes[cls.Name] = classType;

        if (cls.BaseType != null)
        {
            var baseType = ResolveType(cls.BaseType);
            if (baseType is TsClassType baseClass)
                classType.BaseType = baseClass;
        }

        var sym = new ClassSymbol(cls.Name, classType, cls.Range)
        {
            IsExported = cls.Modifiers.Any(m => m.Token.Kind == TokenKind.ExportKeyword)
        };

        _symbolTable.Define(sym);
        _symbolTable.PushScope();

        var members = new List<BoundNode>();
        var prevClass = _currentClassType;
        _currentClassType = classType;
        foreach (var member in cls.Members)
        {
            var bound = BindClassMember(member, classType);
            if (bound != null)
                members.Add(bound);
        }
        _currentClassType = prevClass;

        _symbolTable.PopScope();

        return new BoundClassDeclaration(sym, members);
    }

    private BoundNode? BindClassMember(SyntaxNode member, TsClassType classType)
    {
        switch (member)
        {
            case ConstructorDeclarationSyntax ctor:
            {
                var ctorSym = new MethodSymbol("constructor", TsType.Void, ctor.Range);
                ctorSym.IsStatic = false;
                ctorSym.AccessModifier = TsAccessModifier.Public;

                foreach (var p in ctor.Parameters)
                {
                    var paramType = ResolveType(p.TypeAnnotation);
                    var paramSym = new ParameterSymbol(p.Name, paramType, p.Range);
                    ctorSym.Parameters.Add(paramSym);
                }

                var ctorMethod = new TsMethod("constructor", TsType.Void,
                    ctorSym.Parameters.Select(p => new TsParameter(p.Name, p.Type)).ToList());
                classType.Constructor = ctorMethod;

                _symbolTable.PushScope();
                foreach (var p in ctorSym.Parameters)
                    _symbolTable.Define(p);
                var body = BindNode(ctor.Body);
                _symbolTable.PopScope();

                return body;
            }

            case FieldDeclarationSyntax field:
            {
                var fieldType = ResolveType(field.TypeAnnotation);
                var fieldSym = new FieldSymbol(field.Name, fieldType, field.Range);
                _symbolTable.Define(fieldSym);

                classType.Fields[field.Name] = new TsField(field.Name, fieldType);
                return null;
            }

            case MethodDeclarationSyntax method:
            {
                var methodType = ResolveType(method.ReturnType) ?? TsType.Void;
                var methodSym = new MethodSymbol(method.Name, methodType, method.Range)
                {
                    IsAsync = method.IsAsync
                };

                foreach (var p in method.Parameters)
                {
                    var paramType = ResolveType(p.TypeAnnotation);
                    var paramSym = new ParameterSymbol(p.Name, paramType, p.Range);
                    methodSym.Parameters.Add(paramSym);
                }

                var tsMethod = new TsMethod(method.Name, methodType,
                    methodSym.Parameters.Select(p => new TsParameter(p.Name, p.Type)).ToList());
                classType.Methods[method.Name] = tsMethod;

                _symbolTable.Define(methodSym);

                if (method.Body != null)
                {
                    _symbolTable.PushScope();
                    foreach (var p in methodSym.Parameters)
                        _symbolTable.Define(p);
                    var body = BindNode(method.Body);
                    _symbolTable.PopScope();
                    return body;
                }
                return null;
            }

            case PropertyDeclarationSyntax prop:
            {
                var propType = ResolveType(prop.TypeAnnotation);
                var propSym = new PropertySymbol(prop.Name, propType, prop.Range);
                _symbolTable.Define(propSym);

                classType.Properties[prop.Name] = new TsProperty(prop.Name, propType);
                return null;
            }

            default:
                return null;
        }
    }

    private BoundInterfaceDeclaration BindInterface(InterfaceDeclarationSyntax iface)
    {
        var ifaceType = new TsInterfaceType(iface.Name);
        _interfaceTypes[iface.Name] = ifaceType;

        var sym = new InterfaceSymbol(iface.Name, ifaceType, iface.Range)
        {
            IsExported = iface.Modifiers.Any(m => m.Token.Kind == TokenKind.ExportKeyword)
        };

        _symbolTable.Define(sym);

        foreach (var member in iface.Members)
        {
            if (member is FieldDeclarationSyntax field)
            {
                var fieldType = ResolveType(field.TypeAnnotation);
                ifaceType.Properties[field.Name] = new TsProperty(field.Name, fieldType);
            }
            else if (member is MethodDeclarationSyntax method)
            {
                var returnType = ResolveType(method.ReturnType) ?? TsType.Void;
                var tsMethod = new TsMethod(method.Name, returnType,
                    method.Parameters.Select(p => new TsParameter(p.Name, ResolveType(p.TypeAnnotation))).ToList());
                ifaceType.Methods[method.Name] = tsMethod;
            }
        }

        return new BoundInterfaceDeclaration(sym);
    }

    private BoundEnumDeclaration BindEnum(EnumDeclarationSyntax en)
    {
        var enumType = new TsEnumType(en.Name);
        int value = 0;
        foreach (var member in en.Members)
        {
            if (member.Value is LiteralExpressionSyntax lit && lit.Token.Value is long num)
                value = (int)num;
            enumType.Members[member.Name] = new TsEnumMember(member.Name, value);
            value++;
        }

        _enumTypes[en.Name] = enumType;

        var sym = new EnumSymbol(en.Name, enumType, en.Range)
        {
            IsExported = en.Modifiers.Any(m => m.Token.Kind == TokenKind.ExportKeyword)
        };

        _symbolTable.Define(sym);
        return new BoundEnumDeclaration(sym);
    }

    private BoundNode? BindImport(ImportDeclarationSyntax import)
    {
        return null;
    }

    // Statements
    private BoundNode BindStatement(SyntaxNode stmt)
    {
        return stmt switch
        {
            BlockStatementSyntax block => BindBlock(block),
            VariableDeclarationSyntax varDecl => BindVariableDeclaration(varDecl),
            ReturnStatementSyntax ret => BindReturn(ret),
            IfStatementSyntax ifStmt => BindIf(ifStmt),
            WhileStatementSyntax whileStmt => BindWhile(whileStmt),
            ForStatementSyntax forStmt => BindFor(forStmt),
            ThrowStatementSyntax throwStmt => BindThrow(throwStmt),
            TryStatementSyntax tryStmt => BindTry(tryStmt),
            ExpressionStatementSyntax exprStmt => BindExpressionStatement(exprStmt),
            _ => throw new InvalidOperationException($"Unexpected statement type: {stmt.NodeType}")
        };
    }

    private BoundBlockStatement BindBlock(BlockStatementSyntax block)
    {
        _symbolTable.PushScope();
        var statements = new List<BoundNode>();
        foreach (var stmt in block.Statements)
        {
            statements.Add(BindStatement(stmt));
        }
        _symbolTable.PopScope();
        return new BoundBlockStatement(statements);
    }

    private BoundVariableDeclaration BindVariableDeclaration(VariableDeclarationSyntax varDecl)
    {
        TsType type;
        BoundNode? initializer = null;

        if (varDecl.Initializer != null)
        {
            if (varDecl.Initializer is ObjectLiteralExpressionSyntax objLit && varDecl.TypeAnnotation != null)
            {
                var expectedType = ResolveType(varDecl.TypeAnnotation);
                initializer = BindObjectLiteral(objLit, expectedType);
            }
            else
            {
                initializer = BindExpression(varDecl.Initializer);
            }
            type = initializer.Type;
        }
        else if (varDecl.TypeAnnotation != null)
        {
            type = ResolveType(varDecl.TypeAnnotation);
        }
        else
        {
            _diagnostics.Error("Cannot infer type without initializer", varDecl.Range.Start);
            type = TsType.Void;
        }

        if (varDecl.TypeAnnotation != null && initializer != null)
        {
            var declaredType = ResolveType(varDecl.TypeAnnotation);
            if (!initializer.Type.IsAssignableTo(declaredType) && !declaredType.IsAssignableTo(initializer.Type))
            {
                _diagnostics.Error($"Type mismatch: cannot assign {initializer.Type} to {declaredType}",
                    varDecl.Range.Start);
            }
            type = declaredType;
        }

        var symbol = new LocalSymbol(varDecl.Name, type, varDecl.Range, varDecl.IsConst);
        _symbolTable.Define(symbol);

        return new BoundVariableDeclaration(symbol, initializer);
    }

    private BoundReturnStatement BindReturn(ReturnStatementSyntax ret)
    {
        BoundNode? value = ret.Value != null ? BindExpression(ret.Value) : null;
        return new BoundReturnStatement(value);
    }

    private BoundIfStatement BindIf(IfStatementSyntax ifStmt)
    {
        var condition = BindExpression(ifStmt.Condition);
        var thenBranch = BindStatement(ifStmt.ThenBranch);
        var elseBranch = ifStmt.ElseBranch != null ? BindStatement(ifStmt.ElseBranch) : null;
        return new BoundIfStatement(condition, thenBranch, elseBranch);
    }

    private BoundWhileStatement BindWhile(WhileStatementSyntax whileStmt)
    {
        var condition = BindExpression(whileStmt.Condition);
        var body = BindStatement(whileStmt.Body);
        return new BoundWhileStatement(condition, body);
    }

    private BoundNode BindFor(ForStatementSyntax forStmt)
    {
        _symbolTable.PushScope();

        BoundNode? initializer = forStmt.Initializer != null ? BindStatement(forStmt.Initializer) : null;
        BoundNode? condition = forStmt.Condition != null ? BindExpression(forStmt.Condition) : null;
        BoundNode? iterator = forStmt.Iterator != null ? BindExpression(forStmt.Iterator) : null;
        var body = BindStatement(forStmt.Body);

        _symbolTable.PopScope();

        return new BoundForStatement(initializer, condition, iterator, body);
    }

    private BoundThrowStatement BindThrow(ThrowStatementSyntax throwStmt)
    {
        var expr = BindExpression(throwStmt.Expression);
        return new BoundThrowStatement(expr);
    }

    private BoundTryStatement BindTry(TryStatementSyntax tryStmt)
    {
        var tryBlock = BindStatement(tryStmt.TryBlock);

        Symbol? catchSym = null;
        BoundNode? catchBlock = null;
        if (tryStmt.CatchBlock != null)
        {
            _symbolTable.PushScope();
            if (tryStmt.CatchVariable != null)
            {
                var catchType = tryStmt.CatchType != null ? ResolveType(tryStmt.CatchType) : new TsClassType("Error");
                catchSym = new LocalSymbol(tryStmt.CatchVariable, catchType, tryStmt.CatchBlock.Range);
                _symbolTable.Define(catchSym);
            }
            catchBlock = BindStatement(tryStmt.CatchBlock);
            _symbolTable.PopScope();
        }

        BoundNode? finallyBlock = null;
        if (tryStmt.FinallyBlock != null)
        {
            finallyBlock = BindStatement(tryStmt.FinallyBlock);
        }

        return new BoundTryStatement(tryBlock, catchSym, catchBlock, finallyBlock);
    }

    private BoundExpressionStatement BindExpressionStatement(ExpressionStatementSyntax exprStmt)
    {
        var expr = BindExpression(exprStmt.Expression);
        return new BoundExpressionStatement(expr);
    }

    // Expressions
    public BoundNode BindExpression(ExpressionSyntax expr)
    {
        return expr switch
        {
            LiteralExpressionSyntax lit => BindLiteral(lit),
            IdentifierExpressionSyntax id => BindIdentifier(id),
            BinaryExpressionSyntax bin => BindBinary(bin),
            UnaryExpressionSyntax unary => BindUnary(unary),
            CallExpressionSyntax call => BindCall(call),
            MemberAccessExpressionSyntax member => BindMemberAccess(member),
            AssignmentExpressionSyntax assign => BindAssignment(assign),
            ConditionalExpressionSyntax cond => BindConditional(cond),
            NewExpressionSyntax newExpr => BindNew(newExpr),
            ThisExpressionSyntax thisExpr => BindThis(thisExpr),
            AwaitExpressionSyntax awaitExpr => BindAwait(awaitExpr),
            ObjectLiteralExpressionSyntax objLit => BindObjectLiteral(objLit),
            IndexExpressionSyntax indexExpr => BindIndexExpression(indexExpr),
            _ => throw new InvalidOperationException($"Unexpected expression type: {expr.NodeType}")
        };
    }

    private BoundLiteralExpression BindLiteral(LiteralExpressionSyntax lit)
    {
        TsType type;
        object? value;

        switch (lit.Token.Kind)
        {
            case TokenKind.IntegerLiteral:
                if (lit.Token.Value is long l)
                {
                    if (l >= int.MinValue && l <= int.MaxValue)
                    {
                        type = TsType.Int32;
                        value = (int)l;
                    }
                    else
                    {
                        type = TsType.Int64;
                        value = l;
                    }
                }
                else if (lit.Token.Value is double d)
                {
                    var l2 = (long)d;
                    if (l2 >= int.MinValue && l2 <= int.MaxValue)
                    {
                        type = TsType.Int32;
                        value = (int)l2;
                    }
                    else
                    {
                        type = TsType.Int64;
                        value = l2;
                    }
                }
                else if (lit.Token.Value is int i)
                {
                    type = TsType.Int32;
                    value = i;
                }
                else
                {
                    type = TsType.Int32;
                    value = 0;
                }
                break;
            case TokenKind.FloatLiteral:
                type = TsType.Float64;
                value = lit.Token.Value ?? 0.0;
                break;
            case TokenKind.StringLiteral:
                type = TsType.String;
                value = lit.Token.Value ?? "";
                break;
            case TokenKind.TrueLiteral:
            case TokenKind.FalseLiteral:
                type = TsType.Bool;
                value = lit.Token.Kind == TokenKind.TrueLiteral;
                break;
            case TokenKind.NullLiteral:
                type = TsType.Void;
                value = null;
                break;
            default:
                type = TsType.Void;
                value = null;
                break;
        }

        return new BoundLiteralExpression(value, type);
    }

    private BoundVariableExpression BindIdentifier(IdentifierExpressionSyntax id)
    {
        var symbol = _symbolTable.Lookup(id.Name);
        if (symbol == null)
        {
            _diagnostics.Error($"Undefined symbol: '{id.Name}'", id.Range.Start);
            return new BoundVariableExpression(
                new LocalSymbol(id.Name, TsType.Void, id.Range));
        }

        return new BoundVariableExpression(symbol);
    }

    private BoundBinaryExpression BindBinary(BinaryExpressionSyntax bin)
    {
        var left = BindExpression(bin.Left);
        var right = BindExpression(bin.Right);
        var resultType = InferBinaryResultType(left.Type, right.Type, bin.OperatorToken.Kind);

        return new BoundBinaryExpression(left, bin.OperatorToken.Kind, right, resultType);
    }

    private BoundUnaryExpression BindUnary(UnaryExpressionSyntax unary)
    {
        var operand = BindExpression(unary.Operand);
        var resultType = InferUnaryResultType(operand.Type, unary.OperatorToken.Kind);

        return new BoundUnaryExpression(unary.OperatorToken.Kind, operand, unary.IsPrefix, resultType);
    }

    private BoundCallExpression BindCall(CallExpressionSyntax call)
    {
        var callee = BindExpression(call.Callee);
        var args = call.Arguments.Select(BindExpression).ToList();

        TsType returnType = TsType.Void;

        if (callee is BoundVariableExpression varExpr && varExpr.Symbol is FunctionSymbol funcSym)
        {
            returnType = funcSym.Type;
        }
        else if (callee is BoundMemberAccessExpression memberExpr && memberExpr.Member is MethodSymbol methodSym)
        {
            returnType = methodSym.Type;
        }

        return new BoundCallExpression(callee, args, returnType);
    }

    private BoundMemberAccessExpression BindMemberAccess(MemberAccessExpressionSyntax member)
    {
        var obj = BindExpression(member.Object);

        Symbol? memberSym = null;

        var objType = obj.Type;
        if (objType is TsNullableType nullable)
            objType = nullable.ElementType;
        if (objType is TsGenericType generic)
            objType = generic.Definition;

        if (objType is TsClassType classType)
        {
            if (classType.Fields.TryGetValue(member.MemberName, out var field))
            {
                memberSym = new FieldSymbol(member.MemberName, field.Type, member.Range);
            }
            else if (classType.Methods.TryGetValue(member.MemberName, out var method))
            {
                memberSym = new MethodSymbol(member.MemberName, method.ReturnType, member.Range);
            }
            else if (classType.Properties.TryGetValue(member.MemberName, out var prop))
            {
                memberSym = new PropertySymbol(member.MemberName, prop.Type, member.Range);
            }
        }
        else if (objType is TsInterfaceType ifaceType)
        {
            if (ifaceType.Properties.TryGetValue(member.MemberName, out var prop))
            {
                memberSym = new PropertySymbol(member.MemberName, prop.Type, member.Range);
            }
            else if (ifaceType.Methods.TryGetValue(member.MemberName, out var method))
            {
                memberSym = new MethodSymbol(member.MemberName, method.ReturnType, member.Range);
            }
        }

        if (memberSym == null)
        {
            _diagnostics.Error($"No member '{member.MemberName}' on type '{obj.Type}'", member.Range.Start);
            memberSym = new PropertySymbol(member.MemberName, TsType.Void, member.Range);
        }

        var resultType = member.IsNullConditional && memberSym.Type is not TsNullableType
            ? new TsNullableType(memberSym.Type)
            : memberSym.Type;

        return new BoundMemberAccessExpression(obj, memberSym);
    }

    private BoundAssignmentExpression BindAssignment(AssignmentExpressionSyntax assign)
    {
        var target = BindExpression(assign.Target);
        var value = BindExpression(assign.Value);

        if (target is BoundVariableExpression varExpr && varExpr.Symbol is LocalSymbol local && local.IsConst)
        {
            _diagnostics.Error($"Cannot assign to const variable '{local.Name}'", assign.Range.Start);
        }

        return new BoundAssignmentExpression(target, value);
    }

    private BoundConditionalExpression BindConditional(ConditionalExpressionSyntax cond)
    {
        var condition = BindExpression(cond.Condition);
        var whenTrue = BindExpression(cond.WhenTrue);
        var whenFalse = BindExpression(cond.WhenFalse);

        TsType resultType = whenTrue.Type.IsAssignableTo(whenFalse.Type) ? whenTrue.Type :
                           whenFalse.Type.IsAssignableTo(whenTrue.Type) ? whenFalse.Type :
                           whenTrue.Type;

        return new BoundConditionalExpression(condition, whenTrue, whenFalse, resultType);
    }

    private BoundNewExpression BindNew(NewExpressionSyntax newExpr)
    {
        var type = ResolveType(newExpr.Type);
        var args = newExpr.Arguments.Select(BindExpression).ToList();
        return new BoundNewExpression(type, args);
    }

    private BoundThisExpression BindThis(ThisExpressionSyntax thisExpr)
    {
        var type = _currentClassType ?? new TsClassType("Unknown");
        return new BoundThisExpression(type);
    }

    private BoundAwaitExpression BindAwait(AwaitExpressionSyntax awaitExpr)
    {
        var expr = BindExpression(awaitExpr.Expression);

        if (expr.Type is TsPromiseType promiseType)
        {
            return new BoundAwaitExpression(expr, promiseType.ElementType);
        }

        _diagnostics.Warning("Awaiting non-promise type", awaitExpr.Range.Start);
        return new BoundAwaitExpression(expr, expr.Type);
    }

    private BoundObjectLiteralExpression BindObjectLiteral(ObjectLiteralExpressionSyntax objLit, TsType? expectedType = null)
    {
        var properties = new List<BoundObjectPropertyNode>();
        TsType resultType = new TsClassType("Object");

        if (expectedType is TsInterfaceType ifaceType)
        {
            resultType = ifaceType;
            foreach (var prop in objLit.Properties)
            {
                TsType propType = TsType.Void;
                if (ifaceType.Properties.TryGetValue(prop.Key, out var ifaceProp))
                    propType = ifaceProp.Type;
                var value = BindExpression(prop.Value);
                properties.Add(new BoundObjectPropertyNode(prop.Key, value, propType));
            }
        }
        else
        {
            foreach (var prop in objLit.Properties)
            {
                var value = BindExpression(prop.Value);
                properties.Add(new BoundObjectPropertyNode(prop.Key, value, value.Type));
            }
        }

        return new BoundObjectLiteralExpression(properties, resultType);
    }

    private BoundIndexExpression BindIndexExpression(IndexExpressionSyntax indexExpr)
    {
        var obj = BindExpression(indexExpr.Object);
        var index = BindExpression(indexExpr.Index);
        return new BoundIndexExpression(obj, index, TsType.Void);
    }

    // Type resolution
    public TsType ResolveType(TypeSyntax? typeSyntax)
    {
        if (typeSyntax == null) return TsType.Void;

        return typeSyntax switch
        {
            PrimitiveTypeSyntax prim => TsType.FromToken(prim.TypeKeyword.Kind),
            NamedTypeSyntax named => ResolveNamedType(named),
            ArrayTypeSyntax arr => new TsArrayType(ResolveType(arr.ElementType)),
            MapTypeSyntax map => new TsMapType(ResolveType(map.KeyType), ResolveType(map.ValueType)),
            PromiseTypeSyntax promise => new TsPromiseType(ResolveType(promise.ElementType)),
            NullableTypeSyntax nullable => new TsNullableType(ResolveType(nullable.ElementType)),
            UnionTypeSyntax union => new TsUnionType(union.Types.Select(ResolveType).ToList()),
            _ => TsType.Void
        };
    }

    private TsType ResolveNamedType(NamedTypeSyntax named)
    {
        if (_classTypes.TryGetValue(named.Name, out var classType))
        {
            if (named.TypeArguments.Count > 0)
            {
                var typeArgs = named.TypeArguments.Select(ResolveType).ToList();
                return new TsGenericType(classType, typeArgs);
            }
            return classType;
        }

        if (_interfaceTypes.TryGetValue(named.Name, out var ifaceType))
            return ifaceType;

        if (_enumTypes.TryGetValue(named.Name, out var enumType))
            return enumType;

        if (named.Name == "Error")
            return new TsClassType("Error");

        return new TsClassType(named.Name);
    }

    // Type inference helpers
    private static TsType InferBinaryResultType(TsType left, TsType right, TokenKind op)
    {
        return op switch
        {
            TokenKind.DoubleEquals or TokenKind.StrictNotEquals or
            TokenKind.NotEquals or TokenKind.LessThan or TokenKind.GreaterThan or
            TokenKind.LessOrEqual or TokenKind.GreaterOrEqual or
            TokenKind.AmpersandAmpersand or TokenKind.PipePipe => TsType.Bool,

            TokenKind.QuestionQuestion => right,

            TokenKind.Plus when left == TsType.String || right == TsType.String => TsType.String,

            _ => left.IsNumeric && right.IsNumeric ? left : TsType.Void
        };
    }

    private static TsType InferUnaryResultType(TsType operand, TokenKind op)
    {
        return op switch
        {
            TokenKind.Bang => TsType.Bool,
            TokenKind.Minus or TokenKind.Plus when operand.IsNumeric => operand,
            _ => operand
        };
    }
}
