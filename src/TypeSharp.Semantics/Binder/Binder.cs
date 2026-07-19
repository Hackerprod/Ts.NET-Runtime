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
    private readonly Dictionary<string, TsType> _typeAliases = new();
    private readonly Dictionary<string, Dictionary<string, Symbol>> _importedSymbols = new();
    private TsClassType? _currentClassType;
    private TsType? _currentFunctionReturnType;
    private bool _currentFunctionIsAsync;
    private bool _inConstructor;
    private int _lambdaCounter;
    private int _forOfCounter;
    private string _currentSourceFileName = "module";

    // Generic type parameters in scope, innermost last.
    private readonly List<Dictionary<string, TsTypeParameter>> _typeParameterScopes = new();

    // Closure support: every LocalSymbol/ParameterSymbol records the function
    // nesting depth where it was declared; identifiers resolved from a deeper
    // function are captures and get boxed.
    private int _functionDepth;
    private readonly Dictionary<Symbol, int> _symbolFunctionDepth = new();
    private readonly List<(int FunctionDepth, List<Symbol> Captured)> _captureCollectors = new();

    private void DefineWithDepth(Symbol symbol)
    {
        _symbolFunctionDepth[symbol] = _functionDepth;
        _symbolTable.Define(symbol);
    }

    // Symbols resolved across a function boundary are captures: every
    // function between the use and the declaration must thread the box.
    private void RegisterPossibleCapture(Symbol symbol)
    {
        if (symbol is not (LocalSymbol or ParameterSymbol))
            return;
        if (!_symbolFunctionDepth.TryGetValue(symbol, out int declaredDepth))
            return;
        if (declaredDepth >= _functionDepth || declaredDepth == 0 && _functionDepth == 0)
            return;

        foreach (var (collectorDepth, captured) in _captureCollectors)
        {
            if (collectorDepth > declaredDepth && !captured.Contains(symbol))
                captured.Add(symbol);
        }

        if (declaredDepth < _functionDepth)
        {
            switch (symbol)
            {
                case LocalSymbol local: local.IsCaptured = true; break;
                case ParameterSymbol param: param.IsCaptured = true; break;
            }
        }
    }

    private static readonly Dictionary<(string Owner, string Member), double> StaticNumericConstants = new()
    {
        [("Math", "PI")] = Math.PI,
        [("Math", "E")] = Math.E,
        [("Number", "MAX_SAFE_INTEGER")] = 9007199254740991d,
        [("Number", "MIN_SAFE_INTEGER")] = -9007199254740991d,
        [("Number", "MAX_VALUE")] = double.MaxValue,
        [("Number", "MIN_VALUE")] = double.Epsilon,
        [("Number", "EPSILON")] = double.Epsilon,
        [("Number", "POSITIVE_INFINITY")] = double.PositiveInfinity,
        [("Number", "NEGATIVE_INFINITY")] = double.NegativeInfinity,
    };

    public DiagnosticBag Diagnostics => _diagnostics;

    public Binder()
    {
        _symbolTable = new SymbolTable();
        _diagnostics = new DiagnosticBag();
        RegisterBuiltins();
    }

    public void AddImportedSymbols(string moduleId, Dictionary<string, Symbol> symbols)
    {
        _importedSymbols[moduleId] = symbols;
    }

    public void AddGlobalSymbols(IReadOnlyDictionary<string, Symbol> symbols)
    {
        foreach (var symbol in symbols.Values)
            _symbolTable.Define(symbol);
    }

    private void RegisterBuiltins()
    {
        var consoleType = new TsClassType("Console");
        consoleType.Methods["log"] = new TsMethod("log", TsType.Void, new List<TsParameter>
        {
            new("message", TsType.String)
        });
        _classTypes["Console"] = consoleType;

        // Builtin collection members are dynamically typed until reified
        // generics land; Any keeps argument validation from rejecting them.
        var listType = new TsClassType("List");
        listType.Methods["add"] = new TsMethod("add", TsType.Void, new List<TsParameter>
        {
            new("item", TsType.Any)
        });
        listType.Methods["firstOrNull"] = new TsMethod("firstOrNull", TsType.Any, new List<TsParameter>());
        _classTypes["List"] = listType;

        var mapType = new TsClassType("Map");
        mapType.Methods["set"] = new TsMethod("set", TsType.Void, new List<TsParameter>
        {
            new("key", TsType.Any),
            new("value", TsType.Any)
        });
        mapType.Methods["get"] = new TsMethod("get", TsType.Any, new List<TsParameter>
        {
            new("key", TsType.Any)
        });
        _classTypes["Map"] = mapType;

        RegisterJsGlobals();
    }

    // Static JS-style globals (Math, Number, console) resolve as namespace
    // classes; their methods take flexible arity (empty parameter lists skip
    // argument-count validation) and execute as VM builtins.
    private void RegisterJsGlobals()
    {
        var math = new TsClassType("Math");
        foreach (var name in new[]
        {
            "abs", "floor", "ceil", "round", "trunc", "sqrt", "cbrt", "pow",
            "min", "max", "log", "log2", "log10", "exp", "sign", "random", "hypot"
        })
        {
            math.Methods[name] = new TsMethod(name, TsType.Number, new List<TsParameter>());
        }
        math.Fields["PI"] = new TsField("PI", TsType.Number);
        math.Fields["E"] = new TsField("E", TsType.Number);
        _classTypes["Math"] = math;
        _symbolTable.Define(new ClassSymbol("Math", math, default));

        var number = new TsClassType("Number");
        number.Methods["isInteger"] = new TsMethod("isInteger", TsType.Bool, new List<TsParameter>());
        number.Methods["isFinite"] = new TsMethod("isFinite", TsType.Bool, new List<TsParameter>());
        number.Methods["isNaN"] = new TsMethod("isNaN", TsType.Bool, new List<TsParameter>());
        number.Methods["parseFloat"] = new TsMethod("parseFloat", TsType.Number, new List<TsParameter>());
        number.Methods["parseInt"] = new TsMethod("parseInt", TsType.Number, new List<TsParameter>());
        foreach (var constant in new[]
        {
            "MAX_SAFE_INTEGER", "MIN_SAFE_INTEGER", "MAX_VALUE", "MIN_VALUE",
            "EPSILON", "POSITIVE_INFINITY", "NEGATIVE_INFINITY"
        })
        {
            number.Fields[constant] = new TsField(constant, TsType.Number);
        }
        _classTypes["Number"] = number;
        _symbolTable.Define(new ClassSymbol("Number", number, default));

        var consoleGlobal = new TsClassType("console");
        consoleGlobal.Methods["log"] = new TsMethod("log", TsType.Void, new List<TsParameter>());
        consoleGlobal.Methods["error"] = new TsMethod("error", TsType.Void, new List<TsParameter>());
        consoleGlobal.Methods["warn"] = new TsMethod("warn", TsType.Void, new List<TsParameter>());
        _classTypes["console"] = consoleGlobal;
        _symbolTable.Define(new ClassSymbol("console", consoleGlobal, default));

        var arrayGlobal = new TsClassType("Array");
        arrayGlobal.Methods["from"] = new TsMethod("from", new TsArrayType(TsType.Any), new List<TsParameter>());
        arrayGlobal.Methods["of"] = new TsMethod("of", new TsArrayType(TsType.Any), new List<TsParameter>());
        arrayGlobal.Methods["isArray"] = new TsMethod("isArray", TsType.Bool, new List<TsParameter>());
        _classTypes["Array"] = arrayGlobal;
        _symbolTable.Define(new ClassSymbol("Array", arrayGlobal, default));

        var uint8ArrayGlobal = new TsClassType("Uint8Array");
        uint8ArrayGlobal.Fields["length"] = new TsField("length", TsType.Number) { IsReadonly = true };
        uint8ArrayGlobal.Methods["slice"] = new TsMethod("slice", uint8ArrayGlobal, new List<TsParameter>());
        uint8ArrayGlobal.Methods["subarray"] = new TsMethod("subarray", uint8ArrayGlobal, new List<TsParameter>());
        uint8ArrayGlobal.Methods["set"] = new TsMethod("set", TsType.Void, new List<TsParameter>());
        _classTypes["Uint8Array"] = uint8ArrayGlobal;
        _symbolTable.Define(new ClassSymbol("Uint8Array", uint8ArrayGlobal, default));

        var arrayBufferGlobal = new TsClassType("ArrayBuffer");
        _classTypes["ArrayBuffer"] = arrayBufferGlobal;
        _symbolTable.Define(new ClassSymbol("ArrayBuffer", arrayBufferGlobal, default));

        DefineGlobalFunction("parseInt", TsType.Number);
        DefineGlobalFunction("parseFloat", TsType.Number);
        DefineGlobalFunction("isNaN", TsType.Bool);
        DefineGlobalFunction("isFinite", TsType.Bool);
        DefineGlobalFunction("String", TsType.String);
        DefineGlobalFunction("Boolean", TsType.Bool);
    }

    private void DefineGlobalFunction(string name, TsType returnType)
    {
        _symbolTable.Define(new FunctionSymbol(name, returnType, default)
        {
            HasDynamicSignature = true
        });
    }

    public BoundSourceFile Bind(SourceFileSyntax sourceFile)
    {
        _currentSourceFileName = sourceFile.FileName;
        var members = new List<BoundNode>();

        // Imports must be visible before type/member/function signature passes:
        // idiomatic TS declarations commonly reference imported interfaces in
        // exported function signatures and callback parameter types.
        foreach (var import in sourceFile.Members.OfType<ImportDeclarationSyntax>())
        {
            var boundImport = BindImport(import);
            if (boundImport != null)
                members.Add(boundImport);
        }

        // Pass 1: type declarations are hoisted so annotations anywhere in the
        // file (including function signatures) resolve against real types.
        // Shells first so members/bases can reference types in any order.
        foreach (var member in sourceFile.Members)
            PreDeclareTypeShell(member);
        foreach (var member in sourceFile.Members)
            PreDeclareTypeMembers(member);

        // Pass 2: function signatures are hoisted so forward references and
        // mutual recursion resolve against complete signatures before bodies bind.
        foreach (var function in sourceFile.Members.OfType<FunctionDeclarationSyntax>())
            DeclareFunctionSignature(function);
        foreach (var varDecl in sourceFile.Members.OfType<VariableDeclarationSyntax>())
        {
            if (varDecl.Initializer is LambdaExpressionSyntax lambda)
                DeclareLambdaSignature(varDecl.Name, lambda, varDecl.IsExported);
        }

        foreach (var member in sourceFile.Members)
        {
            if (member is ImportDeclarationSyntax)
                continue;

            var bound = BindNode(member);
            if (bound != null)
                members.Add(bound);
        }

        return new BoundSourceFile(sourceFile.FileName, members);
    }

    private void PreDeclareTypeShell(SyntaxNode member)
    {
        switch (member)
        {
            case ClassDeclarationSyntax cls when !_classTypes.ContainsKey(cls.Name):
                var clsShell = new TsClassType(cls.Name);
                clsShell.TypeParameters.AddRange(cls.GenericParameters.Select(p => new TsTypeParameter(p.Name)));
                _classTypes[cls.Name] = clsShell;
                break;
            case InterfaceDeclarationSyntax iface when !_interfaceTypes.ContainsKey(iface.Name):
                var ifaceShell = new TsInterfaceType(iface.Name);
                ifaceShell.TypeParameters.AddRange(iface.GenericParameters.Select(p => new TsTypeParameter(p.Name)));
                _interfaceTypes[iface.Name] = ifaceShell;
                break;
            case EnumDeclarationSyntax en when !_enumTypes.ContainsKey(en.Name):
                _enumTypes[en.Name] = new TsEnumType(en.Name);
                break;

            // Object-shaped aliases (`type Node<T> = { … }`) get an interface
            // shell so self-referential members resolve.
            case TypeAliasDeclarationSyntax { Type: ObjectTypeSyntax } alias
                when !_interfaceTypes.ContainsKey(alias.Name):
                var aliasShell = new TsInterfaceType(alias.Name);
                aliasShell.TypeParameters.AddRange(alias.GenericParameters.Select(p => new TsTypeParameter(p.Name)));
                _interfaceTypes[alias.Name] = aliasShell;
                break;
        }
    }

    private void PreDeclareTypeMembers(SyntaxNode member)
    {
        switch (member)
        {
            case TypeAliasDeclarationSyntax alias:
                PushTypeParameters(alias.GenericParameters);
                if (alias.Type is ObjectTypeSyntax objType)
                {
                    var shell = _interfaceTypes[alias.Name];
                    foreach (var m in objType.Members)
                    {
                        var memberType = ResolveType(m.Type);
                        if (m.IsOptional && memberType is not TsNullableType)
                            memberType = new TsNullableType(memberType);
                        shell.Properties[m.Name] = new TsProperty(m.Name, memberType);
                    }
                }
                else
                {
                    _typeAliases[alias.Name] = ResolveType(alias.Type);
                }
                PopTypeParameters();
                break;
            case ClassDeclarationSyntax cls:
                if (!_classTypes.TryGetValue(cls.Name, out var classType))
                    break;

                PushTypeParameters(cls.GenericParameters);

                if (cls.BaseType != null && ResolveType(cls.BaseType) is TsClassType baseClass)
                    classType.BaseType = baseClass;

                foreach (var classMember in cls.Members)
                {
                    switch (classMember)
                    {
                        case ConstructorDeclarationSyntax ctor:
                            classType.Constructor = new TsMethod("constructor", TsType.Void,
                                ctor.Parameters.Select(p => new TsParameter(p.Name, ResolveType(p.TypeAnnotation))).ToList());
                            foreach (var p in ctor.Parameters)
                            {
                                if (p.IsPropertyParameter)
                                    classType.Fields[p.Name] = new TsField(p.Name, ResolveType(p.TypeAnnotation));
                            }
                            break;

                        case FieldDeclarationSyntax field:
                            classType.Fields[field.Name] = new TsField(field.Name, ResolveType(field.TypeAnnotation))
                            {
                                IsReadonly = field.IsReadonly
                            };
                            break;

                        case MethodDeclarationSyntax method:
                            classType.Methods[method.Name] = new TsMethod(method.Name,
                                ResolveType(method.ReturnType) ?? TsType.Void,
                                method.Parameters.Select(p => new TsParameter(p.Name, ResolveType(p.TypeAnnotation))).ToList());
                            break;

                        case PropertyDeclarationSyntax prop:
                            classType.Properties[prop.Name] = new TsProperty(prop.Name, ResolveType(prop.TypeAnnotation));
                            break;
                    }
                }

                PopTypeParameters();
                break;

            case InterfaceDeclarationSyntax iface:
            {
                var ifaceType = _interfaceTypes[iface.Name];
                foreach (var m in iface.Members)
                {
                    if (m is FieldDeclarationSyntax field)
                        ifaceType.Properties[field.Name] = new TsProperty(field.Name, ResolveType(field.TypeAnnotation))
                        {
                            IsReadonly = field.IsReadonly
                        };
                    else if (m is MethodDeclarationSyntax method)
                        ifaceType.Methods[method.Name] = new TsMethod(method.Name,
                            ResolveType(method.ReturnType) ?? TsType.Void,
                            method.Parameters.Select(p => new TsParameter(p.Name, ResolveType(p.TypeAnnotation))).ToList());
                }
                break;
            }
        }
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
        var sym = _symbolTable.Lookup(func.Name) is FunctionSymbol declared && declared.Location.Equals(func.Range)
            ? declared
            : DeclareFunctionSignature(func);
        PushTypeParameters(func.GenericParameters);
        _symbolTable.PushScope();

        _functionDepth++;
        var collector = new List<Symbol>();
        _captureCollectors.Add((_functionDepth, collector));

        foreach (var p in sym.Parameters)
            DefineWithDepth(p);

        var prevReturnType = _currentFunctionReturnType;
        var prevIsAsync = _currentFunctionIsAsync;
        _currentFunctionIsAsync = sym.IsAsync;
        _currentFunctionReturnType = BodyReturnType(sym.Type, sym.IsAsync);
        var body = BindNode(func.Body);
        _currentFunctionReturnType = prevReturnType;
        _currentFunctionIsAsync = prevIsAsync;

        _captureCollectors.RemoveAt(_captureCollectors.Count - 1);
        _functionDepth--;
        _symbolTable.PopScope();
        PopTypeParameters();

        var declaration = new BoundFunctionDeclaration(sym, body!);
        declaration.CapturedVariables.AddRange(collector);
        return declaration;
    }

    private FunctionSymbol DeclareLambdaSignature(
        string name,
        LambdaExpressionSyntax lambda,
        bool exported,
        TsFunctionType? contextualType = null)
    {
        if (_symbolTable.Lookup(name) is FunctionSymbol existing && existing.Location.Equals(lambda.Range))
            return existing;

        PushTypeParameters(lambda.GenericParameters);
        var declaredReturnType = lambda.ReturnType != null
            ? ResolveType(lambda.ReturnType)
            : contextualType?.ReturnType ?? TsType.Any;
        var returnType = NormalizeFunctionReturnType(declaredReturnType, lambda.IsAsync);
        var sym = new FunctionSymbol(name, returnType, lambda.Range)
        {
            IsAsync = lambda.IsAsync,
            IsExported = exported
        };
        for (int i = 0; i < lambda.Parameters.Count; i++)
        {
            var parameter = lambda.Parameters[i];
            var parameterType = !parameter.TypeWasInferred
                ? ResolveType(parameter.TypeAnnotation)
                : contextualType != null && i < contextualType.Parameters.Count
                    ? contextualType.Parameters[i].Type
                    : TsType.Any;
            sym.Parameters.Add(new ParameterSymbol(parameter.Name, parameterType, parameter.Range));
        }
        PopTypeParameters();
        _symbolTable.Define(sym);
        return sym;
    }

    private BoundFunctionDeclaration BindLambdaAsFunction(
        string name,
        LambdaExpressionSyntax lambda,
        bool exported,
        TsFunctionType? contextualType = null)
    {
        // Reuse only the signature hoisted for THIS declaration (matched by
        // source range); otherwise a shadowed outer name would be clobbered.
        var sym = _symbolTable.Lookup(name) is FunctionSymbol hoisted && hoisted.Location.Equals(lambda.Range)
            ? hoisted
            : DeclareLambdaSignature(name, lambda, exported, contextualType);
        PushTypeParameters(lambda.GenericParameters);
        _symbolTable.PushScope();

        _functionDepth++;
        var collector = new List<Symbol>();
        _captureCollectors.Add((_functionDepth, collector));

        foreach (var p in sym.Parameters)
            DefineWithDepth(p);

        var prevReturnType = _currentFunctionReturnType;
        var prevIsAsync = _currentFunctionIsAsync;
        _currentFunctionIsAsync = sym.IsAsync;
        _currentFunctionReturnType = sym.Type is TsAnyType ? null : BodyReturnType(sym.Type, sym.IsAsync);
        var body = BindNode(lambda.Body);
        _currentFunctionReturnType = prevReturnType;
        _currentFunctionIsAsync = prevIsAsync;

        _captureCollectors.RemoveAt(_captureCollectors.Count - 1);
        _functionDepth--;
        _symbolTable.PopScope();
        PopTypeParameters();

        var declaration = new BoundFunctionDeclaration(sym, body!);
        declaration.CapturedVariables.AddRange(collector);
        return declaration;
    }

    private FunctionSymbol DeclareFunctionSignature(FunctionDeclarationSyntax func)
    {
        if (_symbolTable.Lookup(func.Name) is FunctionSymbol existing && existing.Location.Equals(func.Range))
            return existing;

        PushTypeParameters(func.GenericParameters);
        var declaredReturnType = ResolveType(func.ReturnType) ?? TsType.Void;
        var sym = new FunctionSymbol(func.Name, NormalizeFunctionReturnType(declaredReturnType, func.IsAsync), func.Range)
        {
            IsAsync = func.IsAsync,
            IsExported = func.Modifiers.Any(m => m.Token.Kind == TokenKind.ExportKeyword)
        };
        foreach (var parameter in func.Parameters)
            sym.Parameters.Add(new ParameterSymbol(parameter.Name, ResolveType(parameter.TypeAnnotation), parameter.Range));
        PopTypeParameters();
        _symbolTable.Define(sym);
        return sym;
    }

    private BoundClassDeclaration BindClass(ClassDeclarationSyntax cls)
    {
        if (!_classTypes.TryGetValue(cls.Name, out var classType))
        {
            classType = new TsClassType(cls.Name);
            _classTypes[cls.Name] = classType;
        }

        if (cls.BaseType != null && classType.BaseType == null)
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
        PushTypeParameters(cls.GenericParameters);
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
        PopTypeParameters();

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

                    // `constructor(private x: T)` also declares field x.
                    if (p.IsPropertyParameter)
                        classType.Fields[p.Name] = new TsField(p.Name, paramType);
                }

                var ctorMethod = new TsMethod("constructor", TsType.Void,
                    ctorSym.Parameters.Select(p => new TsParameter(p.Name, p.Type)).ToList());
                classType.Constructor = ctorMethod;

                _symbolTable.PushScope();
                _functionDepth++;
                foreach (var p in ctorSym.Parameters)
                    DefineWithDepth(p);
                var prevReturnType = _currentFunctionReturnType;
                var prevInConstructor = _inConstructor;
                _currentFunctionReturnType = TsType.Void;
                _inConstructor = true;
                var body = BindNode(ctor.Body);
                _inConstructor = prevInConstructor;
                _currentFunctionReturnType = prevReturnType;
                _functionDepth--;
                _symbolTable.PopScope();

                // Property parameters assign themselves before the user body.
                var propertyAssignments = new List<BoundNode>();
                for (int i = 0; i < ctor.Parameters.Count; i++)
                {
                    var p = ctor.Parameters[i];
                    if (!p.IsPropertyParameter)
                        continue;
                    var paramSym = ctorSym.Parameters[i];
                    propertyAssignments.Add(new BoundExpressionStatement(
                        new BoundAssignmentExpression(
                            new BoundMemberAccessExpression(
                                new BoundThisExpression(classType),
                                new FieldSymbol(p.Name, paramSym.Type, p.Range)),
                            new BoundVariableExpression(paramSym))));
                }
                if (propertyAssignments.Count > 0)
                {
                    propertyAssignments.Add(body!);
                    body = new BoundBlockStatement(propertyAssignments);
                }

                return new BoundConstructorDeclaration(classType.Name, ctorSym, body!);
            }

            case FieldDeclarationSyntax field:
            {
                var fieldType = ResolveType(field.TypeAnnotation);
                var fieldSym = new FieldSymbol(field.Name, fieldType, field.Range)
                {
                    IsReadonly = field.IsReadonly
                };
                _symbolTable.Define(fieldSym);

                classType.Fields[field.Name] = new TsField(field.Name, fieldType)
                {
                    IsReadonly = field.IsReadonly
                };
                return new BoundFieldInitializer(classType.Name, fieldSym);
            }

            case MethodDeclarationSyntax method:
            {
                var declaredMethodType = ResolveType(method.ReturnType) ?? TsType.Void;
                var methodType = NormalizeFunctionReturnType(declaredMethodType, method.IsAsync);
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
                    methodSym.Parameters.Select(p => new TsParameter(p.Name, p.Type)).ToList())
                {
                    IsAsync = method.IsAsync
                };
                ValidateOverride(classType, methodSym, method.Range.Start);
                classType.Methods[method.Name] = tsMethod;

                _symbolTable.Define(methodSym);

                if (method.Body != null)
                {
                    _symbolTable.PushScope();
                    _functionDepth++;
                    foreach (var p in methodSym.Parameters)
                        DefineWithDepth(p);
                    var prevReturnType = _currentFunctionReturnType;
                    var prevIsAsync = _currentFunctionIsAsync;
                    var prevInConstructor = _inConstructor;
                    _currentFunctionIsAsync = method.IsAsync;
                    _currentFunctionReturnType = BodyReturnType(methodType, method.IsAsync);
                    _inConstructor = false;
                    var body = BindNode(method.Body);
                    _inConstructor = prevInConstructor;
                    _currentFunctionReturnType = prevReturnType;
                    _currentFunctionIsAsync = prevIsAsync;
                    _functionDepth--;
                    _symbolTable.PopScope();
                    return new BoundMethodDeclaration(classType.Name, methodSym, body!);
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

    private void ValidateOverride(TsClassType classType, MethodSymbol method, SourceLocation location)
    {
        for (var baseType = classType.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (!baseType.Methods.TryGetValue(method.Name, out var inherited))
                continue;

            if (!method.Type.Equals(inherited.ReturnType))
            {
                _diagnostics.Error(
                    $"Method '{method.Name}' must return '{inherited.ReturnType}' to override inherited method",
                    location, DiagnosticCode.TS2013);
            }

            if (method.Parameters.Count != inherited.Parameters.Count)
            {
                _diagnostics.Error(
                    $"Method '{method.Name}' must have {inherited.Parameters.Count} parameter(s) to override inherited method",
                    location, DiagnosticCode.TS2014);
                return;
            }

            for (int i = 0; i < method.Parameters.Count; i++)
            {
                if (!method.Parameters[i].Type.Equals(inherited.Parameters[i].Type))
                {
                    _diagnostics.Error(
                        $"Parameter {i + 1} of method '{method.Name}' must have type '{inherited.Parameters[i].Type}' to override inherited method",
                        location, DiagnosticCode.TS2015);
                }
            }

            return;
        }
    }

    private BoundInterfaceDeclaration BindInterface(InterfaceDeclarationSyntax iface)
    {
        if (!_interfaceTypes.TryGetValue(iface.Name, out var ifaceType))
        {
            ifaceType = new TsInterfaceType(iface.Name);
            _interfaceTypes[iface.Name] = ifaceType;
        }

        var sym = new InterfaceSymbol(iface.Name, ifaceType, iface.Range)
        {
            IsExported = iface.Modifiers.Any(m => m.Token.Kind == TokenKind.ExportKeyword)
        };

        _symbolTable.Define(sym);
        PushTypeParameters(iface.GenericParameters);

        foreach (var member in iface.Members)
        {
            if (member is FieldDeclarationSyntax field)
            {
                var fieldType = ResolveType(field.TypeAnnotation);
                ifaceType.Properties[field.Name] = new TsProperty(field.Name, fieldType)
                {
                    IsReadonly = field.IsReadonly
                };
            }
            else if (member is MethodDeclarationSyntax method)
            {
                var returnType = ResolveType(method.ReturnType) ?? TsType.Void;
                var tsMethod = new TsMethod(method.Name, returnType,
                    method.Parameters.Select(p => new TsParameter(p.Name, ResolveType(p.TypeAnnotation))).ToList());
                ifaceType.Methods[method.Name] = tsMethod;
            }
        }

        PopTypeParameters();
        return new BoundInterfaceDeclaration(sym);
    }

    private BoundEnumDeclaration BindEnum(EnumDeclarationSyntax en)
    {
        if (!_enumTypes.TryGetValue(en.Name, out var enumType))
            enumType = new TsEnumType(en.Name);
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
        if (!_importedSymbols.TryGetValue(import.ModulePath, out var symbols))
        {
            _diagnostics.Warning(
                $"Imported module '{import.ModulePath}' not available for type checking",
                import.Range.Start, DiagnosticCode.TS3002);
            return new BoundImportDeclaration(import.ModulePath, import.NamedImports.Select(n => n.Name).ToList());
        }

        if (import.IsWildcard)
        {
            var wildcardName = import.NamedImports.FirstOrDefault()?.Alias;
            if (wildcardName != null)
            {
                foreach (var (name, symbol) in symbols)
                {
                    var alias = CreateImportAlias(symbol, $"{wildcardName}.{name}", import.Range);
                    _symbolTable.Define(alias);
                }
            }
            return new BoundImportDeclaration(import.ModulePath, import.NamedImports.Select(n => n.Name).ToList());
        }

        foreach (var namedImport in import.NamedImports)
        {
            var importedName = namedImport.Alias ?? namedImport.Name;
            if (symbols.TryGetValue(namedImport.Name, out var importedSymbol))
            {
                var sym = importedName == importedSymbol.Name
                    ? importedSymbol
                    : CreateImportAlias(importedSymbol, importedName, namedImport.Range);
                _symbolTable.Define(sym);
                RegisterImportedType(sym);
            }
            else
            {
                _diagnostics.Error(
                    $"'{namedImport.Name}' is not exported from module '{import.ModulePath}'",
                    namedImport.Range.Start, DiagnosticCode.TS2012);
            }
        }

        return new BoundImportDeclaration(import.ModulePath, import.NamedImports.Select(n => n.Name).ToList());
    }

    private void RegisterImportedType(Symbol symbol)
    {
        switch (symbol)
        {
            case ClassSymbol cls:
                _classTypes[cls.Name] = cls.ClassType;
                break;
            case InterfaceSymbol iface:
                _interfaceTypes[iface.Name] = iface.InterfaceType;
                break;
            case EnumSymbol en:
                _enumTypes[en.Name] = en.EnumType;
                break;
        }
    }

    private static Symbol CreateImportAlias(Symbol symbol, string alias, SourceRange location)
    {
        return symbol switch
        {
            FunctionSymbol function => CopyFunction(function, alias, location),
            ClassSymbol cls => new ClassSymbol(alias, cls.ClassType, location) { IsExported = cls.IsExported },
            InterfaceSymbol iface => new InterfaceSymbol(alias, iface.InterfaceType, location) { IsExported = iface.IsExported },
            EnumSymbol en => new EnumSymbol(alias, en.EnumType, location) { IsExported = en.IsExported },
            LocalSymbol local => new LocalSymbol(alias, local.Type, location, local.IsConst)
            {
                IsExported = local.IsExported,
                ConstantInitializer = local.ConstantInitializer
            },
            _ => new LocalSymbol(alias, symbol.Type, location)
        };
    }

    private static FunctionSymbol CopyFunction(FunctionSymbol source, string name, SourceRange location)
    {
        var copy = new FunctionSymbol(name, source.Type, location)
        {
            IsAsync = source.IsAsync,
            IsExported = source.IsExported,
            TargetName = source.TargetName ?? source.Name
        };
        foreach (var parameter in source.Parameters)
            copy.Parameters.Add(new ParameterSymbol(parameter.Name, parameter.Type, parameter.Location));
        return copy;
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
            ForOfStatementSyntax forOf => BindForOf(forOf),
            FunctionDeclarationSyntax nestedFunc => BindFunction(nestedFunc),
            VariableDeclarationListSyntax varList => new BoundBlockStatement(
                varList.Declarations.Select(d => BindVariableDeclaration(d)).ToList()),
            ExpressionStatementSyntax exprStmt => BindExpressionStatement(exprStmt),
            _ => throw new InvalidOperationException($"Unexpected statement type: {stmt.NodeType}")
        };
    }

    private BoundBlockStatement BindBlock(BlockStatementSyntax block)
    {
        _symbolTable.PushScope();

        // Nested function declarations hoist within their block.
        foreach (var nested in block.Statements.OfType<FunctionDeclarationSyntax>())
            DeclareFunctionSignature(nested);

        var statements = new List<BoundNode>();
        foreach (var stmt in block.Statements)
        {
            statements.Add(BindStatement(stmt));
        }
        _symbolTable.PopScope();
        return new BoundBlockStatement(statements);
    }

    private BoundNode BindVariableDeclaration(VariableDeclarationSyntax varDecl)
    {
        // `const f = (a) => ...` declares a function, not a data slot.
        if (varDecl.Initializer is LambdaExpressionSyntax lambdaInit)
            return BindLambdaAsFunction(varDecl.Name, lambdaInit, varDecl.IsExported);

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

            // TypeScript semantics: an unannotated numeric literal infers as
            // `number`, so later reassignments with number-typed expressions
            // stay valid. Annotated declarations keep their strict type.
            if (varDecl.TypeAnnotation == null &&
                initializer is BoundLiteralExpression { Type.Name: "int32" } intLiteral)
            {
                type = TsType.Number;
                initializer = new BoundLiteralExpression(
                    Convert.ToDouble(intLiteral.Value ?? 0), TsType.Number);
            }
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
            if (!TsType.IsCompatibleWith(initializer.Type, declaredType))
            {
                _diagnostics.Error($"Type mismatch: cannot assign {initializer.Type} to {declaredType}",
                    varDecl.Range.Start);
            }
            type = declaredType;
        }

        var symbol = new LocalSymbol(varDecl.Name, type, varDecl.Range, varDecl.IsConst)
        {
            IsExported = varDecl.IsExported
        };
        if (symbol.IsConst && initializer != null &&
            (IsCompileTimeConstant(initializer) || IsModuleConstantExpression(initializer)))
            symbol.ConstantInitializer = initializer;
        DefineWithDepth(symbol);

        return new BoundVariableDeclaration(symbol, initializer);
    }

    private static bool IsCompileTimeConstant(BoundNode node)
    {
        return node switch
        {
            BoundLiteralExpression => true,
            BoundUnaryExpression unary => IsCompileTimeConstant(unary.Operand),
            BoundBinaryExpression binary => IsCompileTimeConstant(binary.Left) && IsCompileTimeConstant(binary.Right),
            BoundConditionalExpression conditional =>
                IsCompileTimeConstant(conditional.Condition)
                && IsCompileTimeConstant(conditional.WhenTrue)
                && IsCompileTimeConstant(conditional.WhenFalse),
            BoundArrayLiteralExpression array => array.Elements.All(IsCompileTimeConstant),
            BoundObjectLiteralExpression obj => obj.Properties.All(property => IsCompileTimeConstant(property.Value)),
            BoundVariableExpression { Symbol: LocalSymbol { ConstantInitializer: BoundNode } } => true,
            _ => false
        };
    }

    private static bool IsModuleConstantExpression(BoundNode node)
    {
        return node switch
        {
            BoundLambdaExpression => true,
            BoundCastExpression cast => IsModuleConstantExpression(cast.Operand) || IsCompileTimeConstant(cast.Operand),
            BoundArrayLiteralExpression array => array.Elements.All(e => IsCompileTimeConstant(e) || IsModuleConstantExpression(e)),
            BoundObjectLiteralExpression obj => obj.Properties.All(property =>
                IsCompileTimeConstant(property.Value) || IsModuleConstantExpression(property.Value)),
            BoundMemberAccessExpression member =>
                IsCompileTimeConstant(member.Object) || IsModuleConstantExpression(member.Object),
            BoundVariableExpression { Symbol: LocalSymbol { ConstantInitializer: BoundNode } } => true,
            _ => false
        };
    }

    private BoundReturnStatement BindReturn(ReturnStatementSyntax ret)
    {
        BoundNode? value = ret.Value != null ? BindExpression(ret.Value) : null;

        if (_currentFunctionReturnType != null && _currentFunctionReturnType != TsType.Void)
        {
            if (value == null)
            {
                _diagnostics.Error(
                    $"Function returns '{_currentFunctionReturnType}' but return statement has no value",
                    ret.Range.Start, DiagnosticCode.TS2009);
            }
            else if (!IsReturnCompatible(value.Type, _currentFunctionReturnType))
            {
                _diagnostics.Error(
                    $"Cannot return '{value.Type}' from function returning '{_currentFunctionReturnType}'",
                    ret.Range.Start, DiagnosticCode.TS2010);
            }
        }
        else if (_currentFunctionReturnType == TsType.Void && value != null)
        {
            _diagnostics.Warning(
                "Function returns 'void' but return statement has a value",
                ret.Range.Start, DiagnosticCode.TS2011);
        }

        return new BoundReturnStatement(value);
    }

    private static TsType NormalizeFunctionReturnType(TsType declaredReturnType, bool isAsync)
    {
        if (!isAsync)
            return declaredReturnType;
        return declaredReturnType is TsPromiseType
            ? declaredReturnType
            : new TsPromiseType(declaredReturnType);
    }

    private static TsType BodyReturnType(TsType functionReturnType, bool isAsync)
    {
        return isAsync && functionReturnType is TsPromiseType promise
            ? promise.ElementType
            : functionReturnType;
    }

    private bool IsReturnCompatible(TsType actualType, TsType expectedType)
    {
        if (TsType.IsCompatibleWith(actualType, expectedType))
            return true;
        return _currentFunctionIsAsync &&
               actualType is TsPromiseType actualPromise &&
               TsType.IsCompatibleWith(actualPromise.ElementType, expectedType);
    }

    private BoundIfStatement BindIf(IfStatementSyntax ifStmt)
    {
        var condition = BindExpression(ifStmt.Condition);
        ValidateCondition(condition, ifStmt.Condition.Range.Start);
        var thenBranch = BindStatement(ifStmt.ThenBranch);
        var elseBranch = ifStmt.ElseBranch != null ? BindStatement(ifStmt.ElseBranch) : null;
        return new BoundIfStatement(condition, thenBranch, elseBranch);
    }

    private BoundWhileStatement BindWhile(WhileStatementSyntax whileStmt)
    {
        var condition = BindExpression(whileStmt.Condition);
        ValidateCondition(condition, whileStmt.Condition.Range.Start);
        var body = BindStatement(whileStmt.Body);
        return new BoundWhileStatement(condition, body);
    }

    private void ValidateCondition(BoundNode condition, SourceLocation location)
    {
        // JS-mode types (number, string, any, nullable, null) are truthy in
        // conditions the way TypeScript allows; TypeSharp's strict primitives
        // (int32, float64, …) still require an explicit bool.
        var type = condition.Type;
        bool allowed = type == TsType.Bool || type == TsType.Void ||
                       type is TsAnyType or TsNullableType or TsNullType or TsUnionType ||
                       type == TsType.Number || type == TsType.String ||
                       type is TsArrayType or TsClassType or TsInterfaceType or TsFunctionType;
        if (!allowed)
        {
            _diagnostics.Error(
                $"Condition has type '{type}', expected 'bool'",
                location, DiagnosticCode.TS2016);
        }
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
                DefineWithDepth(catchSym);
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
            SuperExpressionSyntax superExpr => BindSuper(superExpr),
            AwaitExpressionSyntax awaitExpr => BindAwait(awaitExpr),
            ObjectLiteralExpressionSyntax objLit => BindObjectLiteral(objLit),
            ArrayLiteralExpressionSyntax arrLit => BindArrayLiteral(arrLit),
            IndexExpressionSyntax indexExpr => BindIndexExpression(indexExpr),
            LambdaExpressionSyntax lambda => BindInlineLambda(lambda),
            AsExpressionSyntax asExpr => BindAsExpression(asExpr),
            TypeofExpressionSyntax typeofExpr => BindTypeof(typeofExpr),
            TemplatePartSyntax templatePart => new BoundCastExpression(
                BindExpression(templatePart.Expression), TsType.String),
            NonNullAssertionSyntax nonNull => BindNonNullAssertion(nonNull),
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
                if (lit.Token.Value is ulong ul)
                {
                    type = lit.Token.Text.EndsWith("n", StringComparison.Ordinal)
                        ? TsType.BigInt
                        : TsType.UInt64;
                    value = ul;
                }
                else if (lit.Token.Value is long l)
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
                if (lit.Token.Value is decimal decVal)
                {
                    type = TsType.Decimal;
                    value = decVal;
                }
                else
                {
                    type = TsType.Float64;
                    value = lit.Token.Value ?? 0.0;
                }
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
                type = TsType.Null;
                value = null;
                break;
            default:
                type = TsType.Void;
                value = null;
                break;
        }

        return new BoundLiteralExpression(value, type);
    }

    private BoundNode BindIdentifier(IdentifierExpressionSyntax id)
    {
        var symbol = _symbolTable.Lookup(id.Name);
        if (symbol == null)
        {
            switch (id.Name)
            {
                case "undefined":
                    return new BoundLiteralExpression(null, TsType.Null);
                case "Infinity":
                    return new BoundLiteralExpression(double.PositiveInfinity, TsType.Number);
                case "NaN":
                    return new BoundLiteralExpression(double.NaN, TsType.Number);
            }

            _diagnostics.Error($"Undefined symbol: '{id.Name}'", id.Range.Start);
            return new BoundVariableExpression(
                new LocalSymbol(id.Name, TsType.Void, id.Range));
        }

        RegisterPossibleCapture(symbol);
        return new BoundVariableExpression(symbol);
    }

    private BoundBinaryExpression BindBinary(BinaryExpressionSyntax bin)
    {
        var left = BindExpression(bin.Left);
        var right = BindExpression(bin.Right);
        var resultType = InferBinaryResultType(left.Type, right.Type, bin.OperatorToken.Kind);

        if (resultType == TsType.Void && left.Type != TsType.Void && right.Type != TsType.Void)
        {
            _diagnostics.Error(
                $"Operator '{bin.OperatorToken.Text}' cannot be applied to types '{left.Type}' and '{right.Type}'",
                bin.OperatorToken.Location);
        }

        return new BoundBinaryExpression(left, bin.OperatorToken.Kind, right, resultType);
    }

    private BoundUnaryExpression BindUnary(UnaryExpressionSyntax unary)
    {
        var operand = BindExpression(unary.Operand);
        var resultType = InferUnaryResultType(operand.Type, unary.OperatorToken.Kind);

        return new BoundUnaryExpression(unary.OperatorToken.Kind, operand, unary.IsPrefix, resultType);
    }

    private BoundNode BindCall(CallExpressionSyntax call)
    {
        var callee = BindExpression(call.Callee);

        TsType returnType = TsType.Void;
        List<(string Name, TsType Type)>? expectedParams = null;

        if (callee is BoundVariableExpression { Symbol: ClassSymbol { Name: "Array" } })
        {
            var args = BindCallArguments(call.Arguments, null);
            return new BoundArrayConstructionExpression(args, new TsArrayType(TsType.Any));
        }

        if (callee is BoundVariableExpression varExpr && varExpr.Symbol is FunctionSymbol funcSym)
        {
            returnType = funcSym.Type;
            expectedParams = funcSym.HasDynamicSignature
                ? null
                : funcSym.Parameters.Select(p => (p.Name, p.Type)).ToList();
        }
        else if (callee.Type is TsFunctionType fnType)
        {
            returnType = fnType.ReturnType;
            expectedParams = fnType.Parameters.Select(p => (p.Name, p.Type)).ToList();
        }
        else if (callee is BoundMemberAccessExpression memberExpr && memberExpr.Member is MethodSymbol methodSym)
        {
            returnType = methodSym.Type;
            expectedParams = methodSym.Parameters.Count > 0
                ? methodSym.Parameters.Select(p => (p.Name, p.Type)).ToList()
                : null;
        }
        else if (callee is BoundSuperExpression superExpr && superExpr.BaseClass.Constructor != null)
        {
            returnType = TsType.Void;
            expectedParams = superExpr.BaseClass.Constructor.Parameters
                .Select(p => (p.Name, p.Type))
                .ToList();
        }

        var genericArguments = new Dictionary<string, TsType>(StringComparer.Ordinal);
        var boundArgs = BindCallArguments(call.Arguments, expectedParams, genericArguments);
        if (expectedParams != null)
        {
            var substitutedParams = expectedParams
                .Select(p => (p.Name, Type: TsType.Substitute(p.Type, genericArguments)))
                .ToList();
            ValidateCallArguments(boundArgs, substitutedParams, call.Range.Start);
        }

        return new BoundCallExpression(callee, boundArgs, TsType.Substitute(returnType, genericArguments));
    }

    private List<BoundNode> BindCallArguments(
        IReadOnlyList<ExpressionSyntax> arguments,
        IReadOnlyList<(string Name, TsType Type)>? expectedParams,
        Dictionary<string, TsType>? genericArguments = null)
    {
        var result = new List<BoundNode>(arguments.Count);
        for (int i = 0; i < arguments.Count; i++)
        {
            var expectedType = expectedParams != null && i < expectedParams.Count
                ? TsType.Substitute(expectedParams[i].Type, genericArguments ?? new Dictionary<string, TsType>())
                : null;

            if (arguments[i] is LambdaExpressionSyntax lambda && expectedType is TsFunctionType fnType)
                result.Add(BindInlineLambda(lambda, fnType));
            else if (arguments[i] is ObjectLiteralExpressionSyntax objLit && expectedType != null)
                result.Add(BindObjectLiteral(objLit, expectedType));
            else
                result.Add(BindExpression(arguments[i]));

            if (expectedParams != null && genericArguments != null && i < expectedParams.Count)
                InferGenericArguments(expectedParams[i].Type, result[i].Type, genericArguments);
        }

        return result;
    }

    private static void InferGenericArguments(
        TsType expected,
        TsType actual,
        Dictionary<string, TsType> genericArguments)
    {
        switch (expected)
        {
            case TsTypeParameter parameter:
                if (!genericArguments.ContainsKey(parameter.Name))
                    genericArguments[parameter.Name] = actual;
                return;
            case TsArrayType expectedArray when actual is TsArrayType actualArray:
                InferGenericArguments(expectedArray.ElementType, actualArray.ElementType, genericArguments);
                return;
            case TsTupleType expectedTuple when actual is TsTupleType actualTuple:
                for (int i = 0; i < expectedTuple.ElementTypes.Count && i < actualTuple.ElementTypes.Count; i++)
                    InferGenericArguments(expectedTuple.ElementTypes[i], actualTuple.ElementTypes[i], genericArguments);
                return;
            case TsMapType expectedMap when actual is TsMapType actualMap:
                InferGenericArguments(expectedMap.KeyType, actualMap.KeyType, genericArguments);
                InferGenericArguments(expectedMap.ValueType, actualMap.ValueType, genericArguments);
                return;
            case TsNullableType expectedNullable:
                InferGenericArguments(expectedNullable.ElementType,
                    actual is TsNullableType actualNullable ? actualNullable.ElementType : actual,
                    genericArguments);
                return;
            case TsPromiseType expectedPromise when actual is TsPromiseType actualPromise:
                InferGenericArguments(expectedPromise.ElementType, actualPromise.ElementType, genericArguments);
                return;
            case TsFunctionType expectedFunction when actual is TsFunctionType actualFunction:
                for (int i = 0; i < expectedFunction.Parameters.Count && i < actualFunction.Parameters.Count; i++)
                    InferGenericArguments(expectedFunction.Parameters[i].Type, actualFunction.Parameters[i].Type, genericArguments);
                InferGenericArguments(expectedFunction.ReturnType, actualFunction.ReturnType, genericArguments);
                return;
            case TsGenericType expectedGeneric when actual is TsGenericType actualGeneric:
                if (!expectedGeneric.Definition.Equals(actualGeneric.Definition))
                    return;
                for (int i = 0; i < expectedGeneric.TypeArguments.Count && i < actualGeneric.TypeArguments.Count; i++)
                    InferGenericArguments(expectedGeneric.TypeArguments[i], actualGeneric.TypeArguments[i], genericArguments);
                return;
        }
    }

    private void ValidateCallArguments(
        IReadOnlyList<BoundNode> args,
        IReadOnlyList<(string Name, TsType Type)> expectedParams,
        SourceLocation location)
    {
        if (args.Count != expectedParams.Count)
        {
            _diagnostics.Error(
                $"Expected {expectedParams.Count} argument(s) but got {args.Count}",
                location);
            return;
        }

        for (int i = 0; i < args.Count; i++)
        {
            var argType = args[i].Type;
            var paramType = expectedParams[i].Type;
            if (!TsType.IsCompatibleWith(argType, paramType))
            {
                _diagnostics.Error(
                    $"Argument {i + 1}: cannot assign '{argType}' to parameter '{expectedParams[i].Name}' of type '{paramType}'",
                    location);
            }
        }
    }

    private BoundNode BindMemberAccess(MemberAccessExpressionSyntax member)
    {
        var obj = BindExpression(member.Object);

        // Known static numeric constants (Math.PI, Number.MAX_SAFE_INTEGER, …)
        // fold to literals so no runtime lookup is needed.
        if (obj is BoundVariableExpression { Symbol: ClassSymbol staticOwner } &&
            StaticNumericConstants.TryGetValue((staticOwner.Name, member.MemberName), out double constant))
        {
            return new BoundLiteralExpression(constant, TsType.Number);
        }

        Symbol? memberSym = null;

        var objType = obj.Type;
        if (objType is TsNullableType nullable)
            objType = nullable.ElementType;
        IReadOnlyDictionary<string, TsType> genericMap = new Dictionary<string, TsType>();
        if (objType is TsGenericType generic)
        {
            if (generic.Definition is TsClassType { Name: "Map" } &&
                member.MemberName == "get" && generic.TypeArguments.Count == 2)
            {
                var getSymbol = new MethodSymbol("get", new TsNullableType(generic.TypeArguments[1]), member.Range);
                getSymbol.Parameters.Add(new ParameterSymbol("key", generic.TypeArguments[0], member.Range));
                return new BoundMemberAccessExpression(obj, getSymbol);
            }
            if (generic.Definition is TsClassType genericClass)
                genericMap = TsType.CreateGenericMap(genericClass.TypeParameters, generic.TypeArguments);
            else if (generic.Definition is TsInterfaceType genericInterface)
                genericMap = TsType.CreateGenericMap(genericInterface.TypeParameters, generic.TypeArguments);
            objType = generic.Definition;
        }

        if (objType is TsClassType classType)
        {
            for (var current = classType; current != null && memberSym == null; current = current.BaseType)
            {
                if (current.Fields.TryGetValue(member.MemberName, out var field))
                {
                    memberSym = new FieldSymbol(member.MemberName, TsType.Substitute(field.Type, genericMap), member.Range)
                    {
                        IsReadonly = field.IsReadonly
                    };
                }
                else if (current.Methods.TryGetValue(member.MemberName, out var method))
                {
                    memberSym = CreateMethodSymbol(method, current.Name, member.Range, genericMap);
                }
                else if (current.Properties.TryGetValue(member.MemberName, out var prop))
                {
                    memberSym = new PropertySymbol(member.MemberName, TsType.Substitute(prop.Type, genericMap), member.Range)
                    {
                        IsReadonly = prop.IsReadonly
                    };
                }
            }
        }
        else if (objType is TsInterfaceType ifaceType)
        {
            if (ifaceType.Properties.TryGetValue(member.MemberName, out var prop))
            {
                memberSym = new PropertySymbol(member.MemberName, TsType.Substitute(prop.Type, genericMap), member.Range)
                {
                    IsReadonly = prop.IsReadonly
                };
            }
            else if (ifaceType.Methods.TryGetValue(member.MemberName, out var method))
            {
                memberSym = CreateMethodSymbol(method, null, member.Range, genericMap);
            }
        }
        else if (objType is TsArrayType arrayType)
        {
            memberSym = BindArrayMember(arrayType, member.MemberName, member.Range);
        }
        else if (objType is TsTupleType tupleType)
        {
            memberSym = BindArrayMember(new TsArrayType(tupleType.UnifiedElementType()), member.MemberName, member.Range);
        }
        else if (objType is TsPrimitiveType { Name: "string" })
        {
            memberSym = BindStringMember(member.MemberName, member.Range);
        }
        else if (objType is TsAnyType or TsTypeParameter)
        {
            // Dynamic / erased-generic receivers: member exists with type any.
            memberSym = new PropertySymbol(member.MemberName, TsType.Any, member.Range);
        }

        if (memberSym == null)
        {
            _diagnostics.Error($"No member '{member.MemberName}' on type '{obj.Type}'", member.Range.Start);
            memberSym = new PropertySymbol(member.MemberName, TsType.Void, member.Range);
        }

        var resultType = member.IsNullConditional && memberSym.Type is not TsNullableType
            ? new TsNullableType(memberSym.Type)
            : memberSym.Type;

        return new BoundMemberAccessExpression(obj, memberSym, member.IsNullConditional, resultType);
    }

    // Array/string builtin members: methods dispatch through the VM builtin
    // table under Array::/String:: names; parameter lists stay empty so the
    // binder does not enforce a fixed arity on variadic-style JS members.
    private Symbol? BindArrayMember(TsArrayType arrayType, string name, SourceRange range)
    {
        switch (name)
        {
            case "length":
                return new PropertySymbol("length", TsType.Int32, range);
            case "push":
            case "unshift":
                return BuiltinMethod(name, TsType.Int32, "Array", range);
            case "pop":
            case "shift":
                return BuiltinMethod(name, arrayType.ElementType, "Array", range);
            case "reverse":
            case "slice":
            case "concat":
            case "fill":
                return BuiltinMethod(name, arrayType, "Array", range);
            case "includes":
                return BuiltinMethod(name, TsType.Bool, "Array", range);
            case "indexOf":
            case "lastIndexOf":
                return BuiltinMethod(name, TsType.Int32, "Array", range);
            case "join":
                return BuiltinMethod(name, TsType.String, "Array", range);
            case "map":
            case "flatMap":
                return BuiltinMethod(name, new TsArrayType(TsType.Any), "Array", range);
            case "filter":
            case "sort":
                return BuiltinMethod(name, arrayType, "Array", range);
            case "forEach":
                return BuiltinMethod(name, TsType.Void, "Array", range);
            case "reduce":
                return BuiltinMethod(name, TsType.Any, "Array", range);
            case "some":
            case "every":
                return BuiltinMethod(name, TsType.Bool, "Array", range);
            case "find":
                return BuiltinMethod(name, new TsNullableType(arrayType.ElementType), "Array", range);
            case "findIndex":
                return BuiltinMethod(name, TsType.Int32, "Array", range);
            case "flat":
                return BuiltinMethod(name, new TsArrayType(TsType.Any), "Array", range);
            default:
                return null;
        }
    }

    private Symbol? BindStringMember(string name, SourceRange range)
    {
        switch (name)
        {
            case "length":
                return new PropertySymbol("length", TsType.Int32, range);
            case "charAt":
            case "toUpperCase":
            case "toLowerCase":
            case "substring":
            case "slice":
            case "trim":
            case "repeat":
            case "concat":
            case "replace":
            case "replaceAll":
            case "padStart":
            case "padEnd":
                return BuiltinMethod(name, TsType.String, "String", range);
            case "charCodeAt":
            case "indexOf":
            case "lastIndexOf":
                return BuiltinMethod(name, TsType.Int32, "String", range);
            case "includes":
            case "startsWith":
            case "endsWith":
                return BuiltinMethod(name, TsType.Bool, "String", range);
            case "split":
                return BuiltinMethod(name, new TsArrayType(TsType.String), "String", range);
            default:
                return null;
        }
    }

    private static MethodSymbol BuiltinMethod(string name, TsType returnType, string declaringClass, SourceRange range)
    {
        return new MethodSymbol(name, returnType, range)
        {
            DeclaringClassName = declaringClass
        };
    }

    private static MethodSymbol CreateMethodSymbol(
        TsMethod method,
        string? declaringClass,
        SourceRange range,
        IReadOnlyDictionary<string, TsType>? genericMap = null)
    {
        genericMap ??= new Dictionary<string, TsType>();
        var sym = new MethodSymbol(method.Name, TsType.Substitute(method.ReturnType, genericMap), range)
        {
            DeclaringClassName = declaringClass,
            IsAsync = method.IsAsync
        };
        foreach (var p in method.Parameters)
            sym.Parameters.Add(new ParameterSymbol(p.Name, TsType.Substitute(p.Type, genericMap), range));
        return sym;
    }

    private BoundAssignmentExpression BindAssignment(AssignmentExpressionSyntax assign)
    {
        var target = BindExpression(assign.Target);
        var value = BindExpression(assign.Value);

        // Compound assignments (`x += y`) desugar to `x = x <op> y`.
        var compoundOp = assign.OperatorToken.Kind switch
        {
            TokenKind.PlusEquals => TokenKind.Plus,
            TokenKind.MinusEquals => TokenKind.Minus,
            TokenKind.StarEquals => TokenKind.Star,
            TokenKind.SlashEquals => TokenKind.Slash,
            TokenKind.PercentEquals => TokenKind.Percent,
            TokenKind.AmpersandEquals => TokenKind.Ampersand,
            TokenKind.PipeEquals => TokenKind.Pipe,
            TokenKind.CaretEquals => TokenKind.Caret,
            TokenKind.ShiftLeftEquals => TokenKind.ShiftLeft,
            TokenKind.ShiftRightEquals => TokenKind.ShiftRight,
            TokenKind.StarStarEquals => TokenKind.StarStar,
            TokenKind.AmpersandAmpersandEquals => TokenKind.AmpersandAmpersand,
            TokenKind.PipePipeEquals => TokenKind.PipePipe,
            TokenKind.QuestionQuestionEquals => TokenKind.QuestionQuestion,
            _ => (TokenKind?)null
        };
        if (compoundOp is TokenKind op)
        {
            var resultType = InferBinaryResultType(target.Type, value.Type, op);
            if (resultType == TsType.Void && target.Type != TsType.Void && value.Type != TsType.Void)
            {
                _diagnostics.Error(
                    $"Operator '{assign.OperatorToken.Text}' cannot be applied to types '{target.Type}' and '{value.Type}'",
                    assign.OperatorToken.Location);
            }
            value = new BoundBinaryExpression(target, op, value, resultType);
        }

        if (target is BoundVariableExpression varExpr && varExpr.Symbol is LocalSymbol local && local.IsConst)
        {
            _diagnostics.Error($"Cannot assign to const variable '{local.Name}'", assign.Range.Start);
        }
        else if (target is BoundMemberAccessExpression memberExpr)
        {
            bool isReadonly = memberExpr.Member switch
            {
                FieldSymbol field => field.IsReadonly,
                PropertySymbol property => property.IsReadonly,
                _ => false
            };
            if (isReadonly)
                _diagnostics.Error($"Cannot assign to readonly member '{memberExpr.Member.Name}'", assign.Range.Start);
        }

        var targetType = target.Type;
        var valueType = value.Type;
        if (!TsType.IsCompatibleWith(valueType, targetType))
        {
            _diagnostics.Error(
                $"Cannot assign '{valueType}' to variable of type '{targetType}'",
                assign.Range.Start);
        }

        return new BoundAssignmentExpression(target, value);
    }

    private BoundConditionalExpression BindConditional(ConditionalExpressionSyntax cond)
    {
        var condition = BindExpression(cond.Condition);
        ValidateCondition(condition, cond.Condition.Range.Start);
        var whenTrue = BindExpression(cond.WhenTrue);
        var whenFalse = BindExpression(cond.WhenFalse);

        TsType resultType = whenTrue.Type.IsAssignableTo(whenFalse.Type) ? whenTrue.Type :
                           whenFalse.Type.IsAssignableTo(whenTrue.Type) ? whenFalse.Type :
                           whenTrue.Type;

        return new BoundConditionalExpression(condition, whenTrue, whenFalse, resultType);
    }

    private BoundNode BindNew(NewExpressionSyntax newExpr)
    {
        var type = ResolveType(newExpr.Type);
        var args = newExpr.Arguments.Select(BindExpression).ToList();

        // `new Array(n)` builds a real array value, not a class instance.
        if (type is TsArrayType arrayNew)
            return new BoundArrayConstructionExpression(args, arrayNew);

        if (type is TsClassType { Constructor: not null } ctorClass)
        {
            var ctorParams = ctorClass.Constructor.Parameters;
            if (args.Count != ctorParams.Count)
            {
                _diagnostics.Error(
                    $"Constructor of '{ctorClass.Name}' expects {ctorParams.Count} argument(s) but got {args.Count}",
                    newExpr.Range.Start);
            }
            else
            {
                for (int i = 0; i < args.Count; i++)
                {
                    if (!TsType.IsCompatibleWith(args[i].Type, ctorParams[i].Type))
                    {
                        _diagnostics.Error(
                            $"Argument {i + 1}: cannot assign '{args[i].Type}' to constructor parameter '{ctorParams[i].Name}' of type '{ctorParams[i].Type}'",
                            newExpr.Range.Start);
                    }
                }
            }
        }

        return new BoundNewExpression(type, args);
    }

    private BoundThisExpression BindThis(ThisExpressionSyntax thisExpr)
    {
        var type = _currentClassType ?? new TsClassType("Unknown");
        return new BoundThisExpression(type);
    }

    private BoundSuperExpression BindSuper(SuperExpressionSyntax superExpr)
    {
        if (!_inConstructor || _currentClassType == null || _currentClassType.BaseType == null || _currentClassType.BaseType is not TsClassType baseClass)
        {
            _diagnostics.Error("'super' can only be used in a derived class constructor",
                superExpr.Range.Start, DiagnosticCode.TS2001);
            return new BoundSuperExpression(new TsClassType("Unknown"));
        }
        return new BoundSuperExpression(baseClass);
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
        var structuralType = new TsClassType("Object");
        TsType resultType = structuralType;
        IReadOnlyDictionary<string, TsType> genericMap = new Dictionary<string, TsType>();
        var originalExpectedType = expectedType;
        if (expectedType is TsGenericType expectedGeneric)
        {
            if (expectedGeneric.Definition is TsInterfaceType genericInterface)
            {
                genericMap = TsType.CreateGenericMap(genericInterface.TypeParameters, expectedGeneric.TypeArguments);
                expectedType = genericInterface;
            }
            else if (expectedGeneric.Definition is TsClassType genericClass)
            {
                genericMap = TsType.CreateGenericMap(genericClass.TypeParameters, expectedGeneric.TypeArguments);
                expectedType = genericClass;
            }
        }

        if (expectedType is TsInterfaceType ifaceType)
        {
            resultType = originalExpectedType is TsGenericType ? originalExpectedType : ifaceType;
            foreach (var prop in objLit.Properties)
            {
                TsType expectedPropertyType;
                if (ifaceType.Properties.TryGetValue(prop.Key, out var ifaceProp))
                {
                    expectedPropertyType = TsType.Substitute(ifaceProp.Type, genericMap);
                }
                else if (ifaceType.Methods.TryGetValue(prop.Key, out var ifaceMethod))
                {
                    expectedPropertyType = new TsFunctionType(
                        ifaceMethod.Parameters
                            .Select(p => new TsParameter(p.Name, TsType.Substitute(p.Type, genericMap)))
                            .ToList(),
                        TsType.Substitute(ifaceMethod.ReturnType, genericMap));
                }
                else
                {
                    _diagnostics.Error(
                        $"Object literal property '{prop.Key}' does not exist on interface '{ifaceType.Name}'",
                        prop.Range.Start);
                    var unknownValue = BindExpression(prop.Value);
                    properties.Add(new BoundObjectPropertyNode(prop.Key, unknownValue, unknownValue.Type));
                    continue;
                }

                var value = prop.Value switch
                {
                    ObjectLiteralExpressionSyntax nested => BindObjectLiteral(nested, expectedPropertyType),
                    LambdaExpressionSyntax lambda when expectedPropertyType is TsFunctionType fnType =>
                        BindInlineLambda(lambda, fnType),
                    _ => BindExpression(prop.Value)
                };

                if (!TsType.IsCompatibleWith(value.Type, expectedPropertyType))
                {
                    _diagnostics.Error(
                        $"Property '{prop.Key}': cannot assign '{value.Type}' to '{expectedPropertyType}'",
                        prop.Range.Start);
                }
                properties.Add(new BoundObjectPropertyNode(prop.Key, value, expectedPropertyType));
            }

            foreach (var required in ifaceType.Properties.Values)
            {
                if (required.Type is TsNullableType)
                    continue;
                if (!objLit.Properties.Any(p => p.Key == required.Name))
                {
                    _diagnostics.Error(
                        $"Property '{required.Name}' is missing in object literal for interface '{ifaceType.Name}'",
                        objLit.Range.Start);
                }
            }

            foreach (var required in ifaceType.Methods.Values)
            {
                if (!objLit.Properties.Any(p => p.Key == required.Name))
                {
                    _diagnostics.Error(
                        $"Method '{required.Name}' is missing in object literal for interface '{ifaceType.Name}'",
                        objLit.Range.Start);
                }
            }
        }
        else
        {
            foreach (var prop in objLit.Properties)
            {
                var value = BindExpression(prop.Value);
                properties.Add(new BoundObjectPropertyNode(prop.Key, value, value.Type));
                structuralType.Fields[prop.Key] = new TsField(prop.Key, value.Type);
            }
        }

        return new BoundObjectLiteralExpression(properties, resultType);
    }

    private BoundLambdaExpression BindInlineLambda(LambdaExpressionSyntax lambda, TsFunctionType? contextualType = null)
    {
        var name = CreateLambdaName(lambda);
        var function = BindLambdaAsFunction(name, lambda, exported: false, contextualType);

        var fnType = new TsFunctionType(
            function.Symbol.Parameters.Select(p => new TsParameter(p.Name, p.Type)).ToList(),
            function.Symbol.Type);
        return new BoundLambdaExpression(function, fnType);
    }

    private string CreateLambdaName(LambdaExpressionSyntax lambda)
    {
        var source = lambda.Range.Start.Source;
        if (string.IsNullOrWhiteSpace(source))
            source = "module";

        var safeConsumer = ToSafeName(_currentSourceFileName);
        var safeSource = ToSafeName(source);
        return $"$lambda_{safeConsumer}_{safeSource}_{lambda.Range.Start.Line}_{lambda.Range.Start.Column}_{_lambdaCounter++}";
    }

    private static string ToSafeName(string value)
    {
        var fileName = Path.GetFileNameWithoutExtension(value);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = value;

        var safe = new string(fileName.Select(ch =>
            char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "module" : safe;
    }

    private BoundNode BindTypeof(TypeofExpressionSyntax typeofExpr)
    {
        var operand = BindExpression(typeofExpr.Operand);

        // Statically-known operand types fold to their JS typeof string.
        string? known = operand.Type switch
        {
            TsPrimitiveType { Name: "bigint" } => "bigint",
            TsPrimitiveType { IsNumericType: true } => "number",
            TsPrimitiveType { Name: "string" } => "string",
            TsPrimitiveType { Name: "bool" } => "boolean",
            TsFunctionType => "function",
            _ => null
        };
        if (known != null)
            return new BoundLiteralExpression(known, TsType.String);

        return new BoundTypeofExpression(operand);
    }

    private BoundNode BindNonNullAssertion(NonNullAssertionSyntax nonNull)
    {
        var operand = BindExpression(nonNull.Expression);
        return operand.Type switch
        {
            TsNullableType nullable => new BoundCastExpression(operand, nullable.ElementType),
            TsUnionType union when union.Types.Any(t => t is TsNullType or TsNullableType) =>
                new BoundCastExpression(operand,
                    union.Types.FirstOrDefault(t => t is not TsNullType and not TsNullableType) ?? TsType.Any),
            _ => operand
        };
    }

    private BoundNode BindAsExpression(AsExpressionSyntax asExpr)
    {
        var operand = BindExpression(asExpr.Expression);
        if (asExpr.TargetType is NamedTypeSyntax { Name: "const" })
            return operand;
        var targetType = ResolveType(asExpr.TargetType);
        return operand.Type.Equals(targetType)
            ? operand
            : new BoundCastExpression(operand, targetType);
    }

    private BoundNode BindForOf(ForOfStatementSyntax forOf)
    {
        // Desugars to an index-based loop over a captured iterable:
        //   { const $arr = iterable; for (let $i = 0; $i < $arr.length; $i = $i + 1) { const x = $arr[$i]; body } }
        var iterable = BindExpression(forOf.Iterable);
        TsType elementType = iterable.Type switch
        {
            TsArrayType arr => arr.ElementType,
            TsPrimitiveType { Name: "string" } => TsType.String,
            _ => TsType.Any
        };
        if (elementType is TsAnyType && iterable.Type is not TsAnyType)
        {
            _diagnostics.Error($"for...of requires an array or string, got '{iterable.Type}'",
                forOf.Iterable.Range.Start);
        }

        _symbolTable.PushScope();
        int id = _forOfCounter++;

        var arrSym = new LocalSymbol($"$of_arr_{id}", iterable.Type, forOf.Range, isConst: true);
        var idxSym = new LocalSymbol($"$of_idx_{id}", TsType.Int32, forOf.Range);
        DefineWithDepth(arrSym);
        DefineWithDepth(idxSym);

        var arrDecl = new BoundVariableDeclaration(arrSym, iterable);
        var idxDecl = new BoundVariableDeclaration(idxSym, new BoundLiteralExpression(0, TsType.Int32));

        var lengthAccess = new BoundMemberAccessExpression(
            new BoundVariableExpression(arrSym),
            new PropertySymbol("length", TsType.Int32, forOf.Range));
        var condition = new BoundBinaryExpression(
            new BoundVariableExpression(idxSym), TokenKind.LessThan, lengthAccess, TsType.Bool);
        var increment = new BoundAssignmentExpression(
            new BoundVariableExpression(idxSym),
            new BoundBinaryExpression(
                new BoundVariableExpression(idxSym), TokenKind.Plus,
                new BoundLiteralExpression(1, TsType.Int32), TsType.Int32));

        var elementSym = new LocalSymbol(forOf.VariableName, elementType, forOf.Range, forOf.IsConst);
        DefineWithDepth(elementSym);
        var elementDecl = new BoundVariableDeclaration(elementSym,
            new BoundIndexExpression(
                new BoundVariableExpression(arrSym),
                new BoundVariableExpression(idxSym),
                elementType));

        var body = BindStatement(forOf.Body);
        var loopBody = new BoundBlockStatement(new List<BoundNode> { elementDecl, body });
        var loop = new BoundForStatement(idxDecl, condition, increment, loopBody);

        _symbolTable.PopScope();
        return new BoundBlockStatement(new List<BoundNode> { arrDecl, loop });
    }

    private BoundArrayLiteralExpression BindArrayLiteral(ArrayLiteralExpressionSyntax arrLit)
    {
        var elements = arrLit.Elements.Select(BindExpression).ToList();
        var elementType = elements.Count > 0 ? elements[0].Type : TsType.Any;

        for (int i = 1; i < elements.Count; i++)
        {
            if (!TsType.IsCompatibleWith(elements[i].Type, elementType))
            {
                _diagnostics.Error(
                    $"Array element {i + 1} has type '{elements[i].Type}', expected '{elementType}'",
                    arrLit.Elements[i].Range.Start);
            }
        }

        return new BoundArrayLiteralExpression(elements, new TsArrayType(elementType));
    }

    private BoundIndexExpression BindIndexExpression(IndexExpressionSyntax indexExpr)
    {
        var obj = BindExpression(indexExpr.Object);
        var index = BindExpression(indexExpr.Index);

        var resultType = obj.Type switch
        {
            TsArrayType arr => arr.ElementType,
            TsClassType { Name: "Uint8Array" } => TsType.Number,
            TsTupleType tuple when index is BoundLiteralExpression { Value: int constIdx } &&
                constIdx >= 0 && constIdx < tuple.ElementTypes.Count => tuple.ElementTypes[constIdx],
            TsTupleType tuple => tuple.UnifiedElementType(),
            TsMapType map => new TsNullableType(map.ValueType),
            TsPrimitiveType { Name: "string" } => TsType.String,
            _ => TsType.Any
        };

        if ((obj.Type is TsArrayType || obj.Type is TsClassType { Name: "Uint8Array" }) &&
            index.Type is not TsPrimitiveType { IsNumericType: true } &&
            index.Type is not TsAnyType)
        {
            _diagnostics.Error($"Index must be numeric, got '{index.Type}'",
                indexExpr.Index.Range.Start);
        }

        return new BoundIndexExpression(obj, index, resultType);
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
            UnionTypeSyntax union => ResolveUnionType(union),
            FunctionTypeSyntax fn => new TsFunctionType(
                fn.Parameters.Select(p => new TsParameter(p.Name, ResolveType(p.TypeAnnotation))).ToList(),
                ResolveType(fn.ReturnType)),
            ObjectTypeSyntax anon => ResolveObjectType(anon),
            TupleTypeSyntax tuple => new TsTupleType(tuple.ElementTypes.Select(ResolveType).ToList()),
            _ => TsType.Void
        };
    }

    private TsType ResolveUnionType(UnionTypeSyntax union)
    {
        var members = union.Types.Select(ResolveType).ToList();
        // `T | null` / `T | undefined` normalizes to nullable T so member
        // access and null-flow analysis see one canonical shape.
        var nonNull = members.Where(m => m is not TsNullType).ToList();
        if (nonNull.Count < members.Count)
        {
            return nonNull.Count == 1
                ? new TsNullableType(nonNull[0])
                : new TsNullableType(new TsUnionType(nonNull));
        }
        return new TsUnionType(members);
    }

    private TsInterfaceType ResolveObjectType(ObjectTypeSyntax objType)
    {
        var iface = new TsInterfaceType($"$anon_{objType.Range.Start.Line}_{objType.Range.Start.Column}");
        foreach (var m in objType.Members)
        {
            var memberType = ResolveType(m.Type);
            if (m.IsOptional && memberType is not TsNullableType)
                memberType = new TsNullableType(memberType);
            iface.Properties[m.Name] = new TsProperty(m.Name, memberType);
        }
        return iface;
    }

    private void PushTypeParameters(IEnumerable<GenericParameterSyntax> parameters)
    {
        var scope = new Dictionary<string, TsTypeParameter>(StringComparer.Ordinal);
        foreach (var p in parameters)
            scope[p.Name] = new TsTypeParameter(p.Name);
        _typeParameterScopes.Add(scope);
    }

    private void PopTypeParameters()
    {
        if (_typeParameterScopes.Count > 0)
            _typeParameterScopes.RemoveAt(_typeParameterScopes.Count - 1);
    }

    private TsTypeParameter? LookupTypeParameter(string name)
    {
        for (int i = _typeParameterScopes.Count - 1; i >= 0; i--)
        {
            if (_typeParameterScopes[i].TryGetValue(name, out var param))
                return param;
        }
        return null;
    }

    private TsType ResolveNamedType(NamedTypeSyntax named)
    {
        if (named.Name is "null" or "undefined")
            return TsType.Null;

        if (LookupTypeParameter(named.Name) is TsTypeParameter typeParam)
            return typeParam;

        if (named.Name == "Array")
        {
            return new TsArrayType(named.TypeArguments.Count > 0
                ? ResolveType(named.TypeArguments[0])
                : TsType.Any);
        }

        if (named.Name == "Promise")
        {
            return new TsPromiseType(named.TypeArguments.Count > 0
                ? ResolveType(named.TypeArguments[0])
                : TsType.Any);
        }

        if (named.Name is "Uint8Array" or "Uint8ClampedArray")
            return _classTypes.TryGetValue("Uint8Array", out var bytesType)
                ? bytesType
                : new TsClassType("Uint8Array");

        if (named.Name == "ArrayBuffer")
            return _classTypes.TryGetValue("ArrayBuffer", out var bufferType)
                ? bufferType
                : new TsClassType("ArrayBuffer");

        if (_typeAliases.TryGetValue(named.Name, out var aliased))
            return aliased;

        // User-declared interfaces shadow same-named builtin classes (a repo
        // defining its own `Map` interface must win over the builtin).
        if (_interfaceTypes.TryGetValue(named.Name, out var userIface))
        {
            if (named.TypeArguments.Count > 0)
            {
                var typeArgs = named.TypeArguments.Select(ResolveType).ToList();
                return new TsGenericType(userIface, typeArgs);
            }
            return userIface;
        }

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
        {
            if (named.TypeArguments.Count > 0)
            {
                var typeArgs = named.TypeArguments.Select(ResolveType).ToList();
                return new TsGenericType(ifaceType, typeArgs);
            }
            return ifaceType;
        }

        if (_enumTypes.TryGetValue(named.Name, out var enumType))
            return enumType;

        if (named.Name == "Error")
            return new TsClassType("Error");

        return new TsClassType(named.Name);
    }

    // Type inference helpers
    private static TsType InferBinaryResultType(TsType left, TsType right, TokenKind op)
    {
        if (left is TsAnyType || right is TsAnyType)
        {
            // Comparisons stay bool; arithmetic on a dynamic operand stays
            // dynamic so declared annotations (e.g. int64) keep accepting it.
            return op switch
            {
                TokenKind.DoubleEquals or TokenKind.TripleEquals or TokenKind.StrictNotEquals or
                TokenKind.NotEquals or TokenKind.LessThan or TokenKind.GreaterThan or
                TokenKind.LessOrEqual or TokenKind.GreaterOrEqual or
                TokenKind.AmpersandAmpersand or TokenKind.PipePipe => TsType.Bool,
                _ => TsType.Any
            };
        }

        if (left == TsType.BigInt || right == TsType.BigInt)
        {
            if (op is TokenKind.DoubleEquals or TokenKind.TripleEquals or TokenKind.StrictNotEquals or
                TokenKind.NotEquals or TokenKind.LessThan or TokenKind.GreaterThan or
                TokenKind.LessOrEqual or TokenKind.GreaterOrEqual)
                return TsType.Bool;

            return left == TsType.BigInt && right == TsType.BigInt
                ? TsType.BigInt
                : TsType.Void;
        }

        return op switch
        {
            TokenKind.DoubleEquals or TokenKind.TripleEquals or TokenKind.StrictNotEquals or
            TokenKind.NotEquals or TokenKind.LessThan or TokenKind.GreaterThan or
            TokenKind.LessOrEqual or TokenKind.GreaterOrEqual or
            TokenKind.AmpersandAmpersand or TokenKind.PipePipe => TsType.Bool,

            TokenKind.QuestionQuestion => right,

            TokenKind.StarStar => TsType.Number,

            TokenKind.Plus when left == TsType.String && right == TsType.String => TsType.String,

            _ => left.IsNumeric && right.IsNumeric ? WiderNumeric(left, right) : TsType.Void
        };
    }

    // Mixed numeric operands take the wider side (int32 literal + number
    // variable computes as number, matching TypeScript arithmetic).
    public static TsType WiderNumeric(TsType left, TsType right)
    {
        if (left.Equals(right)) return left;
        int leftRank = NumericRank(left);
        int rightRank = NumericRank(right);
        return leftRank >= rightRank ? left : right;
    }

    private static int NumericRank(TsType type) => type.Name switch
    {
        "decimal" => 8,
        "float64" or "number" => 7,
        "float32" => 6,
        "bigint" => 5,
        "uint64" => 5,
        "int64" => 4,
        "uint32" => 3,
        "int32" => 2,
        "int16" or "uint16" => 1,
        _ => 0
    };

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
