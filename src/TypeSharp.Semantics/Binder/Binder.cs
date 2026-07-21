using TypeSharp.Semantics.Symbols;
using System.Numerics;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Diagnostics;
using TypeSharp.Syntax.SyntaxTree;

namespace TypeSharp.Semantics.Binder;

public sealed class Binder
{
    private readonly record struct CallParameter(string Name, TsType Type, bool IsOptional, bool IsRest = false);

    private readonly SymbolTable _symbolTable;
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<string, TsClassType> _classTypes = new();
    private readonly Dictionary<string, TsInterfaceType> _interfaceTypes = new();
    private readonly Dictionary<string, TsEnumType> _enumTypes = new();
    private readonly Dictionary<string, TsType> _typeAliases = new();
    private readonly Dictionary<string, TypeAliasDeclarationSyntax> _typeAliasDeclarations = new();
    private readonly HashSet<string> _resolvingTypeAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Symbol>> _importedSymbols = new();
    private TsClassType? _currentClassType;
    private ParameterSymbol? _currentThisSymbol;
    private TsType? _currentFunctionReturnType;
    private bool _currentFunctionIsAsync;
    private bool _currentFunctionIsGenerator;
    private bool _inConstructor;
    private int _lambdaCounter;
    private int _forOfCounter;
    private int _loopDepth;
    private int _breakableDepth;
    private readonly Stack<(string Name, bool IsLoop)> _labels = new();
    private string _currentSourceFileName = "module";

    // Generic type parameters in scope, innermost last.
    private readonly List<Dictionary<string, TsTypeParameter>> _typeParameterScopes = new();
    private readonly List<Dictionary<string, TsType>> _typeValueScopes = new();

    // Closure support: every LocalSymbol/ParameterSymbol records the function
    // nesting depth where it was declared; identifiers resolved from a deeper
    // function are captures and get boxed.
    private int _functionDepth;
    private readonly Dictionary<Symbol, int> _symbolFunctionDepth = new();
    private readonly List<(int FunctionDepth, List<Symbol> Captured)> _captureCollectors = new();

    private sealed record NullCheckNarrowing(
        Symbol? Symbol,
        string? AccessPath,
        TsType OriginalType,
        TsType TrueType,
        TsType FalseType);

    private readonly Stack<Dictionary<string, TsType>> _memberAccessNarrowingScopes = new();

    private void DefineWithDepth(Symbol symbol)
    {
        _symbolFunctionDepth[symbol] = _functionDepth;
        if (symbol is LocalSymbol local && _functionDepth == 0)
        {
            local.IsModuleScoped = true;
            local.ModuleName = _currentSourceFileName;
            local.RuntimeName = local.Name;
        }
        _symbolTable.Define(symbol);
    }

    // Symbols resolved across a function boundary are captures: every
    // function between the use and the declaration must thread the box.
    private void RegisterPossibleCapture(Symbol symbol)
    {
        if (symbol is LocalSymbol { IsModuleScoped: true })
            return;
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

        var dateGlobal = new TsClassType("Date");
        dateGlobal.Methods["getTime"] = new TsMethod("getTime", TsType.Number, new List<TsParameter>());
        _classTypes["Date"] = dateGlobal;
        _symbolTable.Define(new ClassSymbol("Date", dateGlobal, default));

        DefineGlobalFunction("parseInt", TsType.Number);
        DefineGlobalFunction("parseFloat", TsType.Number);
        DefineGlobalFunction("isNaN", TsType.Bool);
        DefineGlobalFunction("isFinite", TsType.Bool);
        DefineGlobalFunction("String", TsType.String);
        DefineGlobalFunction("Number", TsType.Number);
        DefineGlobalFunction("Boolean", TsType.Bool);
        DefineGlobalFunction("BigInt", TsType.BigInt);
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
                        shell.Properties[m.Name] = new TsProperty(m.Name, memberType)
                        {
                            IsReadonly = m.IsReadonly
                        };
                    }
                }
                else
                {
                    _typeAliasDeclarations[alias.Name] = alias;
                    if (alias.GenericParameters.Count == 0)
                        _typeAliases[alias.Name] = ResolveType(alias.Type);
                }
                PopTypeParameters();
                break;
            case ClassDeclarationSyntax cls:
                if (!_classTypes.TryGetValue(cls.Name, out var classType))
                    break;
                classType.IsAbstract = cls.IsAbstract;

                PushTypeParameters(cls.GenericParameters);

                if (cls.BaseType != null && ResolveType(cls.BaseType) is TsClassType baseClass)
                    classType.BaseType = baseClass;

                foreach (var classMember in cls.Members)
                {
                    switch (classMember)
                    {
                        case ConstructorDeclarationSyntax ctor:
                            classType.Constructor = new TsMethod("constructor", TsType.Void,
                                ctor.Parameters.Select(CreateTypeParameter).ToList());
                            foreach (var p in ctor.Parameters)
                            {
                                if (p.IsPropertyParameter)
                                    classType.Fields[p.Name] = new TsField(p.Name, CreateParameterSymbol(p).Type);
                            }
                            break;

                        case FieldDeclarationSyntax field:
                            classType.Fields[field.Name] = new TsField(field.Name, ResolveType(field.TypeAnnotation))
                            {
                                IsReadonly = field.IsReadonly,
                                IsStatic = field.IsStatic,
                                IsAbstract = field.IsAbstract,
                                IsPrivateName = field.IsPrivateName,
                                RuntimeName = GetRuntimeFieldName(cls.Name, field.Name, field.IsPrivateName)
                            };
                            break;

                        case MethodDeclarationSyntax method:
                            PushTypeParameters(method.GenericParameters);
                            var classMethod = new TsMethod(method.Name,
                                ResolveType(method.ReturnType) ?? TsType.Void,
                                method.Parameters.Select(CreateTypeParameter).ToList())
                            {
                                IsStatic = method.IsStatic,
                                IsAbstract = method.IsAbstract,
                                IsPrivateName = method.IsPrivateName,
                                RuntimeName = GetRuntimeMemberName(cls.Name, method.Name, method.IsPrivateName)
                            };
                            classMethod.TypeParameters.AddRange(method.GenericParameters.Select(p => new TsTypeParameter(p.Name)));
                            classType.Methods[method.Name] = classMethod;
                            PopTypeParameters();
                            break;

                        case AccessorDeclarationSyntax accessor:
                        {
                            var valueType = accessor.IsGetter
                                ? ResolveType(accessor.TypeAnnotation) ?? TsType.Any
                                : accessor.Parameter != null
                                    ? CreateParameterSymbol(accessor.Parameter).Type
                                    : TsType.Any;
                            if (!classType.Properties.TryGetValue(accessor.Name, out var property))
                            {
                                property = new TsProperty(accessor.Name, valueType)
                                {
                                    IsStatic = accessor.IsStatic,
                                    IsAbstract = accessor.IsAbstract,
                                    IsPrivateName = accessor.IsPrivateName,
                                    RuntimeName = GetRuntimeFieldName(cls.Name, accessor.Name, accessor.IsPrivateName),
                                    DeclaringClassName = cls.Name
                                };
                                classType.Properties[accessor.Name] = property;
                            }
                            if (accessor.IsGetter)
                            {
                                property.HasGetter = true;
                                property.GetterName = $"{cls.Name}::get:{accessor.Name}";
                            }
                            else
                            {
                                property.HasSetter = true;
                                property.SetterName = $"{cls.Name}::set:{accessor.Name}";
                                property.IsReadonly = false;
                            }
                            break;
                        }

                        case IndexSignatureSyntax indexSignature:
                            classType.IndexSignatures.Add(new TsIndexSignature(
                                ResolveType(indexSignature.KeyType),
                                ResolveType(indexSignature.ValueType))
                            {
                                IsReadonly = indexSignature.IsReadonly
                            });
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
                PushTypeParameters(iface.GenericParameters);
                foreach (var m in iface.Members)
                {
                    if (m is FieldDeclarationSyntax field)
                        ifaceType.Properties[field.Name] = new TsProperty(field.Name, ResolveType(field.TypeAnnotation))
                        {
                            IsReadonly = field.IsReadonly
                        };
                    else if (m is MethodDeclarationSyntax method)
                    {
                        PushTypeParameters(method.GenericParameters);
                        var interfaceMethod = new TsMethod(method.Name,
                            ResolveType(method.ReturnType) ?? TsType.Void,
                            method.Parameters.Select(CreateTypeParameter).ToList());
                        interfaceMethod.TypeParameters.AddRange(method.GenericParameters.Select(p => new TsTypeParameter(p.Name)));
                        ifaceType.Methods[method.Name] = interfaceMethod;
                        PopTypeParameters();
                    }
                }
                PopTypeParameters();
                break;
            }
        }
    }

    private BoundNode? BindNode(SyntaxNode node)
    {
        return node switch
        {
            FunctionDeclarationSyntax { IsOverloadSignature: true } => null,
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
        BindParameterDefaultValues(func.Parameters, sym.Parameters);

        var prevReturnType = _currentFunctionReturnType;
        var prevIsAsync = _currentFunctionIsAsync;
        var prevIsGenerator = _currentFunctionIsGenerator;
        _currentFunctionIsAsync = sym.IsAsync;
        _currentFunctionIsGenerator = sym.IsGenerator;
        _currentFunctionReturnType = BodyReturnType(sym.Type, sym.IsAsync);
        var body = BindNode(func.Body);
        _currentFunctionReturnType = prevReturnType;
        _currentFunctionIsAsync = prevIsAsync;
        _currentFunctionIsGenerator = prevIsGenerator;

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
        var returnType = lambda.IsGenerator
            ? TsType.Any
            : NormalizeFunctionReturnType(declaredReturnType, lambda.IsAsync);
        var sym = new FunctionSymbol(name, returnType, lambda.Range)
        {
            IsAsync = lambda.IsAsync,
            IsGenerator = lambda.IsGenerator,
            IsExported = exported
        };
        sym.TypeParameters.AddRange(lambda.GenericParameters.Select(p => new TsTypeParameter(p.Name)));
        for (int i = 0; i < lambda.Parameters.Count; i++)
        {
            var parameter = lambda.Parameters[i];
            var parameterType = !parameter.TypeWasInferred
                ? ResolveType(parameter.TypeAnnotation)
                : contextualType != null && i < contextualType.Parameters.Count
                    ? ResolveDefaultParameterType(contextualType.Parameters[i], parameter.DefaultValue)
                    : TsType.Any;
            sym.Parameters.Add(CreateParameterSymbol(parameter, parameterType));
        }
        ApplyTypePredicateSignature(sym, lambda.ReturnType);
        ApplyAssertionSignature(sym, lambda.ReturnType);
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
        BindParameterDefaultValues(lambda.Parameters, sym.Parameters);

        var prevReturnType = _currentFunctionReturnType;
        var prevIsAsync = _currentFunctionIsAsync;
        var prevIsGenerator = _currentFunctionIsGenerator;
        _currentFunctionIsAsync = sym.IsAsync;
        _currentFunctionIsGenerator = sym.IsGenerator;
        _currentFunctionReturnType = sym.Type is TsAnyType ? null : BodyReturnType(sym.Type, sym.IsAsync);
        var body = BindNode(lambda.Body);
        _currentFunctionReturnType = prevReturnType;
        _currentFunctionIsAsync = prevIsAsync;
        _currentFunctionIsGenerator = prevIsGenerator;

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
        var returnType = func.IsGenerator
            ? TsType.Any
            : NormalizeFunctionReturnType(declaredReturnType, func.IsAsync);

        if (func.IsOverloadSignature)
        {
            var overload = new TsFunctionType(
                func.Parameters.Select(CreateTypeParameter).ToList(),
                returnType);
            if (_symbolTable.Lookup(func.Name) is FunctionSymbol overloadedFunction)
            {
                overloadedFunction.Overloads.Add(overload);
                PopTypeParameters();
                return overloadedFunction;
            }

            var overloadOnly = new FunctionSymbol(func.Name, returnType, func.Range)
            {
                IsAsync = func.IsAsync,
                IsGenerator = func.IsGenerator,
                IsExported = func.Modifiers.Any(m => m.Token.Kind == TokenKind.ExportKeyword)
            };
            overloadOnly.Overloads.Add(overload);
            _symbolTable.Define(overloadOnly);
            PopTypeParameters();
            return overloadOnly;
        }

        var sym = new FunctionSymbol(func.Name, returnType, func.Range)
        {
            IsAsync = func.IsAsync,
            IsGenerator = func.IsGenerator,
            IsExported = func.Modifiers.Any(m => m.Token.Kind == TokenKind.ExportKeyword)
        };
        if (_symbolTable.Lookup(func.Name) is FunctionSymbol priorOverloads)
            sym.Overloads.AddRange(priorOverloads.Overloads);
        sym.TypeParameters.AddRange(func.GenericParameters.Select(p => new TsTypeParameter(p.Name)));
        foreach (var parameter in func.Parameters)
            sym.Parameters.Add(CreateParameterSymbol(parameter));
        ApplyTypePredicateSignature(sym, func.ReturnType);
        ApplyAssertionSignature(sym, func.ReturnType);
        PopTypeParameters();
        _symbolTable.Define(sym);
        return sym;
    }

    private void ApplyTypePredicateSignature(FunctionSymbol symbol, TypeSyntax? returnType)
    {
        if (returnType is not TypePredicateSyntax predicate)
            return;

        if (symbol.Parameters.All(parameter => parameter.Name != predicate.ParameterName))
        {
            _diagnostics.Error(
                $"Type predicate parameter '{predicate.ParameterName}' must be declared in function parameters",
                predicate.Range.Start);
            return;
        }

        symbol.Type = TsType.Bool;
        symbol.TypePredicateParameterName = predicate.ParameterName;
        symbol.TypePredicateTargetType = ResolveType(predicate.TargetType);
    }

    private void ApplyAssertionSignature(FunctionSymbol symbol, TypeSyntax? returnType)
    {
        if (returnType is not AssertionTypeSyntax assertion)
            return;

        if (symbol.Parameters.All(parameter => parameter.Name != assertion.ParameterName))
        {
            _diagnostics.Error(
                $"Assertion parameter '{assertion.ParameterName}' must be declared in function parameters",
                assertion.Range.Start);
            return;
        }

        symbol.Type = TsType.Void;
        symbol.AssertionParameterName = assertion.ParameterName;
        symbol.AssertionTargetType = assertion.TargetType != null
            ? ResolveType(assertion.TargetType)
            : null;
    }

    private ParameterSymbol CreateParameterSymbol(ParameterSyntax parameter, TsType? contextualType = null)
    {
        var parameterType = !parameter.TypeWasInferred
            ? ResolveType(parameter.TypeAnnotation)
            : contextualType ?? InferParameterTypeFromDefault(parameter.DefaultValue) ?? TsType.Any;

        return new ParameterSymbol(parameter.Name, parameterType, parameter.Range)
        {
            HasDefault = parameter.DefaultValue != null,
            IsTypeInferred = parameter.TypeWasInferred,
            IsRest = parameter.IsRest
        };
    }

    private TsParameter CreateTypeParameter(ParameterSyntax parameter)
    {
        var symbol = CreateParameterSymbol(parameter);
        return new TsParameter(symbol.Name, symbol.Type)
        {
            HasDefault = symbol.HasDefault,
            DefaultValue = symbol.DefaultValue,
            IsRest = symbol.IsRest
        };
    }

    private static TsParameter CreateTypeParameter(ParameterSymbol symbol) =>
        new(symbol.Name, symbol.Type)
        {
            HasDefault = symbol.HasDefault,
            DefaultValue = symbol.DefaultValue,
            IsRest = symbol.IsRest
        };

    private static CallParameter ToCallParameter(ParameterSymbol symbol) =>
        new(symbol.Name, symbol.Type, symbol.HasDefault || symbol.Type is TsNullableType, symbol.IsRest);

    private static CallParameter ToCallParameter(TsParameter parameter) =>
        new(parameter.Name, parameter.Type, parameter.HasDefault || parameter.Type is TsNullableType, parameter.IsRest);

    private static string GetRuntimeFieldName(string className, string memberName, bool isPrivateName) =>
        isPrivateName
            ? $"\uE000private:{className}:{memberName.TrimStart('#')}"
            : memberName;

    private static string GetRuntimeMemberName(string className, string memberName, bool isPrivateName) =>
        isPrivateName
            ? $"#{className}:{memberName.TrimStart('#')}"
            : memberName;

    private TsType? InferParameterTypeFromDefault(ExpressionSyntax? expression)
    {
        if (expression == null)
            return null;

        return expression switch
        {
            LiteralExpressionSyntax literal => InferLiteralType(literal),
            ArrayLiteralExpressionSyntax array => InferArrayLiteralType(array),
            ObjectLiteralExpressionSyntax => TsType.Any,
            _ => null
        };
    }

    private static TsType? InferLiteralType(LiteralExpressionSyntax literal) =>
        literal.Token.Kind switch
        {
            TokenKind.IntegerLiteral when literal.Token.Value is System.Numerics.BigInteger => TsType.BigInt,
            TokenKind.IntegerLiteral when literal.Token.Value is long value && value is >= int.MinValue and <= int.MaxValue => TsType.Int32,
            TokenKind.IntegerLiteral when literal.Token.Value is long => TsType.Int64,
            TokenKind.IntegerLiteral => TsType.Int32,
            TokenKind.FloatLiteral => literal.Token.Value is decimal ? TsType.Decimal : TsType.Float64,
            TokenKind.StringLiteral => TsType.String,
            TokenKind.TrueLiteral or TokenKind.FalseLiteral => TsType.Bool,
            TokenKind.NullLiteral => TsType.Null,
            _ => null
        };

    private TsType InferArrayLiteralType(ArrayLiteralExpressionSyntax array)
    {
        if (array.Elements.Count == 0)
            return new TsArrayType(TsType.Any);

        var elementTypes = array.Elements
            .Select(InferParameterTypeFromDefault)
            .Where(type => type != null)
            .Cast<TsType>()
            .ToList();

        if (elementTypes.Count != array.Elements.Count)
            return new TsArrayType(TsType.Any);

        var first = elementTypes[0];
        return elementTypes.All(type => TsType.IsCompatibleWith(type, first) && TsType.IsCompatibleWith(first, type))
            ? new TsArrayType(first)
            : new TsArrayType(TsType.Any);
    }

    private TsType ResolveDefaultParameterType(TsParameter contextualParameter, ExpressionSyntax? defaultValue)
    {
        if (defaultValue != null && contextualParameter.Type is TsNullableType nullable)
            return nullable.ElementType;

        return contextualParameter.Type;
    }

    private void BindParameterDefaultValues(IReadOnlyList<ParameterSyntax> syntaxParameters, IReadOnlyList<ParameterSymbol> symbols)
    {
        for (int i = 0; i < syntaxParameters.Count && i < symbols.Count; i++)
        {
            var syntaxParameter = syntaxParameters[i];
            var symbol = symbols[i];
            if (syntaxParameter.DefaultValue == null)
                continue;

            var defaultExpression = BindExpression(syntaxParameter.DefaultValue);
            symbol.HasDefault = true;
            symbol.DefaultExpression = defaultExpression;

            if (symbol.IsTypeInferred && symbol.Type is TsAnyType && defaultExpression.Type is not TsAnyType and not TsNullType and not TsPrimitiveType { Name: "void" })
                symbol.Type = defaultExpression.Type;
            else if (!symbol.IsTypeInferred && !TsType.IsCompatibleWith(defaultExpression.Type, symbol.Type))
                _diagnostics.Error(
                    $"Default value for parameter '{symbol.Name}' has type '{defaultExpression.Type}', which is not assignable to '{symbol.Type}'",
                    syntaxParameter.DefaultValue.Range.Start);
        }
    }

    private BoundClassDeclaration BindClass(ClassDeclarationSyntax cls)
        => BindClassCore(cls.Name, cls.GenericParameters, cls.BaseType, cls.Members, cls.Range, defineSymbol: true,
            isExported: cls.Modifiers.Any(m => m.Token.Kind == TokenKind.ExportKeyword), cls.IsAbstract,
            cls.Decorators);

    private BoundClassExpression BindClassExpression(ClassExpressionSyntax cls)
    {
        var name = cls.Name ?? $"$class_expr_{_lambdaCounter++}";
        var declaration = BindClassCore(name, cls.GenericParameters, cls.BaseType, cls.Members, cls.Range,
            defineSymbol: false, isExported: false, isAbstract: false, decorators: Array.Empty<DecoratorSyntax>());
        return new BoundClassExpression(declaration);
    }

    private BoundClassDeclaration BindClassCore(
        string name,
        IReadOnlyList<GenericParameterSyntax> genericParameters,
        TypeSyntax? baseTypeSyntax,
        IReadOnlyList<SyntaxNode> classMembers,
        SourceRange range,
        bool defineSymbol,
        bool isExported,
        bool isAbstract = false,
        IReadOnlyList<DecoratorSyntax>? decorators = null)
    {
        if (!_classTypes.TryGetValue(name, out var classType))
        {
            classType = new TsClassType(name);
            _classTypes[name] = classType;
        }

        if (baseTypeSyntax != null && classType.BaseType == null)
        {
            var baseType = ResolveType(baseTypeSyntax);
            if (baseType is TsClassType baseClass)
                classType.BaseType = baseClass;
        }

        var sym = new ClassSymbol(name, classType, range)
        {
            IsExported = isExported,
            IsAbstract = isAbstract
        };
        classType.IsAbstract = isAbstract;

        if (defineSymbol)
            _symbolTable.Define(sym);
        PushTypeParameters(genericParameters);
        _symbolTable.PushScope();

        var members = new List<BoundNode>();
        var prevClass = _currentClassType;
        _currentClassType = classType;
        int staticOrdinal = 0;
        foreach (var member in classMembers)
        {
            var bound = BindClassMember(member, classType, staticOrdinal);
            if (bound != null)
            {
                members.Add(bound);
                if (bound is BoundStaticBlock)
                    staticOrdinal++;
            }
        }
        _currentClassType = prevClass;

        _symbolTable.PopScope();
        PopTypeParameters();

        var declaration = new BoundClassDeclaration(sym, members);
        if (decorators != null)
            declaration.Decorators.AddRange(decorators.Select(decorator => BindExpression(decorator.Expression)));
        ValidateAbstractImplementation(classType, range.Start);
        return declaration;
    }

    private BoundNode? BindClassMember(SyntaxNode member, TsClassType classType, int staticOrdinal = 0)
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
                    var paramSym = CreateParameterSymbol(p);
                    var paramType = paramSym.Type;
                    ctorSym.Parameters.Add(paramSym);

                    // `constructor(private x: T)` also declares field x.
                    if (p.IsPropertyParameter)
                        classType.Fields[p.Name] = new TsField(p.Name, paramType);
                }

                var ctorMethod = new TsMethod("constructor", TsType.Void,
                    ctorSym.Parameters.Select(CreateTypeParameter).ToList());
                classType.Constructor = ctorMethod;

                _symbolTable.PushScope();
                _functionDepth++;
                var thisSymbol = new ParameterSymbol("this", classType, ctor.Range);
                DefineWithDepth(thisSymbol);
                foreach (var p in ctorSym.Parameters)
                    DefineWithDepth(p);
                BindParameterDefaultValues(ctor.Parameters, ctorSym.Parameters);
                var prevReturnType = _currentFunctionReturnType;
                var prevInConstructor = _inConstructor;
                var prevThisSymbol = _currentThisSymbol;
                _currentThisSymbol = thisSymbol;
                _currentFunctionReturnType = TsType.Void;
                _inConstructor = true;
                var body = BindNode(ctor.Body);
                _inConstructor = prevInConstructor;
                _currentFunctionReturnType = prevReturnType;
                _currentThisSymbol = prevThisSymbol;
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
                    IsReadonly = field.IsReadonly,
                    IsStatic = field.IsStatic,
                    IsAbstract = field.IsAbstract,
                    IsPrivateName = field.IsPrivateName,
                    RuntimeName = GetRuntimeFieldName(classType.Name, field.Name, field.IsPrivateName)
                };
                classType.Fields[field.Name] = new TsField(field.Name, fieldType)
                {
                    IsReadonly = field.IsReadonly,
                    IsStatic = field.IsStatic,
                    IsAbstract = field.IsAbstract,
                    IsPrivateName = field.IsPrivateName,
                    RuntimeName = fieldSym.RuntimeName
                };
                var boundField = new BoundFieldInitializer(classType.Name, fieldSym);
                boundField.Decorators.AddRange(field.Decorators.Select(decorator => BindExpression(decorator.Expression)));
                return boundField;
            }

            case MethodDeclarationSyntax method:
            {
                PushTypeParameters(method.GenericParameters);
                var declaredMethodType = ResolveType(method.ReturnType) ?? TsType.Void;
                var methodType = NormalizeFunctionReturnType(declaredMethodType, method.IsAsync);
                var methodSym = new MethodSymbol(method.Name, methodType, method.Range)
                {
                    IsAsync = method.IsAsync,
                    IsStatic = method.IsStatic,
                    IsAbstract = method.IsAbstract,
                    IsPrivateName = method.IsPrivateName,
                    RuntimeName = GetRuntimeMemberName(classType.Name, method.Name, method.IsPrivateName)
                };
                methodSym.TypeParameters.AddRange(method.GenericParameters.Select(p => new TsTypeParameter(p.Name)));

                foreach (var p in method.Parameters)
                {
                    var paramSym = CreateParameterSymbol(p);
                    methodSym.Parameters.Add(paramSym);
                }

                var tsMethod = new TsMethod(method.Name, methodType,
                    methodSym.Parameters.Select(CreateTypeParameter).ToList())
                {
                    IsAsync = method.IsAsync,
                    IsStatic = method.IsStatic,
                    IsAbstract = method.IsAbstract,
                    IsPrivateName = method.IsPrivateName,
                    RuntimeName = methodSym.RuntimeName
                };
                tsMethod.TypeParameters.AddRange(methodSym.TypeParameters.Select(p => new TsTypeParameter(p.Name)));
                ValidateOverride(classType, methodSym, method.Range.Start);
                classType.Methods[method.Name] = tsMethod;

                if (method.IsAbstract)
                {
                    if (method.Body != null)
                        _diagnostics.Error("Abstract methods cannot have an implementation", method.Range.Start);
                    PopTypeParameters();
                    return null;
                }

                if (method.Body != null)
                {
                    _symbolTable.PushScope();
                    _functionDepth++;
                    var thisSymbol = new ParameterSymbol("this", classType, method.Range);
                    DefineWithDepth(thisSymbol);
                    foreach (var p in methodSym.Parameters)
                        DefineWithDepth(p);
                    BindParameterDefaultValues(method.Parameters, methodSym.Parameters);
                    var prevReturnType = _currentFunctionReturnType;
                    var prevIsAsync = _currentFunctionIsAsync;
                    var prevInConstructor = _inConstructor;
                    var prevThisSymbol = _currentThisSymbol;
                    _currentThisSymbol = thisSymbol;
                    _currentFunctionIsAsync = method.IsAsync;
                    _currentFunctionReturnType = BodyReturnType(methodType, method.IsAsync);
                    _inConstructor = false;
                    var body = BindNode(method.Body);
                    _inConstructor = prevInConstructor;
                    _currentFunctionReturnType = prevReturnType;
                    _currentFunctionIsAsync = prevIsAsync;
                    _currentThisSymbol = prevThisSymbol;
                    _functionDepth--;
                    _symbolTable.PopScope();
                    PopTypeParameters();
                    var boundMethod = new BoundMethodDeclaration(classType.Name, methodSym, body!);
                    boundMethod.Decorators.AddRange(method.Decorators.Select(decorator => BindExpression(decorator.Expression)));
                    return boundMethod;
                }
                PopTypeParameters();
                return null;
            }

            case AccessorDeclarationSyntax accessor:
            {
                var accessorType = accessor.IsGetter
                    ? ResolveType(accessor.TypeAnnotation) ?? TsType.Any
                    : accessor.Parameter != null ? CreateParameterSymbol(accessor.Parameter).Type : TsType.Any;
                var propSym = new PropertySymbol(accessor.Name, accessorType, accessor.Range)
                {
                    IsStatic = accessor.IsStatic,
                    IsAbstract = accessor.IsAbstract,
                    IsPrivateName = accessor.IsPrivateName,
                    HasGetter = accessor.IsGetter,
                    HasSetter = !accessor.IsGetter,
                    GetterName = accessor.IsGetter ? $"{classType.Name}::get:{accessor.Name}" : null,
                    SetterName = accessor.IsGetter ? null : $"{classType.Name}::set:{accessor.Name}",
                    RuntimeName = GetRuntimeFieldName(classType.Name, accessor.Name, accessor.IsPrivateName),
                    DeclaringClassName = classType.Name
                };

                if (!classType.Properties.TryGetValue(accessor.Name, out var tsProperty))
                {
                    tsProperty = new TsProperty(accessor.Name, accessorType)
                    {
                        IsStatic = accessor.IsStatic,
                        IsAbstract = accessor.IsAbstract,
                        IsPrivateName = accessor.IsPrivateName,
                        RuntimeName = propSym.RuntimeName,
                        DeclaringClassName = classType.Name
                    };
                    classType.Properties[accessor.Name] = tsProperty;
                }
                if (accessor.IsGetter)
                {
                    tsProperty.HasGetter = true;
                    tsProperty.GetterName = propSym.GetterName;
                }
                else
                {
                    tsProperty.HasSetter = true;
                    tsProperty.SetterName = propSym.SetterName;
                }

                if (accessor.IsAbstract)
                {
                    if (accessor.Body != null)
                        _diagnostics.Error("Abstract accessors cannot have an implementation", accessor.Range.Start);
                    return null;
                }

                if (accessor.Body == null)
                    return null;

                _symbolTable.PushScope();
                _functionDepth++;
                var thisSymbol = new ParameterSymbol("this", classType, accessor.Range);
                DefineWithDepth(thisSymbol);
                ParameterSymbol? paramSym = null;
                if (!accessor.IsGetter && accessor.Parameter != null)
                {
                    paramSym = CreateParameterSymbol(accessor.Parameter);
                    DefineWithDepth(paramSym);
                }
                var prevReturnType = _currentFunctionReturnType;
                var prevThisSymbol = _currentThisSymbol;
                _currentThisSymbol = thisSymbol;
                _currentFunctionReturnType = accessor.IsGetter ? BodyReturnType(accessorType, isAsync: false) : TsType.Void;
                var body = BindNode(accessor.Body);
                _currentFunctionReturnType = prevReturnType;
                _currentThisSymbol = prevThisSymbol;
                _functionDepth--;
                _symbolTable.PopScope();
                var boundAccessor = new BoundAccessorDeclaration(classType.Name, propSym, accessor.IsGetter, paramSym, body!);
                boundAccessor.Decorators.AddRange(accessor.Decorators.Select(decorator => BindExpression(decorator.Expression)));
                return boundAccessor;
            }

            case StaticBlockSyntax staticBlock:
            {
                _symbolTable.PushScope();
                _functionDepth++;
                var prevReturnType = _currentFunctionReturnType;
                _currentFunctionReturnType = TsType.Void;
                var body = BindNode(staticBlock.Body);
                _currentFunctionReturnType = prevReturnType;
                _functionDepth--;
                _symbolTable.PopScope();
                return new BoundStaticBlock(classType.Name, staticOrdinal, body!);
            }

            case IndexSignatureSyntax indexSignature:
            {
                classType.IndexSignatures.Add(new TsIndexSignature(
                    ResolveType(indexSignature.KeyType),
                    ResolveType(indexSignature.ValueType))
                {
                    IsReadonly = indexSignature.IsReadonly
                });
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

    private void ValidateAbstractImplementation(TsClassType classType, SourceLocation location)
    {
        if (classType.IsAbstract || classType.BaseType == null)
            return;

        for (var current = classType.BaseType; current != null; current = current.BaseType)
        {
            foreach (var method in current.Methods.Values.Where(m => m.IsAbstract))
            {
                if (!classType.Methods.TryGetValue(method.Name, out var implementation) || implementation.IsAbstract)
                    _diagnostics.Error($"Class '{classType.Name}' must implement abstract method '{method.Name}'", location);
            }

            foreach (var property in current.Properties.Values.Where(p => p.IsAbstract))
            {
                if (!classType.Properties.TryGetValue(property.Name, out var implementation) || implementation.IsAbstract)
                    _diagnostics.Error($"Class '{classType.Name}' must implement abstract property '{property.Name}'", location);
            }
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
                PushTypeParameters(method.GenericParameters);
                var returnType = ResolveType(method.ReturnType) ?? TsType.Void;
                var tsMethod = new TsMethod(method.Name, returnType,
                    method.Parameters.Select(CreateTypeParameter).ToList());
                tsMethod.TypeParameters.AddRange(method.GenericParameters.Select(p => new TsTypeParameter(p.Name)));
                ifaceType.Methods[method.Name] = tsMethod;
                PopTypeParameters();
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
                ConstantInitializer = local.ConstantInitializer,
                IsModuleScoped = local.IsModuleScoped,
                ModuleName = local.ModuleName,
                RuntimeName = local.RuntimeName ?? local.Name
            },
            _ => new LocalSymbol(alias, symbol.Type, location)
        };
    }

    private static FunctionSymbol CopyFunction(FunctionSymbol source, string name, SourceRange location)
    {
        var copy = new FunctionSymbol(name, source.Type, location)
        {
            IsAsync = source.IsAsync,
            IsGenerator = source.IsGenerator,
            IsExported = source.IsExported,
            TargetName = source.TargetName ?? source.Name,
            TypePredicateParameterName = source.TypePredicateParameterName,
            TypePredicateTargetType = source.TypePredicateTargetType,
            AssertionParameterName = source.AssertionParameterName,
            AssertionTargetType = source.AssertionTargetType
        };
        copy.Overloads.AddRange(source.Overloads);
        foreach (var parameter in source.Parameters)
        {
            copy.Parameters.Add(new ParameterSymbol(parameter.Name, parameter.Type, parameter.Location)
            {
                HasDefault = parameter.HasDefault,
                DefaultValue = parameter.DefaultValue,
                DefaultExpression = parameter.DefaultExpression,
                IsTypeInferred = parameter.IsTypeInferred,
                IsCaptured = parameter.IsCaptured,
                IsRest = parameter.IsRest
            });
        }
        copy.TypeParameters.AddRange(source.TypeParameters.Select(p => new TsTypeParameter(p.Name)));
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
            YieldStatementSyntax yield => BindYield(yield),
            IfStatementSyntax ifStmt => BindIf(ifStmt),
            SwitchStatementSyntax switchStmt => BindSwitch(switchStmt),
            WhileStatementSyntax whileStmt => BindWhile(whileStmt),
            DoWhileStatementSyntax doWhileStmt => BindDoWhile(doWhileStmt),
            ForStatementSyntax forStmt => BindFor(forStmt),
            BreakStatementSyntax breakStmt => BindBreak(breakStmt),
            ContinueStatementSyntax continueStmt => BindContinue(continueStmt),
            ThrowStatementSyntax throwStmt => BindThrow(throwStmt),
            TryStatementSyntax tryStmt => BindTry(tryStmt),
            ForOfStatementSyntax forOf => BindForOf(forOf),
            ForInStatementSyntax forIn => BindForIn(forIn),
            LabelledStatementSyntax labelled => BindLabelled(labelled),
            FunctionDeclarationSyntax nestedFunc => BindFunction(nestedFunc),
            VariableDeclarationListSyntax varList => new BoundBlockStatement(
                varList.Declarations.Select(d => BindVariableDeclaration(d)).ToList()),
            DestructuringVariableDeclarationSyntax destructuring => BindDestructuringVariableDeclaration(destructuring),
            ExpressionStatementSyntax exprStmt => BindExpressionStatement(exprStmt),
            _ => throw new InvalidOperationException($"Unexpected statement type: {stmt.NodeType}")
        };
    }

    private BoundBlockStatement BindBlock(BlockStatementSyntax block)
    {
        _symbolTable.PushScope();
        var originalNarrowedTypes = new Dictionary<Symbol, TsType>();

        try
        {
            // Nested function declarations hoist within their block.
            foreach (var nested in block.Statements.OfType<FunctionDeclarationSyntax>())
                DeclareFunctionSignature(nested);

            var statements = new List<BoundNode>();
            foreach (var stmt in block.Statements)
            {
                var bound = BindStatement(stmt);
                statements.Add(bound);
                ApplyGuardNarrowingForFollowingStatements(stmt, bound, originalNarrowedTypes);
                ApplyAssertionNarrowingForFollowingStatements(stmt, originalNarrowedTypes);
            }
            return new BoundBlockStatement(statements);
        }
        finally
        {
            foreach (var (symbol, type) in originalNarrowedTypes)
                symbol.Type = type;
            _symbolTable.PopScope();
        }
    }

    private void ApplyGuardNarrowingForFollowingStatements(
        SyntaxNode statement,
        BoundNode bound,
        Dictionary<Symbol, TsType> originalTypes)
    {
        if (statement is not IfStatementSyntax ifStmt ||
            ifStmt.ElseBranch != null ||
            bound is not BoundIfStatement boundIf ||
            !DefinitelyExits(boundIf.ThenBranch))
        {
            return;
        }

        var narrowings = TryGetFalsePathNullCheckNarrowings(ifStmt.Condition);
        if (narrowings.Count == 0)
        {
            return;
        }

        foreach (var narrowing in narrowings)
        {
            if (narrowing.Symbol == null || narrowing.FalseType.Equals(narrowing.Symbol.Type))
            {
                continue;
            }

            if (!originalTypes.ContainsKey(narrowing.Symbol))
            {
                originalTypes[narrowing.Symbol] = narrowing.Symbol.Type;
            }

            narrowing.Symbol.Type = narrowing.FalseType;
        }
    }

    private void ApplyAssertionNarrowingForFollowingStatements(
        SyntaxNode statement,
        Dictionary<Symbol, TsType> originalTypes)
    {
        if (statement is not ExpressionStatementSyntax { Expression: CallExpressionSyntax call } ||
            TryGetAssertionNarrowing(call) is not { } narrowing ||
            narrowing.Symbol == null ||
            narrowing.TrueType.Equals(narrowing.Symbol.Type))
        {
            return;
        }

        if (!originalTypes.ContainsKey(narrowing.Symbol))
        {
            originalTypes[narrowing.Symbol] = narrowing.Symbol.Type;
        }

        narrowing.Symbol.Type = narrowing.TrueType;
    }

    private NullCheckNarrowing? TryGetAssertionNarrowing(CallExpressionSyntax call)
    {
        if (call.Callee is not IdentifierExpressionSyntax callee ||
            _symbolTable.Lookup(callee.Name) is not FunctionSymbol function ||
            function.AssertionParameterName == null ||
            function.AssertionTargetType == null)
        {
            return null;
        }

        var parameterIndex = function.Parameters.FindIndex(parameter => parameter.Name == function.AssertionParameterName);
        if (parameterIndex < 0 || parameterIndex >= call.Arguments.Count ||
            call.Arguments[parameterIndex] is not IdentifierExpressionSyntax identifier)
        {
            return null;
        }

        var symbol = _symbolTable.Lookup(identifier.Name);
        if (symbol is not (LocalSymbol or ParameterSymbol))
            return null;

        return new NullCheckNarrowing(
            symbol,
            null,
            symbol.Type,
            function.AssertionTargetType,
            symbol.Type);
    }

    private static bool DefinitelyExits(BoundNode statement) =>
        statement switch
        {
            BoundReturnStatement or BoundThrowStatement or BoundBreakStatement or BoundContinueStatement => true,
            BoundBlockStatement block => block.Statements.Count > 0 && DefinitelyExits(block.Statements[^1]),
            BoundIfStatement ifStmt => ifStmt.ElseBranch != null &&
                                       DefinitelyExits(ifStmt.ThenBranch) &&
                                       DefinitelyExits(ifStmt.ElseBranch),
            _ => false
        };

    private BoundNode BindVariableDeclaration(VariableDeclarationSyntax varDecl)
    {
        // `const f = (a) => ...` declares a function, not a data slot.
        if (varDecl.Initializer is LambdaExpressionSyntax lambdaInit)
            return BindLambdaAsFunction(varDecl.Name, lambdaInit, varDecl.IsExported);

        TsType type;
        BoundNode? initializer = null;

        if (varDecl.Initializer != null)
        {
            if (varDecl.TypeAnnotation != null)
            {
                var expectedType = ResolveType(varDecl.TypeAnnotation);
                initializer = BindExpressionWithExpectedType(varDecl.Initializer, expectedType);
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
        var expectedReturnType = _currentFunctionReturnType is { } functionReturnType && functionReturnType != TsType.Void
            ? functionReturnType
            : null;
        BoundNode? value = ret.Value != null
            ? expectedReturnType != null
                ? BindExpressionWithExpectedType(ret.Value, expectedReturnType)
                : BindExpression(ret.Value)
            : null;

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

    private BoundYieldStatement BindYield(YieldStatementSyntax yield)
    {
        if (!_currentFunctionIsGenerator)
            _diagnostics.Error("'yield' can only be used inside a generator function", yield.Range.Start);

        var value = yield.Value != null ? BindExpression(yield.Value) : null;
        return new BoundYieldStatement(value);
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
        var trueNarrowings = TryGetTruePathNullCheckNarrowings(ifStmt.Condition);
        var thenBranch = trueNarrowings.Count > 0
            ? BindStatementWithTemporaryNarrowings(ifStmt.ThenBranch, trueNarrowings, useTrueType: true)
            : BindStatement(ifStmt.ThenBranch);
        var falseNarrowings = ifStmt.ElseBranch != null
            ? TryGetFalsePathNullCheckNarrowings(ifStmt.Condition)
            : new List<NullCheckNarrowing>();
        var elseBranch = ifStmt.ElseBranch != null
            ? falseNarrowings.Count > 0
                ? BindStatementWithTemporaryNarrowings(ifStmt.ElseBranch, falseNarrowings, useTrueType: false)
                : BindStatement(ifStmt.ElseBranch)
            : null;
        return new BoundIfStatement(condition, thenBranch, elseBranch);
    }

    private BoundNode BindDestructuringVariableDeclaration(DestructuringVariableDeclarationSyntax declaration)
    {
        if (declaration.Initializer == null)
            return new BoundBlockStatement(new List<BoundNode>());

        var source = BindExpression(declaration.Initializer);
        var temp = new LocalSymbol($"$destructure_{_forOfCounter++}", source.Type, declaration.Range, isConst: true);
        DefineWithDepth(temp);
        var statements = new List<BoundNode> { new BoundVariableDeclaration(temp, source) };

        if (declaration.Pattern != null)
        {
            BindDestructuringDeclarationPattern(
                declaration.Pattern,
                new BoundVariableExpression(temp),
                declaration.IsConst,
                statements);
            return new BoundBlockStatement(statements);
        }

        for (int i = 0; i < declaration.Elements.Count; i++)
        {
            var element = declaration.Elements[i];
            if (string.IsNullOrEmpty(element.Name))
                continue;
            if (element.IsRest)
            {
                if (!declaration.IsArray)
                {
                    var excluded = declaration.Elements.Take(i).Where(e => !string.IsNullOrEmpty(e.Name)).Select(e => e.SourceName).ToList();
                    var objectRestSymbol = new LocalSymbol(element.Name, TsType.Any, element.Range, declaration.IsConst);
                    DefineWithDepth(objectRestSymbol);
                    statements.Add(new BoundVariableDeclaration(objectRestSymbol,
                        new BoundObjectRestExpression(new BoundVariableExpression(temp), excluded)));
                    continue;
                }
                var restSymbol = new LocalSymbol(element.Name, new TsArrayType(TsType.Any), element.Range, declaration.IsConst);
                DefineWithDepth(restSymbol);
                statements.Add(new BoundVariableDeclaration(restSymbol,
                    new BoundArraySliceExpression(new BoundVariableExpression(temp), i)));
                continue;
            }

            BoundNode value = declaration.IsArray
                ? new BoundIndexExpression(new BoundVariableExpression(temp), new BoundLiteralExpression(i, TsType.Int32), TsType.Any)
                : new BoundMemberAccessExpression(new BoundVariableExpression(temp), new PropertySymbol(element.SourceName, TsType.Any, element.Range));
            if (element.DefaultValue != null)
            {
                var fallback = BindExpression(element.DefaultValue);
                var isUndefined = new BoundBinaryExpression(value, TokenKind.TripleEquals,
                    new BoundLiteralExpression(null, TsType.Void), TsType.Bool);
                var isNull = new BoundBinaryExpression(value, TokenKind.TripleEquals,
                    new BoundLiteralExpression(null, TsType.Any), TsType.Bool);
                var isMissing = new BoundBinaryExpression(isUndefined, TokenKind.PipePipe, isNull, TsType.Bool);
                value = new BoundConditionalExpression(isMissing, fallback, value, TsType.Any);
            }

            var symbol = new LocalSymbol(element.Name, TsType.Any, element.Range, declaration.IsConst);
            DefineWithDepth(symbol);
            statements.Add(new BoundVariableDeclaration(symbol, value));
        }
        return new BoundBlockStatement(statements);
    }

    private void BindDestructuringDeclarationPattern(
        ExpressionSyntax pattern,
        BoundNode source,
        bool isConst,
        List<BoundNode> statements)
    {
        switch (pattern)
        {
            case ArrayLiteralExpressionSyntax array:
                for (int i = 0; i < array.Elements.Count; i++)
                {
                    var element = array.Elements[i];
                    if (element is SpreadExpressionSyntax spread)
                    {
                        DeclareDestructuringBinding(spread.Expression, new BoundArraySliceExpression(source, i), isConst, statements);
                        continue;
                    }

                    var value = new BoundIndexExpression(source, new BoundLiteralExpression(i, TsType.Int32), TsType.Any);
                    BindDestructuringDeclarationElement(element, value, isConst, statements);
                }
                break;

            case ObjectLiteralExpressionSyntax obj:
                var excluded = new List<string>();
                foreach (var property in obj.Properties)
                {
                    if (property is ObjectSpreadPropertySyntax spread)
                    {
                        DeclareDestructuringBinding(spread.Value, new BoundObjectRestExpression(source, excluded.ToList()), isConst, statements);
                        continue;
                    }

                    if (property is ComputedObjectPropertySyntax computed)
                    {
                        var key = BindExpression(computed.KeyExpression);
                        var value = new BoundIndexExpression(source, key, TsType.Any);
                        BindDestructuringDeclarationElement(computed.Value, value, isConst, statements);
                    }
                    else
                    {
                        excluded.Add(property.Key);
                        var value = new BoundMemberAccessExpression(source, new PropertySymbol(property.Key, TsType.Any, property.Range));
                        BindDestructuringDeclarationElement(property.Value, value, isConst, statements);
                    }
                }
                break;

            default:
                DeclareDestructuringBinding(pattern, source, isConst, statements);
                break;
        }
    }

    private void BindDestructuringDeclarationElement(
        ExpressionSyntax targetPattern,
        BoundNode value,
        bool isConst,
        List<BoundNode> statements)
    {
        ExpressionSyntax target = targetPattern;
        ExpressionSyntax? defaultValue = null;
        if (targetPattern is AssignmentExpressionSyntax { OperatorToken.Kind: TokenKind.Equals } defaulted)
        {
            target = defaulted.Target;
            defaultValue = defaulted.Value;
        }

        value = ApplyDestructuringDefault(value, defaultValue);
        if (target is ArrayLiteralExpressionSyntax or ObjectLiteralExpressionSyntax)
        {
            var nestedTemp = new LocalSymbol($"$destructure_nested_{_forOfCounter++}", TsType.Any, target.Range, isConst: true);
            DefineWithDepth(nestedTemp);
            statements.Add(new BoundVariableDeclaration(nestedTemp, value));
            BindDestructuringDeclarationPattern(target, new BoundVariableExpression(nestedTemp), isConst, statements);
            return;
        }

        DeclareDestructuringBinding(target, value, isConst, statements);
    }

    private void DeclareDestructuringBinding(ExpressionSyntax target, BoundNode value, bool isConst, List<BoundNode> statements)
    {
        if (target is not IdentifierExpressionSyntax identifier)
        {
            _diagnostics.Error("Invalid destructuring binding target", target.Range.Start);
            return;
        }

        var symbol = new LocalSymbol(identifier.Name, TsType.Any, target.Range, isConst);
        DefineWithDepth(symbol);
        statements.Add(new BoundVariableDeclaration(symbol, value));
    }

    private BoundSwitchStatement BindSwitch(SwitchStatementSyntax switchStmt)
    {
        // A switch introduces one lexical scope shared by every clause.  This
        // matters for `let`/`const` declarations and for closures created in a
        // clause; creating a scope per case would not match JavaScript.
        var expression = BindExpression(switchStmt.Expression);
        _symbolTable.PushScope();
        _breakableDepth++;
        try
        {
            // Function declarations are hoisted across the switch body just as
            // they are across a normal block.
            foreach (var declaration in switchStmt.Clauses
                         .SelectMany(clause => clause.Statements)
                         .OfType<FunctionDeclarationSyntax>())
            {
                DeclareFunctionSignature(declaration);
            }

            var clauses = new List<BoundSwitchClause>();
            foreach (var clause in switchStmt.Clauses)
            {
                var test = clause.Test != null ? BindExpression(clause.Test) : null;
                var statements = clause.Statements.Select(BindStatement).ToList();
                clauses.Add(new BoundSwitchClause(test, statements));
            }

            return new BoundSwitchStatement(expression, clauses);
        }
        finally
        {
            _breakableDepth--;
            _symbolTable.PopScope();
        }
    }

    private BoundNode BindStatementWithTemporaryType(SyntaxNode statement, Symbol symbol, TsType type)
    {
        var previous = symbol.Type;
        symbol.Type = type;
        try
        {
            return BindStatement(statement);
        }
        finally
        {
            symbol.Type = previous;
        }
    }

    private BoundNode BindStatementWithTemporaryTypes(
        SyntaxNode statement,
        IReadOnlyList<(Symbol Symbol, TsType Type)> types)
    {
        var previous = new List<(Symbol Symbol, TsType Type)>();
        foreach (var item in types)
        {
            previous.Add((item.Symbol, item.Symbol.Type));
            item.Symbol.Type = item.Type;
        }

        try
        {
            return BindStatement(statement);
        }
        finally
        {
            for (int i = previous.Count - 1; i >= 0; i--)
            {
                previous[i].Symbol.Type = previous[i].Type;
            }
        }
    }

    private BoundNode BindStatementWithTemporaryNarrowings(
        SyntaxNode statement,
        IReadOnlyList<NullCheckNarrowing> narrowings,
        bool useTrueType)
    {
        return BindWithTemporaryNarrowings(narrowings, useTrueType, () => BindStatement(statement));
    }

    private T BindWithTemporaryNarrowings<T>(
        IReadOnlyList<NullCheckNarrowing> narrowings,
        bool useTrueType,
        Func<T> bind)
    {
        var previousSymbols = new List<(Symbol Symbol, TsType Type)>();
        Dictionary<string, TsType>? memberTypes = null;

        foreach (var narrowing in narrowings)
        {
            var narrowedType = useTrueType ? narrowing.TrueType : narrowing.FalseType;
            if (narrowing.Symbol != null)
            {
                previousSymbols.Add((narrowing.Symbol, narrowing.Symbol.Type));
                narrowing.Symbol.Type = narrowedType;
                continue;
            }

            if (narrowing.AccessPath != null)
            {
                memberTypes ??= new Dictionary<string, TsType>();
                memberTypes[narrowing.AccessPath] = narrowedType;
            }
        }

        if (memberTypes != null)
        {
            _memberAccessNarrowingScopes.Push(memberTypes);
        }

        try
        {
            return bind();
        }
        finally
        {
            if (memberTypes != null)
            {
                _memberAccessNarrowingScopes.Pop();
            }

            for (int i = previousSymbols.Count - 1; i >= 0; i--)
            {
                previousSymbols[i].Symbol.Type = previousSymbols[i].Type;
            }
        }
    }

    private NullCheckNarrowing? TryGetNullCheckNarrowing(ExpressionSyntax condition)
    {
        if (condition is not BinaryExpressionSyntax binary)
        {
            return null;
        }

        if (binary.OperatorToken.Kind == TokenKind.AmpersandAmpersand)
        {
            var left = TryGetNullCheckNarrowing(binary.Left);
            if (left != null && !left.TrueType.Equals(left.OriginalType))
            {
                return new NullCheckNarrowing(left.Symbol, left.AccessPath, left.OriginalType, left.TrueType, left.OriginalType);
            }

            var right = TryGetNullCheckNarrowing(binary.Right);
            if (right != null && !right.TrueType.Equals(right.OriginalType))
            {
                return new NullCheckNarrowing(right.Symbol, right.AccessPath, right.OriginalType, right.TrueType, right.OriginalType);
            }

            return null;
        }

        if (binary.OperatorToken.Kind == TokenKind.PipePipe)
        {
            var left = TryGetNullCheckNarrowing(binary.Left);
            if (left != null && !left.FalseType.Equals(left.OriginalType))
            {
                return new NullCheckNarrowing(left.Symbol, left.AccessPath, left.OriginalType, left.OriginalType, left.FalseType);
            }

            var right = TryGetNullCheckNarrowing(binary.Right);
            if (right != null && !right.FalseType.Equals(right.OriginalType))
            {
                return new NullCheckNarrowing(right.Symbol, right.AccessPath, right.OriginalType, right.OriginalType, right.FalseType);
            }

            return null;
        }

        if (!TryGetIdentifierNullComparison(binary, out var identifierName, out var equalityIsTrueWhenNull))
        {
            return TryGetMemberAccessNullCheckNarrowing(binary);
        }

        var symbol = _symbolTable.Lookup(identifierName);
        if (symbol is not (LocalSymbol or ParameterSymbol))
        {
            return null;
        }

        if (!TryRemoveNullish(symbol.Type, out var nonNullType))
        {
            return null;
        }

        return equalityIsTrueWhenNull
            ? new NullCheckNarrowing(symbol, null, symbol.Type, TsType.Null, nonNullType)
            : new NullCheckNarrowing(symbol, null, symbol.Type, nonNullType, TsType.Null);
    }

    private NullCheckNarrowing? TryGetMemberAccessNullCheckNarrowing(BinaryExpressionSyntax binary)
    {
        if (!TryGetMemberAccessNullComparison(binary, out var accessExpression, out var equalityIsTrueWhenNull))
        {
            return null;
        }

        var accessPath = GetMemberAccessPath(accessExpression);
        if (accessPath == null)
        {
            return null;
        }

        var boundAccess = BindExpression(accessExpression);
        if (!TryRemoveNullish(boundAccess.Type, out var nonNullType))
        {
            return null;
        }

        return equalityIsTrueWhenNull
            ? new NullCheckNarrowing(null, accessPath, boundAccess.Type, TsType.Null, nonNullType)
            : new NullCheckNarrowing(null, accessPath, boundAccess.Type, nonNullType, TsType.Null);
    }

    private List<NullCheckNarrowing> TryGetFalsePathNullCheckNarrowings(ExpressionSyntax condition)
    {
        if (condition is not BinaryExpressionSyntax binary)
        {
            var single = TryGetConditionNarrowing(condition);
            return single == null ? new List<NullCheckNarrowing>() : new List<NullCheckNarrowing> { single };
        }

        if (binary.OperatorToken.Kind == TokenKind.PipePipe)
        {
            var result = TryGetFalsePathNullCheckNarrowings(binary.Left);
            result.AddRange(TryGetFalsePathNullCheckNarrowings(binary.Right));
            return result;
        }

        var narrowing = TryGetConditionNarrowing(condition);
        return narrowing == null ? new List<NullCheckNarrowing>() : new List<NullCheckNarrowing> { narrowing };
    }

    private List<NullCheckNarrowing> TryGetTruePathNullCheckNarrowings(ExpressionSyntax condition)
    {
        if (condition is not BinaryExpressionSyntax binary)
        {
            var single = TryGetConditionNarrowing(condition);
            return single == null ? new List<NullCheckNarrowing>() : new List<NullCheckNarrowing> { single };
        }

        if (binary.OperatorToken.Kind == TokenKind.AmpersandAmpersand)
        {
            var result = TryGetTruePathNullCheckNarrowings(binary.Left);
            result.AddRange(TryGetTruePathNullCheckNarrowings(binary.Right));
            return result;
        }

        var narrowing = TryGetConditionNarrowing(condition);
        return narrowing == null ? new List<NullCheckNarrowing>() : new List<NullCheckNarrowing> { narrowing };
    }

    private NullCheckNarrowing? TryGetConditionNarrowing(ExpressionSyntax condition)
    {
        return TryGetTypePredicateNarrowing(condition) ??
               TryGetDiscriminatedUnionNarrowing(condition) ??
               TryGetNullCheckNarrowing(condition);
    }

    private NullCheckNarrowing? TryGetDiscriminatedUnionNarrowing(ExpressionSyntax condition)
    {
        if (condition is not BinaryExpressionSyntax binary)
            return null;

        var comparesEqual = binary.OperatorToken.Kind is TokenKind.DoubleEquals or TokenKind.TripleEquals;
        var comparesNotEqual = binary.OperatorToken.Kind is TokenKind.NotEquals or TokenKind.StrictNotEquals;
        if (!comparesEqual && !comparesNotEqual)
            return null;

        MemberAccessExpressionSyntax? memberAccess = null;
        TsLiteralType? literalType = null;
        if (binary.Left is MemberAccessExpressionSyntax leftAccess &&
            TryGetExpressionLiteralType(binary.Right, out var rightLiteral))
        {
            memberAccess = leftAccess;
            literalType = rightLiteral;
        }
        else if (binary.Right is MemberAccessExpressionSyntax rightAccess &&
                 TryGetExpressionLiteralType(binary.Left, out var leftLiteral))
        {
            memberAccess = rightAccess;
            literalType = leftLiteral;
        }

        if (memberAccess?.Object is not IdentifierExpressionSyntax receiver ||
            literalType == null ||
            _symbolTable.Lookup(receiver.Name) is not { } symbol ||
            symbol is not (LocalSymbol or ParameterSymbol) ||
            symbol.Type is not TsUnionType union)
        {
            return null;
        }

        var matching = union.Types
            .Where(candidate => TryGetReadableMemberType(candidate, memberAccess.MemberName, out var memberType) &&
                                TsType.IsCompatibleWith(literalType, memberType) &&
                                TsType.IsCompatibleWith(memberType, literalType))
            .ToList();

        if (matching.Count == 0 || matching.Count == union.Types.Count)
            return null;

        var nonMatching = union.Types
            .Where(candidate => !matching.Contains(candidate))
            .ToList();

        var narrowed = matching.Count == 1
            ? matching[0]
            : new TsUnionType(DistinctTypes(matching));
        var excluded = nonMatching.Count switch
        {
            0 => TsType.Never,
            1 => nonMatching[0],
            _ => new TsUnionType(DistinctTypes(nonMatching))
        };

        return comparesEqual
            ? new NullCheckNarrowing(symbol, null, symbol.Type, narrowed, excluded)
            : new NullCheckNarrowing(symbol, null, symbol.Type, excluded, narrowed);
    }

    private static bool TryGetExpressionLiteralType(ExpressionSyntax expression, out TsLiteralType literalType)
    {
        if (expression is LiteralExpressionSyntax literal)
        {
            var resolved = ResolveLiteralType(new LiteralTypeSyntax(literal.Token, literal.Range));
            if (resolved is TsLiteralType typedLiteral)
            {
                literalType = typedLiteral;
                return true;
            }
        }

        literalType = null!;
        return false;
    }

    private NullCheckNarrowing? TryGetTypePredicateNarrowing(ExpressionSyntax condition)
    {
        if (condition is not CallExpressionSyntax call ||
            call.Callee is not IdentifierExpressionSyntax callee ||
            _symbolTable.Lookup(callee.Name) is not FunctionSymbol function ||
            function.TypePredicateParameterName == null ||
            function.TypePredicateTargetType == null)
        {
            return null;
        }

        var parameterIndex = function.Parameters.FindIndex(parameter => parameter.Name == function.TypePredicateParameterName);
        if (parameterIndex < 0 || parameterIndex >= call.Arguments.Count)
        {
            return null;
        }

        var argument = call.Arguments[parameterIndex];
        if (argument is IdentifierExpressionSyntax identifier)
        {
            var symbol = _symbolTable.Lookup(identifier.Name);
            if (symbol is not (LocalSymbol or ParameterSymbol))
                return null;

            return new NullCheckNarrowing(
                symbol,
                null,
                symbol.Type,
                function.TypePredicateTargetType,
                symbol.Type);
        }

        if (argument is MemberAccessExpressionSyntax member &&
            GetMemberAccessPath(member) is { } accessPath)
        {
            var bound = BindExpression(member);
            return new NullCheckNarrowing(
                null,
                accessPath,
                bound.Type,
                function.TypePredicateTargetType,
                bound.Type);
        }

        return null;
    }

    private static bool TryGetIdentifierNullComparison(
        BinaryExpressionSyntax binary,
        out string identifierName,
        out bool equalityIsTrueWhenNull)
    {
        identifierName = string.Empty;
        equalityIsTrueWhenNull = false;

        var comparesEqual = binary.OperatorToken.Kind is TokenKind.DoubleEquals or TokenKind.TripleEquals;
        var comparesNotEqual = binary.OperatorToken.Kind is TokenKind.NotEquals or TokenKind.StrictNotEquals;
        if (!comparesEqual && !comparesNotEqual)
        {
            return false;
        }

        IdentifierExpressionSyntax? identifier = null;
        if (binary.Left is IdentifierExpressionSyntax left && IsNullishLiteral(binary.Right))
        {
            identifier = left;
        }
        else if (binary.Right is IdentifierExpressionSyntax right && IsNullishLiteral(binary.Left))
        {
            identifier = right;
        }

        if (identifier == null)
        {
            return false;
        }

        identifierName = identifier.Name;
        equalityIsTrueWhenNull = comparesEqual;
        return true;
    }

    private static bool TryGetMemberAccessNullComparison(
        BinaryExpressionSyntax binary,
        out MemberAccessExpressionSyntax accessExpression,
        out bool equalityIsTrueWhenNull)
    {
        accessExpression = null!;
        equalityIsTrueWhenNull = false;

        var comparesEqual = binary.OperatorToken.Kind is TokenKind.DoubleEquals or TokenKind.TripleEquals;
        var comparesNotEqual = binary.OperatorToken.Kind is TokenKind.NotEquals or TokenKind.StrictNotEquals;
        if (!comparesEqual && !comparesNotEqual)
        {
            return false;
        }

        if (binary.Left is MemberAccessExpressionSyntax left && IsNullishLiteral(binary.Right))
        {
            accessExpression = left;
        }
        else if (binary.Right is MemberAccessExpressionSyntax right && IsNullishLiteral(binary.Left))
        {
            accessExpression = right;
        }

        if (accessExpression == null)
        {
            return false;
        }

        equalityIsTrueWhenNull = comparesEqual;
        return true;
    }

    private static string? GetMemberAccessPath(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierExpressionSyntax identifier => identifier.Name,
            MemberAccessExpressionSyntax member when GetMemberAccessPath(member.Object) is { } owner =>
                owner + "." + member.MemberName,
            _ => null
        };
    }

    private static bool IsNullishLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax literal && literal.Token.Kind == TokenKind.NullLiteral ||
        expression is IdentifierExpressionSyntax identifier && identifier.Name == "undefined";

    private static bool TryRemoveNullish(TsType type, out TsType nonNullType)
    {
        switch (type)
        {
            case TsNullableType nullable:
                nonNullType = nullable.ElementType;
                return true;
            case TsUnionType union:
                var narrowed = new List<TsType>();
                var changed = false;
                foreach (var candidate in union.Types)
                {
                    if (candidate is TsNullType || candidate.Equals(TsType.Void))
                    {
                        changed = true;
                        continue;
                    }

                    if (candidate is TsNullableType nullableCandidate)
                    {
                        narrowed.Add(nullableCandidate.ElementType);
                        changed = true;
                        continue;
                    }

                    narrowed.Add(candidate);
                }

                if (!changed)
                {
                    nonNullType = type;
                    return false;
                }

                nonNullType = narrowed.Count switch
                {
                    0 => TsType.Void,
                    1 => narrowed[0],
                    _ => new TsUnionType(narrowed)
                };
                return true;
            default:
                nonNullType = type;
                return false;
        }
    }

    private BoundWhileStatement BindWhile(WhileStatementSyntax whileStmt)
    {
        var condition = BindExpression(whileStmt.Condition);
        ValidateCondition(condition, whileStmt.Condition.Range.Start);
        _loopDepth++;
        _breakableDepth++;
        BoundNode body;
        try
        {
            body = BindStatement(whileStmt.Body);
        }
        finally
        {
            _loopDepth--;
            _breakableDepth--;
        }
        return new BoundWhileStatement(condition, body);
    }

    private BoundDoWhileStatement BindDoWhile(DoWhileStatementSyntax doWhileStmt)
    {
        _loopDepth++;
        _breakableDepth++;
        BoundNode body;
        BoundNode condition;
        try
        {
            body = BindStatement(doWhileStmt.Body);
            condition = BindExpression(doWhileStmt.Condition);
            ValidateCondition(condition, doWhileStmt.Condition.Range.Start);
        }
        finally
        {
            _loopDepth--;
            _breakableDepth--;
        }
        return new BoundDoWhileStatement(body, condition);
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
        _loopDepth++;
        _breakableDepth++;
        BoundNode body;
        try
        {
            body = BindStatement(forStmt.Body);
        }
        finally
        {
            _loopDepth--;
            _breakableDepth--;
        }

        _symbolTable.PopScope();

        return new BoundForStatement(initializer, condition, iterator, body);
    }

    private BoundBreakStatement BindBreak(BreakStatementSyntax breakStmt)
    {
        if (breakStmt.Label != null)
        {
            if (!_labels.Any(label => label.Name == breakStmt.Label))
                _diagnostics.Error($"Undefined label '{breakStmt.Label}'", breakStmt.Range.Start, DiagnosticCode.TS2016);
            return new BoundBreakStatement(breakStmt.Label);
        }
        if (_breakableDepth == 0)
        {
            _diagnostics.Error("'break' can only be used inside a loop or switch", breakStmt.Range.Start, DiagnosticCode.TS2016);
        }

        return new BoundBreakStatement();
    }

    private BoundContinueStatement BindContinue(ContinueStatementSyntax continueStmt)
    {
        if (continueStmt.Label != null)
        {
            var label = _labels.FirstOrDefault(candidate => candidate.Name == continueStmt.Label);
            if (label.Name == null)
                _diagnostics.Error($"Undefined label '{continueStmt.Label}'", continueStmt.Range.Start, DiagnosticCode.TS2016);
            else if (!label.IsLoop)
                _diagnostics.Error($"Label '{continueStmt.Label}' does not name an iteration statement", continueStmt.Range.Start, DiagnosticCode.TS2016);
            return new BoundContinueStatement(continueStmt.Label);
        }
        if (_loopDepth == 0)
        {
            _diagnostics.Error("'continue' can only be used inside a loop", continueStmt.Range.Start, DiagnosticCode.TS2016);
        }

        return new BoundContinueStatement();
    }

    private BoundNode BindLabelled(LabelledStatementSyntax labelled)
    {
        if (_labels.Any(label => label.Name == labelled.Label))
            _diagnostics.Error($"Duplicate label '{labelled.Label}'", labelled.Range.Start, DiagnosticCode.TS2016);

        bool isLoop = LabelsIterationStatement(labelled.Statement);
        _labels.Push((labelled.Label, isLoop));
        try { return new BoundLabelledStatement(labelled.Label, BindStatement(labelled.Statement)); }
        finally { _labels.Pop(); }
    }

    private static bool LabelsIterationStatement(StatementSyntax statement)
    {
        while (statement is LabelledStatementSyntax labelled)
            statement = labelled.Statement;
        return statement is WhileStatementSyntax or DoWhileStatementSyntax or ForStatementSyntax or ForOfStatementSyntax or ForInStatementSyntax;
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
            ClassExpressionSyntax classExpression => BindClassExpression(classExpression),
            AsExpressionSyntax asExpr => BindAsExpression(asExpr),
            SatisfiesExpressionSyntax satisfiesExpr => BindSatisfiesExpression(satisfiesExpr),
            TypeofExpressionSyntax typeofExpr => BindTypeof(typeofExpr),
            VoidExpressionSyntax voidExpr => BindVoid(voidExpr),
            DeleteExpressionSyntax deleteExpr => BindDelete(deleteExpr),
            SpreadExpressionSyntax spread => BindSpread(spread),
            RegexLiteralExpressionSyntax regex => BindRegexLiteral(regex),
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
                if (lit.Token.Value is BigInteger bi)
                {
                    type = TsType.BigInt;
                    value = bi;
                }
                else if (lit.Token.Value is ulong ul)
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
                    return new BoundLiteralExpression(null, TsType.Void);
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
        var right = BindRightOperand(bin);
        var resultType = InferBinaryResultType(left.Type, right.Type, bin.OperatorToken.Kind);

        if (resultType == TsType.Void && left.Type != TsType.Void && right.Type != TsType.Void)
        {
            _diagnostics.Error(
                $"Operator '{bin.OperatorToken.Text}' cannot be applied to types '{left.Type}' and '{right.Type}'",
                bin.OperatorToken.Location);
        }

        return new BoundBinaryExpression(left, bin.OperatorToken.Kind, right, resultType);
    }

    private BoundNode BindRightOperand(BinaryExpressionSyntax binary)
    {
        if (binary.OperatorToken.Kind is not (TokenKind.AmpersandAmpersand or TokenKind.PipePipe))
        {
            return BindExpression(binary.Right);
        }

        var narrowing = TryGetNullCheckNarrowing(binary.Left);
        if (narrowing == null)
        {
            return BindExpression(binary.Right);
        }

        var rightType = binary.OperatorToken.Kind == TokenKind.AmpersandAmpersand
            ? narrowing.TrueType
            : narrowing.FalseType;
        if (rightType.Equals(narrowing.OriginalType))
        {
            return BindExpression(binary.Right);
        }

        return BindExpressionWithTemporaryNarrowings(
            binary.Right,
            new List<NullCheckNarrowing> { narrowing },
            binary.OperatorToken.Kind == TokenKind.AmpersandAmpersand);
    }

    private BoundNode BindExpressionWithTemporaryType(ExpressionSyntax expression, Symbol symbol, TsType type)
    {
        var previous = symbol.Type;
        symbol.Type = type;
        try
        {
            return BindExpression(expression);
        }
        finally
        {
            symbol.Type = previous;
        }
    }

    private BoundNode BindExpressionWithTemporaryTypes(
        ExpressionSyntax expression,
        IReadOnlyList<(Symbol Symbol, TsType Type)> types)
    {
        var previous = new List<(Symbol Symbol, TsType Type)>();
        foreach (var item in types)
        {
            previous.Add((item.Symbol, item.Symbol.Type));
            item.Symbol.Type = item.Type;
        }

        try
        {
            return BindExpression(expression);
        }
        finally
        {
            for (int i = previous.Count - 1; i >= 0; i--)
            {
                previous[i].Symbol.Type = previous[i].Type;
            }
        }
    }

    private BoundNode BindExpressionWithTemporaryNarrowings(
        ExpressionSyntax expression,
        IReadOnlyList<NullCheckNarrowing> narrowings,
        bool useTrueType)
    {
        return BindWithTemporaryNarrowings(narrowings, useTrueType, () => BindExpression(expression));
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
        List<CallParameter>? expectedParams = null;
        IReadOnlyList<TsTypeParameter> genericParameters = Array.Empty<TsTypeParameter>();
        IReadOnlyList<TsFunctionType> overloads = Array.Empty<TsFunctionType>();

        if (callee is BoundVariableExpression { Symbol: ClassSymbol { Name: "Array" } })
        {
            var args = BindCallArguments(call.Arguments, null);
            return new BoundArrayConstructionExpression(args, new TsArrayType(TsType.Any));
        }

        if (callee is BoundVariableExpression varExpr && varExpr.Symbol is FunctionSymbol funcSym)
        {
            returnType = funcSym.Type;
            genericParameters = funcSym.TypeParameters;
            overloads = funcSym.Overloads;
            expectedParams = funcSym.HasDynamicSignature || overloads.Count > 0
                ? null
                : funcSym.Parameters.Select(ToCallParameter).ToList();
        }
        else if (callee.Type is TsFunctionType fnType)
        {
            returnType = fnType.ReturnType;
            expectedParams = fnType.Parameters.Select(ToCallParameter).ToList();
        }
        else if (callee is BoundMemberAccessExpression memberExpr && memberExpr.Member is MethodSymbol methodSym)
        {
            returnType = methodSym.Type;
            genericParameters = methodSym.TypeParameters;
            expectedParams = methodSym.Parameters.Count > 0
                ? methodSym.Parameters.Select(ToCallParameter).ToList()
                : null;
        }
        else if (callee is BoundSuperExpression superExpr && superExpr.BaseClass.Constructor != null)
        {
            returnType = TsType.Void;
            expectedParams = superExpr.BaseClass.Constructor.Parameters
                .Select(ToCallParameter)
                .ToList();
        }

        var genericArguments = new Dictionary<string, TsType>(StringComparer.Ordinal);
        ApplyExplicitGenericArguments(call, genericParameters, genericArguments);
        var boundArgs = BindCallArguments(call.Arguments, expectedParams, genericArguments);
        if (overloads.Count > 0 && !boundArgs.Any(arg => arg is BoundSpreadExpression))
        {
            if (TrySelectOverload(boundArgs, overloads, out var selectedOverload))
            {
                returnType = selectedOverload.ReturnType;
            }
            else
            {
                _diagnostics.Error(
                    $"No overload matches call with argument types ({string.Join(", ", boundArgs.Select(arg => arg.Type.Name))})",
                    call.Range.Start);
            }
        }
        else if (expectedParams != null && !boundArgs.Any(arg => arg is BoundSpreadExpression))
        {
            var substitutedParams = expectedParams
                .Select(p => p with { Type = TsType.Substitute(p.Type, genericArguments) })
                .ToList();
            ValidateCallArguments(boundArgs, substitutedParams, call.Range.Start);
        }

        var boundReturnType = TsType.Substitute(returnType, genericArguments);
        if (call.IsNullConditional && boundReturnType is not TsNullableType)
            boundReturnType = new TsNullableType(boundReturnType);
        return new BoundCallExpression(callee, boundArgs, boundReturnType, call.IsNullConditional);
    }

    private static bool TrySelectOverload(
        IReadOnlyList<BoundNode> args,
        IReadOnlyList<TsFunctionType> overloads,
        out TsFunctionType selected)
    {
        foreach (var overload in overloads)
        {
            var parameters = overload.Parameters.Select(ToCallParameter).ToList();
            if (IsCallCompatibleWithParameters(args, parameters))
            {
                selected = overload;
                return true;
            }
        }

        selected = null!;
        return false;
    }

    private static bool IsCallCompatibleWithParameters(
        IReadOnlyList<BoundNode> args,
        IReadOnlyList<CallParameter> expectedParams)
    {
        int restIndex = expectedParams.ToList().FindIndex(p => p.IsRest);
        int fixedCount = restIndex >= 0 ? restIndex : expectedParams.Count;
        var requiredCount = fixedCount;
        while (requiredCount > 0 && expectedParams[requiredCount - 1].IsOptional)
            requiredCount--;

        if (args.Count < requiredCount || restIndex < 0 && args.Count > expectedParams.Count)
            return false;

        for (int i = 0; i < args.Count; i++)
        {
            var parameter = i < expectedParams.Count
                ? expectedParams[i]
                : expectedParams[restIndex];
            parameter = parameter with { Type = GetCallArgumentExpectedType(parameter) };
            if (!IsArgumentAssignableToParameter(args[i].Type, parameter))
                return false;
        }

        return true;
    }

    private void ApplyExplicitGenericArguments(
        CallExpressionSyntax call,
        IReadOnlyList<TsTypeParameter> genericParameters,
        Dictionary<string, TsType> genericArguments)
    {
        if (call.TypeArguments.Count == 0)
            return;

        if (genericParameters.Count == 0)
        {
            _diagnostics.Error("Type arguments can only be used on generic functions or methods", call.Range.Start);
            return;
        }

        if (call.TypeArguments.Count != genericParameters.Count)
        {
            _diagnostics.Error(
                $"Generic call expected {genericParameters.Count} type argument(s), got {call.TypeArguments.Count}",
                call.Range.Start);
        }

        int count = Math.Min(call.TypeArguments.Count, genericParameters.Count);
        for (int i = 0; i < count; i++)
            genericArguments[genericParameters[i].Name] = ResolveType(call.TypeArguments[i]);
    }

    private List<BoundNode> BindCallArguments(
        IReadOnlyList<ExpressionSyntax> arguments,
        IReadOnlyList<CallParameter>? expectedParams,
        Dictionary<string, TsType>? genericArguments = null)
    {
        var result = new List<BoundNode>(arguments.Count);
        int restIndex = expectedParams?.ToList().FindIndex(p => p.IsRest) ?? -1;
        for (int i = 0; i < arguments.Count; i++)
        {
            CallParameter? expectedParam = expectedParams != null
                ? i < expectedParams.Count
                    ? expectedParams[i]
                    : restIndex >= 0
                        ? expectedParams[restIndex]
                        : null
                : null;
            var expectedType = expectedParam != null
                ? TsType.Substitute(GetCallArgumentExpectedType(expectedParam.Value), genericArguments ?? new Dictionary<string, TsType>())
                : null;

            if (arguments[i] is SpreadExpressionSyntax spread)
                result.Add(BindSpread(spread));
            else if (arguments[i] is LambdaExpressionSyntax lambda && expectedType is TsFunctionType fnType)
                result.Add(BindInlineLambda(lambda, fnType));
            else if (arguments[i] is ObjectLiteralExpressionSyntax objLit && expectedType != null)
                result.Add(BindObjectLiteral(objLit, expectedType));
            else if (arguments[i] is ArrayLiteralExpressionSyntax arrLit && expectedType != null)
                result.Add(BindArrayLiteral(arrLit, expectedType));
            else
                result.Add(BindExpression(arguments[i]));

            if (expectedParam != null && genericArguments != null)
                InferGenericArguments(GetCallArgumentExpectedType(expectedParam.Value), result[i].Type, genericArguments);
        }

        return result;
    }

    private static TsType GetCallArgumentExpectedType(CallParameter parameter) =>
        parameter.IsRest && parameter.Type is TsArrayType arrayType
            ? arrayType.ElementType
            : parameter.Type;

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
        IReadOnlyList<CallParameter> expectedParams,
        SourceLocation location)
    {
        int restIndex = expectedParams.ToList().FindIndex(p => p.IsRest);
        int fixedCount = restIndex >= 0 ? restIndex : expectedParams.Count;
        var requiredCount = fixedCount;
        while (requiredCount > 0 && expectedParams[requiredCount - 1].IsOptional)
            requiredCount--;

        if (args.Count < requiredCount || restIndex < 0 && args.Count > expectedParams.Count)
        {
            _diagnostics.Error(
                restIndex >= 0
                    ? $"Expected at least {requiredCount} argument(s) but got {args.Count}"
                    : requiredCount == expectedParams.Count
                    ? $"Expected {expectedParams.Count} argument(s) but got {args.Count}"
                    : $"Expected between {requiredCount} and {expectedParams.Count} argument(s) but got {args.Count}",
                location);
            return;
        }

        for (int i = 0; i < args.Count; i++)
        {
            var argType = args[i].Type;
            var param = i < expectedParams.Count
                ? expectedParams[i]
                : expectedParams[restIndex];
            var expectedType = GetCallArgumentExpectedType(param);
            param = param with { Type = expectedType };
            if (!IsArgumentAssignableToParameter(argType, param))
            {
                _diagnostics.Error(
                    $"Argument {i + 1}: cannot assign '{argType}' to parameter '{param.Name}' of type '{expectedType}'",
                    location);
            }
        }
    }

    private static bool IsArgumentAssignableToParameter(TsType argumentType, CallParameter parameter)
    {
        if (argumentType == TsType.Void && parameter.IsOptional)
            return true;

        return TsType.IsCompatibleWith(argumentType, parameter.Type);
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
            if (generic.Definition is TsClassType genericClass)
                genericMap = TsType.CreateGenericMap(genericClass.TypeParameters, generic.TypeArguments);
            else if (generic.Definition is TsInterfaceType genericInterface)
                genericMap = TsType.CreateGenericMap(genericInterface.TypeParameters, generic.TypeArguments);
            objType = generic.Definition;
        }

        bool isStaticAccess = obj is BoundVariableExpression { Symbol: ClassSymbol };

        if (objType is TsClassType classType)
        {
            for (var current = classType; current != null && memberSym == null; current = current.BaseType)
            {
                if (current.Fields.TryGetValue(member.MemberName, out var field) && field.IsStatic == isStaticAccess)
                {
                    if (field.IsPrivateName && _currentClassType?.Name != current.Name)
                        _diagnostics.Error($"Private field '{member.MemberName}' is not accessible here", member.Range.Start);
                    memberSym = new FieldSymbol(member.MemberName, TsType.Substitute(field.Type, genericMap), member.Range)
                    {
                        IsReadonly = field.IsReadonly,
                        IsStatic = field.IsStatic,
                        IsPrivateName = field.IsPrivateName,
                        RuntimeName = field.IsStatic
                            ? $"$static:{current.Name}:{field.Name}"
                            : field.RuntimeName ?? field.Name
                    };
                }
                else if (current.Methods.TryGetValue(member.MemberName, out var method) &&
                         (method.IsStatic == isStaticAccess || isStaticAccess && IsBuiltinStaticNamespace(current.Name)))
                {
                    if (method.IsPrivateName && _currentClassType?.Name != current.Name)
                        _diagnostics.Error($"Private method '{member.MemberName}' is not accessible here", member.Range.Start);
                    memberSym = CreateMethodSymbol(method, current.Name, member.Range, genericMap);
                }
                else if (current.Properties.TryGetValue(member.MemberName, out var prop) && prop.IsStatic == isStaticAccess)
                {
                    if (prop.IsPrivateName && _currentClassType?.Name != current.Name)
                        _diagnostics.Error($"Private accessor '{member.MemberName}' is not accessible here", member.Range.Start);
                    memberSym = new PropertySymbol(member.MemberName, TsType.Substitute(prop.Type, genericMap), member.Range)
                    {
                        IsReadonly = prop.IsReadonly,
                        IsStatic = prop.IsStatic,
                        IsPrivateName = prop.IsPrivateName,
                        HasGetter = prop.HasGetter,
                        HasSetter = prop.HasSetter,
                        GetterName = prop.GetterName,
                        SetterName = prop.SetterName,
                        RuntimeName = prop.IsStatic ? $"$static:{current.Name}:{prop.Name}" : prop.RuntimeName,
                        DeclaringClassName = prop.DeclaringClassName ?? current.Name
                    };
                }
            }
        }
        else if (objType is TsUnionType unionType)
        {
            var memberTypes = new List<TsType>();
            foreach (var candidate in unionType.Types)
            {
                if (!TryGetReadableMemberType(candidate, member.MemberName, out var candidateMemberType))
                {
                    memberTypes.Clear();
                    break;
                }
                memberTypes.Add(candidateMemberType);
            }

            if (memberTypes.Count > 0)
            {
                var resultMemberType = memberTypes.Count == 1
                    ? memberTypes[0]
                    : new TsUnionType(DistinctTypes(memberTypes));
                memberSym = new PropertySymbol(member.MemberName, resultMemberType, member.Range);
            }
        }
        else if (objType is TsIntersectionType intersectionType)
        {
            foreach (var candidate in intersectionType.Types)
            {
                if (TryGetReadableMemberType(candidate, member.MemberName, out var candidateMemberType))
                {
                    memberSym = new PropertySymbol(member.MemberName, candidateMemberType, member.Range);
                    break;
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
        else if (objType is TsMapType mapType)
        {
            memberSym = BindMapMember(mapType, member.MemberName, member.Range);
        }
        else if (objType is TsSetType setType)
        {
            memberSym = BindSetMember(setType, member.MemberName, member.Range);
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

        if (GetMemberAccessPath(member) is { } accessPath &&
            TryGetTemporaryMemberAccessType(accessPath, out var narrowedType))
        {
            resultType = narrowedType;
        }

        return new BoundMemberAccessExpression(obj, memberSym, member.IsNullConditional, resultType);
    }

    private bool TryGetTemporaryMemberAccessType(string accessPath, out TsType type)
    {
        foreach (var scope in _memberAccessNarrowingScopes)
        {
            if (scope.TryGetValue(accessPath, out type!))
            {
                return true;
            }
        }

        type = TsType.Void;
        return false;
    }

    // Array/string builtin members: methods dispatch through the VM builtin
    // table under Array::/String:: names; parameter lists stay empty so the
    // binder does not enforce a fixed arity on variadic-style JS members.
    private Symbol? BindArrayMember(TsArrayType arrayType, string name, SourceRange range)
    {
        switch (name)
        {
            case "length":
                return new PropertySymbol("length", TsType.Int32, range) { IsReadonly = true };
            case "push":
            case "unshift":
                if (arrayType.IsReadonly)
                    return null;
                return BuiltinMethod(name, TsType.Int32, "Array", range);
            case "pop":
            case "shift":
                if (arrayType.IsReadonly)
                    return null;
                return BuiltinMethod(name, arrayType.ElementType, "Array", range);
            case "reverse":
            case "fill":
            case "splice":
                if (arrayType.IsReadonly)
                    return null;
                return BuiltinMethod(name, arrayType, "Array", range);
            case "slice":
            case "concat":
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
                return BuiltinMethod(name, arrayType, "Array", range);
            case "sort":
                if (arrayType.IsReadonly)
                    return null;
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
            IsAsync = method.IsAsync,
            IsStatic = method.IsStatic,
            IsAbstract = method.IsAbstract,
            IsPrivateName = method.IsPrivateName,
            RuntimeName = method.RuntimeName
        };
        sym.TypeParameters.AddRange(method.TypeParameters.Select(p => new TsTypeParameter(p.Name)));
        foreach (var p in method.Parameters)
        {
            sym.Parameters.Add(new ParameterSymbol(p.Name, TsType.Substitute(p.Type, genericMap), range)
            {
                HasDefault = p.HasDefault,
                DefaultValue = p.DefaultValue,
                IsRest = p.IsRest
            });
        }
        return sym;
    }

    private BoundNode BindAssignment(AssignmentExpressionSyntax assign)
    {
        if (assign.OperatorToken.Kind == TokenKind.Equals &&
            assign.Target is ArrayLiteralExpressionSyntax or ObjectLiteralExpressionSyntax)
        {
            return BindDestructuringAssignment(assign);
        }

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
                PropertySymbol property => property.IsReadonly || property.HasGetter && !property.HasSetter,
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

    private BoundDestructuringAssignmentExpression BindDestructuringAssignment(AssignmentExpressionSyntax assign)
    {
        var source = BindExpression(assign.Value);
        var sourceTemp = new LocalSymbol($"$destructure_assign_{_forOfCounter++}", source.Type, assign.Range, isConst: true);
        DefineWithDepth(sourceTemp);

        var temporaries = new List<BoundVariableDeclaration>
        {
            new(sourceTemp, source)
        };
        var assignments = new List<BoundAssignmentExpression>();

        BindDestructuringAssignmentPattern(
            assign.Target,
            new BoundVariableExpression(sourceTemp),
            temporaries,
            assignments,
            new List<string>());

        return new BoundDestructuringAssignmentExpression(
            temporaries,
            assignments,
            new BoundVariableExpression(sourceTemp));
    }

    private void BindDestructuringAssignmentPattern(
        ExpressionSyntax pattern,
        BoundNode source,
        List<BoundVariableDeclaration> temporaries,
        List<BoundAssignmentExpression> assignments,
        List<string> excludedObjectKeys)
    {
        switch (pattern)
        {
            case ArrayLiteralExpressionSyntax array:
                for (int i = 0; i < array.Elements.Count; i++)
                {
                    var element = array.Elements[i];
                    if (element is SpreadExpressionSyntax spread)
                    {
                        var target = BindDestructuringTarget(spread.Expression);
                        assignments.Add(new BoundAssignmentExpression(target, new BoundArraySliceExpression(source, i)));
                        continue;
                    }

                    var value = new BoundIndexExpression(source, new BoundLiteralExpression(i, TsType.Int32), TsType.Any);
                    BindDestructuringAssignmentElement(element, value, temporaries, assignments, excludedObjectKeys);
                }
                break;

            case ObjectLiteralExpressionSyntax obj:
                var localExcludedObjectKeys = new List<string>();
                foreach (var property in obj.Properties)
                {
                    if (property is ObjectSpreadPropertySyntax spread)
                    {
                        var target = BindDestructuringTarget(spread.Value);
                        assignments.Add(new BoundAssignmentExpression(target, new BoundObjectRestExpression(source, localExcludedObjectKeys.ToList())));
                        continue;
                    }

                    if (property is ComputedObjectPropertySyntax computed)
                    {
                        var key = BindExpression(computed.KeyExpression);
                        var value = new BoundIndexExpression(source, key, TsType.Any);
                        BindDestructuringAssignmentElement(computed.Value, value, temporaries, assignments, localExcludedObjectKeys);
                    }
                    else
                    {
                        localExcludedObjectKeys.Add(property.Key);
                        var value = new BoundMemberAccessExpression(source, new PropertySymbol(property.Key, TsType.Any, property.Range));
                        BindDestructuringAssignmentElement(property.Value, value, temporaries, assignments, localExcludedObjectKeys);
                    }
                }
                break;

            default:
                assignments.Add(new BoundAssignmentExpression(BindDestructuringTarget(pattern), source));
                break;
        }
    }

    private void BindDestructuringAssignmentElement(
        ExpressionSyntax targetPattern,
        BoundNode value,
        List<BoundVariableDeclaration> temporaries,
        List<BoundAssignmentExpression> assignments,
        List<string> excludedObjectKeys)
    {
        ExpressionSyntax target = targetPattern;
        ExpressionSyntax? defaultValue = null;
        if (targetPattern is AssignmentExpressionSyntax { OperatorToken.Kind: TokenKind.Equals } defaulted)
        {
            target = defaulted.Target;
            defaultValue = defaulted.Value;
        }

        if (target is ArrayLiteralExpressionSyntax or ObjectLiteralExpressionSyntax)
        {
            var temp = new LocalSymbol($"$destructure_nested_{_forOfCounter++}", TsType.Any, target.Range, isConst: true);
            DefineWithDepth(temp);
            temporaries.Add(new BoundVariableDeclaration(temp, ApplyDestructuringDefault(value, defaultValue)));
            BindDestructuringAssignmentPattern(target, new BoundVariableExpression(temp), temporaries, assignments, excludedObjectKeys);
            return;
        }

        assignments.Add(new BoundAssignmentExpression(
            BindDestructuringTarget(target),
            ApplyDestructuringDefault(value, defaultValue)));
    }

    private BoundNode ApplyDestructuringDefault(BoundNode value, ExpressionSyntax? defaultValue)
    {
        if (defaultValue == null)
            return value;

        var fallback = BindExpression(defaultValue);
        var isUndefined = new BoundBinaryExpression(value, TokenKind.TripleEquals,
            new BoundLiteralExpression(null, TsType.Void), TsType.Bool);
        var isNull = new BoundBinaryExpression(value, TokenKind.TripleEquals,
            new BoundLiteralExpression(null, TsType.Any), TsType.Bool);
        var isMissing = new BoundBinaryExpression(isUndefined, TokenKind.PipePipe, isNull, TsType.Bool);
        return new BoundConditionalExpression(isMissing, fallback, value, TsType.Any);
    }

    private BoundNode BindDestructuringTarget(ExpressionSyntax target)
    {
        var bound = BindExpression(target);
        if (bound is not (BoundVariableExpression or BoundMemberAccessExpression or BoundIndexExpression))
            _diagnostics.Error("Invalid destructuring assignment target", target.Range.Start);
        return bound;
    }

    private BoundConditionalExpression BindConditional(ConditionalExpressionSyntax cond)
    {
        var condition = BindExpression(cond.Condition);
        ValidateCondition(condition, cond.Condition.Range.Start);
        var trueNarrowings = TryGetTruePathNullCheckNarrowings(cond.Condition);
        var whenTrue = trueNarrowings.Count > 0
            ? BindExpressionWithTemporaryNarrowings(cond.WhenTrue, trueNarrowings, useTrueType: true)
            : BindExpression(cond.WhenTrue);
        var falseNarrowings = TryGetFalsePathNullCheckNarrowings(cond.Condition);
        var whenFalse = falseNarrowings.Count > 0
            ? BindExpressionWithTemporaryNarrowings(cond.WhenFalse, falseNarrowings, useTrueType: false)
            : BindExpression(cond.WhenFalse);

        TsType resultType = InferConditionalResultType(whenTrue.Type, whenFalse.Type);

        return new BoundConditionalExpression(condition, whenTrue, whenFalse, resultType);
    }

    private static TsType InferConditionalResultType(TsType whenTrue, TsType whenFalse)
    {
        if (whenTrue is TsAnyType || whenFalse is TsAnyType)
            return TsType.Any;

        if (whenTrue is TsNullType)
            return whenFalse is TsNullableType ? whenFalse : new TsNullableType(whenFalse);

        if (whenFalse is TsNullType)
            return whenTrue is TsNullableType ? whenTrue : new TsNullableType(whenTrue);

        if (whenTrue is TsNullableType nullableTrue && whenFalse.IsAssignableTo(nullableTrue.ElementType))
            return nullableTrue;

        if (whenFalse is TsNullableType nullableFalse && whenTrue.IsAssignableTo(nullableFalse.ElementType))
            return nullableFalse;

        return whenTrue.IsAssignableTo(whenFalse) ? whenTrue :
               whenFalse.IsAssignableTo(whenTrue) ? whenFalse :
               whenTrue;
    }

    private BoundNode BindNew(NewExpressionSyntax newExpr)
    {
        var type = ResolveType(newExpr.Type);
        var args = newExpr.Arguments.Select(BindExpression).ToList();

        // `new Array(n)` builds a real array value, not a class instance.
        if (type is TsArrayType arrayNew)
            return new BoundArrayConstructionExpression(args, arrayNew);

        if (type is TsClassType { IsAbstract: true } abstractClass)
            _diagnostics.Error($"Cannot create an instance of abstract class '{abstractClass.Name}'", newExpr.Range.Start);

        if (type is TsClassType { Constructor: not null } ctorClass)
        {
            ValidateCallArguments(
                args,
                ctorClass.Constructor.Parameters.Select(ToCallParameter).ToList(),
                newExpr.Range.Start);
        }

        return new BoundNewExpression(type, args);
    }

    private BoundThisExpression BindThis(ThisExpressionSyntax thisExpr)
    {
        var type = _currentClassType ?? new TsClassType("Unknown");
        if (_currentThisSymbol != null)
            RegisterPossibleCapture(_currentThisSymbol);
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
                if (prop is ObjectSpreadPropertySyntax)
                {
                    var spreadValue = BindExpression(prop.Value);
                    properties.Add(new BoundObjectPropertyNode(string.Empty, spreadValue, spreadValue.Type, isSpread: true));
                    continue;
                }

                if (prop is ComputedObjectPropertySyntax computed)
                {
                    var key = BindExpression(computed.KeyExpression);
                    var computedValue = BindExpression(computed.Value);
                    properties.Add(new BoundObjectPropertyNode(string.Empty, computedValue, computedValue.Type, computedKey: key));
                    continue;
                }

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

                var value = BindExpressionWithExpectedType(prop.Value, expectedPropertyType);

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
                if (prop is ObjectSpreadPropertySyntax)
                {
                    properties.Add(new BoundObjectPropertyNode(string.Empty, value, value.Type, isSpread: true));
                    AddSpreadFieldsToStructuralType(structuralType, value.Type);
                    continue;
                }

                if (prop is ComputedObjectPropertySyntax computed)
                {
                    var key = BindExpression(computed.KeyExpression);
                    properties.Add(new BoundObjectPropertyNode(string.Empty, value, value.Type, computedKey: key));
                    continue;
                }

                properties.Add(new BoundObjectPropertyNode(prop.Key, value, value.Type));
                structuralType.Fields[prop.Key] = new TsField(prop.Key, value.Type);
            }
        }

        return new BoundObjectLiteralExpression(properties, resultType);
    }

    private static void AddSpreadFieldsToStructuralType(TsClassType target, TsType spreadType)
    {
        if (spreadType is TsClassType classType)
        {
            foreach (var field in classType.Fields.Values)
                target.Fields[field.Name] = new TsField(field.Name, field.Type)
                {
                    IsReadonly = field.IsReadonly,
                    IsStatic = field.IsStatic,
                    AccessModifier = field.AccessModifier
                };
        }
        else if (spreadType is TsInterfaceType interfaceType)
        {
            foreach (var property in interfaceType.Properties.Values)
                target.Fields[property.Name] = new TsField(property.Name, property.Type)
                {
                    IsReadonly = property.IsReadonly,
                    AccessModifier = property.AccessModifier
                };
        }
    }

    private BoundNode BindExpressionWithExpectedType(ExpressionSyntax expression, TsType expectedType)
    {
        var unwrappedExpectedType = expectedType is TsNullableType nullable
            ? nullable.ElementType
            : expectedType;

        return expression switch
        {
            ObjectLiteralExpressionSyntax nested => BindObjectLiteral(nested, unwrappedExpectedType),
            LambdaExpressionSyntax lambda when unwrappedExpectedType is TsFunctionType fnType =>
                BindInlineLambda(lambda, fnType),
            ArrayLiteralExpressionSyntax array => BindArrayLiteral(array, unwrappedExpectedType),
            _ => BindExpression(expression)
        };
    }

    private BoundLambdaExpression BindInlineLambda(LambdaExpressionSyntax lambda, TsFunctionType? contextualType = null)
    {
        var name = CreateLambdaName(lambda);
        var function = BindLambdaAsFunction(name, lambda, exported: false, contextualType);

        var fnType = new TsFunctionType(
            function.Symbol.Parameters.Select(CreateTypeParameter).ToList(),
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

    private BoundNode BindVoid(VoidExpressionSyntax voidExpr)
    {
        var operand = BindExpression(voidExpr.Operand);
        return new BoundVoidExpression(operand);
    }

    private BoundNode BindDelete(DeleteExpressionSyntax deleteExpr)
    {
        switch (deleteExpr.Operand)
        {
            case MemberAccessExpressionSyntax memberAccess:
            {
                var obj = BindExpression(memberAccess.Object);
                return new BoundDeleteFieldExpression(obj, memberAccess.MemberName, memberAccess.IsNullConditional);
            }
            case IndexExpressionSyntax indexExpr:
            {
                var obj = BindExpression(indexExpr.Object);
                var index = BindExpression(indexExpr.Index);
                return new BoundDeleteIndexExpression(obj, index);
            }
            default:
            {
                // Evaluate operand for side effects, result discarded.
                // `delete <non-target>` is a no-op returning false.
                var operand = BindExpression(deleteExpr.Operand);
                return new BoundDeleteNonReferenceExpression(operand);
            }
        }
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

    private BoundNode BindSatisfiesExpression(SatisfiesExpressionSyntax satisfiesExpr)
    {
        var operand = BindExpression(satisfiesExpr.Expression);
        var targetType = ResolveType(satisfiesExpr.TargetType);
        if (!TsType.IsCompatibleWith(operand.Type, targetType))
        {
            _diagnostics.Error(
                $"Type '{operand.Type}' does not satisfy '{targetType}'",
                satisfiesExpr.Range.Start);
        }

        // `satisfies` is a static check only: unlike `as`, it preserves the
        // original expression type and emits no runtime conversion.
        return operand;
    }

    private BoundNode BindForOf(ForOfStatementSyntax forOf)
    {
        // Desugars to an index-based loop over a VM-materialized iterable.
        // Spread, Array.from and for...of share the same runtime iterable policy.
        var iterable = BindExpression(forOf.Iterable);
        TsType elementType = iterable.Type switch
        {
            TsArrayType arr => arr.ElementType,
            TsPrimitiveType { Name: "string" } => TsType.String,
            _ => TsType.Any
        };
        if (elementType is TsAnyType && iterable.Type is not (TsAnyType or TsClassType or TsInterfaceType or TsMapType or TsSetType))
        {
            _diagnostics.Error($"for...of requires an iterable value, got '{iterable.Type}'",
                forOf.Iterable.Range.Start);
        }

        _symbolTable.PushScope();
        int id = _forOfCounter++;

        var arrSym = new LocalSymbol($"$of_arr_{id}", new TsArrayType(elementType), forOf.Range, isConst: true);
        var idxSym = new LocalSymbol($"$of_idx_{id}", TsType.Int32, forOf.Range);
        DefineWithDepth(arrSym);
        DefineWithDepth(idxSym);

        var arrDecl = new BoundVariableDeclaration(arrSym, new BoundIterableValuesExpression(iterable));
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

        _loopDepth++;
        _breakableDepth++;
        BoundNode body;
        try
        {
            body = BindStatement(forOf.Body);
        }
        finally
        {
            _loopDepth--;
            _breakableDepth--;
        }
        var loopBody = new BoundBlockStatement(new List<BoundNode> { elementDecl, body });
        var loop = new BoundForStatement(idxDecl, condition, increment, loopBody);

        _symbolTable.PopScope();
        return new BoundBlockStatement(new List<BoundNode> { arrDecl, loop });
    }

    private BoundRegexLiteralExpression BindRegexLiteral(RegexLiteralExpressionSyntax regex)
    {
        return new BoundRegexLiteralExpression(regex.Pattern, regex.Flags);
    }

    private BoundSpreadExpression BindSpread(SpreadExpressionSyntax spread)
    {
        var expression = BindExpression(spread.Expression);
        return new BoundSpreadExpression(expression);
    }

    private BoundNode BindForIn(ForInStatementSyntax forIn)
    {
        // Snapshot keys exactly once before the loop. This makes mutation during
        // iteration deterministic and prevents the body from observing host members.
        var enumerable = BindExpression(forIn.Enumerable);
        bool supported = enumerable.Type is TsAnyType or TsArrayType or TsClassType or TsInterfaceType ||
                         enumerable.Type == TsType.String;
        if (!supported)
        {
            _diagnostics.Error($"for...in requires an object, array, string, or any, got '{enumerable.Type}'",
                forIn.Enumerable.Range.Start);
        }

        _symbolTable.PushScope();
        int id = _forOfCounter++;
        var keysType = new TsArrayType(TsType.String);
        var keysSym = new LocalSymbol($"$in_keys_{id}", keysType, forIn.Range, isConst: true);
        var idxSym = new LocalSymbol($"$in_idx_{id}", TsType.Int32, forIn.Range);
        DefineWithDepth(keysSym);
        DefineWithDepth(idxSym);

        var keysDecl = new BoundVariableDeclaration(keysSym, new BoundEnumerateKeysExpression(enumerable));
        var idxDecl = new BoundVariableDeclaration(idxSym, new BoundLiteralExpression(0, TsType.Int32));
        var condition = new BoundBinaryExpression(
            new BoundVariableExpression(idxSym), TokenKind.LessThan,
            new BoundMemberAccessExpression(new BoundVariableExpression(keysSym),
                new PropertySymbol("length", TsType.Int32, forIn.Range)), TsType.Bool);
        var increment = new BoundAssignmentExpression(new BoundVariableExpression(idxSym),
            new BoundBinaryExpression(new BoundVariableExpression(idxSym), TokenKind.Plus,
                new BoundLiteralExpression(1, TsType.Int32), TsType.Int32));
        var keySym = new LocalSymbol(forIn.VariableName, TsType.String, forIn.Range, forIn.IsConst);
        DefineWithDepth(keySym);
        var keyDecl = new BoundVariableDeclaration(keySym,
            new BoundIndexExpression(new BoundVariableExpression(keysSym), new BoundVariableExpression(idxSym), TsType.String));

        _loopDepth++;
        _breakableDepth++;
        BoundNode body;
        try { body = BindStatement(forIn.Body); }
        finally { _loopDepth--; _breakableDepth--; }

        _symbolTable.PopScope();
        return new BoundBlockStatement(new List<BoundNode>
        {
            keysDecl,
            new BoundForStatement(idxDecl, condition, increment,
                new BoundBlockStatement(new List<BoundNode> { keyDecl, body }))
        });
    }

    private BoundArrayLiteralExpression BindArrayLiteral(ArrayLiteralExpressionSyntax arrLit, TsType? expectedType = null)
    {
        var expectedElementType = expectedType switch
        {
            TsNullableType nullable => nullable.ElementType,
            TsArrayType array => array.ElementType,
            _ => expectedType
        };

        var elements = new List<BoundNode>(arrLit.Elements.Count);
        for (int i = 0; i < arrLit.Elements.Count; i++)
        {
            if (arrLit.Elements[i] is SpreadExpressionSyntax spread)
                elements.Add(BindSpread(spread));
            else if (expectedElementType != null)
                elements.Add(BindExpressionWithExpectedType(arrLit.Elements[i], expectedElementType));
            else
                elements.Add(BindExpression(arrLit.Elements[i]));
        }

        var firstNormalElement = elements.FirstOrDefault(e => e is not BoundSpreadExpression);
        var elementType = expectedElementType ?? firstNormalElement?.Type ?? TsType.Any;

        for (int i = expectedElementType != null ? 0 : 1; i < elements.Count; i++)
        {
            if (elements[i] is BoundSpreadExpression)
                continue;

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

        var classIndexSignature = obj.Type is TsClassType cls
            ? cls.IndexSignatures.FirstOrDefault(sig => IsIndexKeyCompatible(index.Type, sig.KeyType))
            : null;

        var resultType = obj.Type switch
        {
            TsArrayType arr => arr.ElementType,
            TsClassType { Name: "Uint8Array" } => TsType.Number,
            TsClassType when classIndexSignature != null => classIndexSignature.ValueType,
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
        else if (obj.Type is TsClassType { IndexSignatures.Count: > 0 } && classIndexSignature == null && index.Type is not TsAnyType)
        {
            _diagnostics.Error($"Index type '{index.Type}' is not compatible with declared index signatures",
                indexExpr.Index.Range.Start);
        }

        if (indexExpr.IsNullConditional && resultType is not TsNullableType)
            resultType = new TsNullableType(resultType);
        return new BoundIndexExpression(obj, index, resultType, indexExpr.IsNullConditional);
    }

    private static bool IsBuiltinStaticNamespace(string name) =>
        name is "Math" or "Number" or "console" or "Array" or "Date" or "Uint8Array";

    private static bool IsIndexKeyCompatible(TsType actual, TsType expected)
    {
        if (actual is TsAnyType || expected is TsAnyType)
            return true;
        if (actual is TsLiteralType literal)
            actual = literal.BaseType;
        if (expected == TsType.String && actual == TsType.String)
            return true;
        if (expected == TsType.Number && actual is TsPrimitiveType { IsNumericType: true })
            return true;
        return TsType.IsCompatibleWith(actual, expected);
    }

    // Type resolution
    public TsType ResolveType(TypeSyntax? typeSyntax)
    {
        if (typeSyntax == null) return TsType.Void;

        return typeSyntax switch
        {
            PrimitiveTypeSyntax prim => TsType.FromToken(prim.TypeKeyword.Kind),
            LiteralTypeSyntax literal => ResolveLiteralType(literal),
            NamedTypeSyntax named => ResolveNamedType(named),
            ArrayTypeSyntax arr => new TsArrayType(ResolveType(arr.ElementType), arr.IsReadonly),
            IndexedAccessTypeSyntax indexed => ResolveIndexedAccessType(indexed),
            MapTypeSyntax map => new TsMapType(ResolveType(map.KeyType), ResolveType(map.ValueType)),
            PromiseTypeSyntax promise => new TsPromiseType(ResolveType(promise.ElementType)),
            NullableTypeSyntax nullable => new TsNullableType(ResolveType(nullable.ElementType)),
            UnionTypeSyntax union => ResolveUnionType(union),
            IntersectionTypeSyntax intersection => ResolveIntersectionType(intersection),
            ConditionalTypeSyntax conditional => ResolveConditionalType(conditional),
            KeyofTypeSyntax keyof => ResolveKeyofType(keyof),
            TypeofTypeSyntax typeQuery => ResolveTypeofType(typeQuery),
            TypePredicateSyntax => TsType.Bool,
            InferTypeSyntax infer => ResolveInferType(infer),
            AssertionTypeSyntax => TsType.Void,
            FunctionTypeSyntax fn => new TsFunctionType(
                fn.Parameters.Select(CreateTypeParameter).ToList(),
                ResolveType(fn.ReturnType)),
            ObjectTypeSyntax anon => ResolveObjectType(anon),
            MappedTypeSyntax mapped => ResolveMappedType(mapped),
            TupleTypeSyntax tuple => new TsTupleType(tuple.ElementTypes.Select(ResolveType).ToList()),
            _ => TsType.Void
        };
    }

    private static TsType ResolveLiteralType(LiteralTypeSyntax literal)
    {
        var token = literal.LiteralToken;
        return token.Kind switch
        {
            TokenKind.StringLiteral => new TsLiteralType(token.Value?.ToString() ?? token.Text, TsType.String),
            TokenKind.IntegerLiteral => new TsLiteralType(token.Value, TsType.Int64),
            TokenKind.FloatLiteral => new TsLiteralType(token.Value, TsType.Number),
            TokenKind.TrueLiteral => new TsLiteralType(true, TsType.Bool),
            TokenKind.FalseLiteral => new TsLiteralType(false, TsType.Bool),
            TokenKind.TemplateLiteral => (token.Value?.ToString() ?? token.Text).Contains("${", StringComparison.Ordinal)
                ? TsType.String
                : new TsLiteralType(token.Value?.ToString() ?? token.Text, TsType.String),
            TokenKind.NullLiteral => TsType.Null,
            _ => TsType.Any
        };
    }

    private TsType ResolveConditionalType(ConditionalTypeSyntax conditional)
    {
        var checkType = ResolveType(conditional.CheckType);
        var inferredTypes = new Dictionary<string, TsType>(StringComparer.Ordinal);
        if (TryInferConditionalTypes(checkType, conditional.ExtendsType, inferredTypes))
        {
            _typeValueScopes.Add(inferredTypes);
            try
            {
                return ResolveType(conditional.TrueType);
            }
            finally
            {
                _typeValueScopes.RemoveAt(_typeValueScopes.Count - 1);
            }
        }

        var extendsType = ResolveType(conditional.ExtendsType);

        if (checkType is TsAnyType or TsUnknownType || extendsType is TsAnyType or TsUnknownType)
            return new TsUnionType(DistinctTypes(new[] { ResolveType(conditional.TrueType), ResolveType(conditional.FalseType) }));

        return TsType.IsCompatibleWith(checkType, extendsType)
            ? ResolveType(conditional.TrueType)
            : ResolveType(conditional.FalseType);
    }

    private TsType ResolveInferType(InferTypeSyntax infer)
    {
        _diagnostics.Error($"'infer {infer.Name}' can only be used in the extends clause of a conditional type", infer.Range.Start);
        return TsType.Any;
    }

    private bool TryInferConditionalTypes(TsType actualType, TypeSyntax pattern, Dictionary<string, TsType> inferredTypes)
    {
        switch (pattern)
        {
            case InferTypeSyntax infer:
                inferredTypes[infer.Name] = actualType;
                return true;
            case ArrayTypeSyntax arrayPattern when actualType is TsArrayType actualArray:
                return TryInferConditionalTypes(actualArray.ElementType, arrayPattern.ElementType, inferredTypes);
            case PromiseTypeSyntax promisePattern when actualType is TsPromiseType actualPromise:
                return TryInferConditionalTypes(actualPromise.ElementType, promisePattern.ElementType, inferredTypes);
            case NamedTypeSyntax { Name: "Array", TypeArguments.Count: > 0 } namedArray when actualType is TsArrayType actualNamedArray:
                return TryInferConditionalTypes(actualNamedArray.ElementType, namedArray.TypeArguments[0], inferredTypes);
            case NamedTypeSyntax { Name: "Promise", TypeArguments.Count: > 0 } namedPromise when actualType is TsPromiseType actualNamedPromise:
                return TryInferConditionalTypes(actualNamedPromise.ElementType, namedPromise.TypeArguments[0], inferredTypes);
            case NamedTypeSyntax { Name: "Set", TypeArguments.Count: > 0 } namedSet when actualType is TsSetType actualSet:
                return TryInferConditionalTypes(actualSet.ElementType, namedSet.TypeArguments[0], inferredTypes);
            case NamedTypeSyntax { Name: "Map", TypeArguments.Count: > 1 } namedMap when actualType is TsMapType actualMap:
                return TryInferConditionalTypes(actualMap.KeyType, namedMap.TypeArguments[0], inferredTypes) &&
                       TryInferConditionalTypes(actualMap.ValueType, namedMap.TypeArguments[1], inferredTypes);
            default:
                if (ContainsInferType(pattern))
                    return false;
                return TsType.IsCompatibleWith(actualType, ResolveType(pattern));
        }
    }

    private static bool ContainsInferType(TypeSyntax type)
    {
        if (type is InferTypeSyntax)
            return true;
        return type.GetChildren().OfType<TypeSyntax>().Any(ContainsInferType);
    }

    private TsType ResolveIntersectionType(IntersectionTypeSyntax intersection)
    {
        var members = new List<TsType>();
        foreach (var member in intersection.Types.Select(ResolveType))
        {
            if (member is TsNeverType)
                return TsType.Never;
            if (member is TsIntersectionType nested)
                members.AddRange(nested.Types);
            else if (member is not TsAnyType)
                members.Add(member);
        }

        if (members.Count == 0)
            return TsType.Any;
        if (members.Count == 1)
            return members[0];
        return new TsIntersectionType(DistinctTypes(members));
    }

    private TsType ResolveKeyofType(KeyofTypeSyntax keyof)
    {
        var keys = GetPropertyKeys(ResolveType(keyof.Operand));
        return keys.Count switch
        {
            0 => TsType.Never,
            1 => keys[0],
            _ => new TsUnionType(DistinctTypes(keys))
        };
    }

    private TsType ResolveTypeofType(TypeofTypeSyntax typeQuery)
    {
        var operand = BindExpression(typeQuery.Operand);
        return operand.Type;
    }

    private TsType ResolveIndexedAccessType(IndexedAccessTypeSyntax indexed)
    {
        var objectType = ResolveType(indexed.ObjectType);
        var indexType = ResolveType(indexed.IndexType);
        return ResolveIndexedAccessType(objectType, indexType, indexed.Range.Start);
    }

    private TsType ResolveIndexedAccessType(TsType objectType, TsType indexType, SourceLocation location)
    {
        objectType = objectType is TsGenericType genericObject ? genericObject.Definition : objectType;

        if (indexType is TsUnionType unionIndex)
        {
            var resolved = unionIndex.Types
                .Select(index => ResolveIndexedAccessType(objectType, index, location))
                .Where(type => type is not TsNeverType)
                .ToList();
            return resolved.Count switch
            {
                0 => TsType.Never,
                1 => resolved[0],
                _ => new TsUnionType(DistinctTypes(resolved))
            };
        }

        if (objectType is TsUnionType unionObject)
        {
            var resolved = unionObject.Types
                .Select(type => ResolveIndexedAccessType(type, indexType, location))
                .Where(type => type is not TsNeverType)
                .ToList();
            return resolved.Count switch
            {
                0 => TsType.Never,
                1 => resolved[0],
                _ => new TsUnionType(DistinctTypes(resolved))
            };
        }

        if (objectType is TsIntersectionType intersectionObject)
        {
            var resolved = intersectionObject.Types
                .Select(type => ResolveIndexedAccessType(type, indexType, location))
                .Where(type => type is not TsNeverType)
                .ToList();
            return resolved.Count switch
            {
                0 => TsType.Never,
                1 => resolved[0],
                _ => new TsUnionType(DistinctTypes(resolved))
            };
        }

        if (TryGetLiteralKey(indexType, out var key))
        {
            if (TryGetReadableMemberType(objectType, key, out var memberType))
                return memberType;

            if (int.TryParse(key, out var ordinal) && objectType is TsTupleType tuple &&
                ordinal >= 0 && ordinal < tuple.ElementTypes.Count)
                return tuple.ElementTypes[ordinal];
        }

        if (objectType is TsArrayType array && IsNumericIndexType(indexType))
            return array.ElementType;
        if (objectType is TsTupleType tupleType && IsNumericIndexType(indexType))
            return tupleType.UnifiedElementType();
        if (objectType is TsMapType map && TsType.IsCompatibleWith(indexType, map.KeyType))
            return map.ValueType;
        if (TryGetIndexSignatureType(objectType, indexType, out var indexSignatureType))
            return indexSignatureType;

        _diagnostics.Error($"Type '{objectType}' has no index signature for '{indexType}'", location);
        return TsType.Any;
    }

    private TsType ResolveUnionType(UnionTypeSyntax union)
    {
        var members = union.Types
            .Select(ResolveType)
            .Where(type => type is not TsNeverType)
            .ToList();
        if (members.Any(type => type is TsUnknownType))
            return TsType.Unknown;
        members = DistinctTypes(members);
        if (members.Count == 0)
            return TsType.Never;
        if (members.Count == 1)
            return members[0];

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
            iface.Properties[m.Name] = new TsProperty(m.Name, memberType)
            {
                IsReadonly = m.IsReadonly
            };
        }
        return iface;
    }

    private TsInterfaceType ResolveMappedType(MappedTypeSyntax mappedType)
    {
        var iface = new TsInterfaceType($"$mapped_{mappedType.Range.Start.Line}_{mappedType.Range.Start.Column}");
        var keys = GetMappedKeys(ResolveType(mappedType.SourceType));
        foreach (var key in keys)
        {
            if (!TryGetLiteralKey(key, out var propertyName) || string.IsNullOrEmpty(propertyName))
                continue;

            _typeValueScopes.Add(new Dictionary<string, TsType>(StringComparer.Ordinal)
            {
                [mappedType.TypeParameterName] = key
            });
            try
            {
                iface.Properties[propertyName] = new TsProperty(propertyName, ResolveType(mappedType.ValueType));
            }
            finally
            {
                _typeValueScopes.RemoveAt(_typeValueScopes.Count - 1);
            }
        }

        return iface;
    }

    private static List<TsType> GetMappedKeys(TsType sourceType)
    {
        if (sourceType is TsLiteralType)
            return new List<TsType> { sourceType };
        if (sourceType is TsUnionType union && union.Types.All(type => type is TsLiteralType))
            return DistinctTypes(union.Types);
        return GetPropertyKeys(sourceType);
    }

    private static List<TsType> DistinctTypes(IEnumerable<TsType> types)
    {
        var result = new List<TsType>();
        foreach (var type in types)
        {
            if (result.All(existing => !existing.Equals(type)))
                result.Add(type);
        }
        return result;
    }

    private static bool TryGetLiteralKey(TsType type, out string key)
    {
        if (type is TsLiteralType literal)
        {
            key = literal.Value?.ToString() ?? "";
            return literal.BaseType == TsType.String ||
                   literal.BaseType == TsType.Int64 ||
                   literal.BaseType == TsType.Int32 ||
                   literal.BaseType == TsType.Number;
        }

        key = "";
        return false;
    }

    private static bool IsNumericIndexType(TsType type) =>
        type == TsType.Number ||
        type == TsType.Int8 ||
        type == TsType.UInt8 ||
        type == TsType.Int16 ||
        type == TsType.UInt16 ||
        type == TsType.Int32 ||
        type == TsType.UInt32 ||
        type == TsType.Int64 ||
        type == TsType.UInt64 ||
        type is TsLiteralType { BaseType: TsPrimitiveType { IsNumericType: true } };

    private static bool TryGetIndexSignatureType(TsType objectType, TsType indexType, out TsType resultType)
    {
        objectType = objectType is TsGenericType generic ? generic.Definition : objectType;
        if (objectType is TsClassType cls)
        {
            var match = cls.IndexSignatures.FirstOrDefault(sig => IsIndexKeyCompatible(indexType, sig.KeyType));
            if (match != null)
            {
                resultType = match.ValueType;
                return true;
            }
        }

        resultType = TsType.Void;
        return false;
    }

    private static bool TryGetReadableMemberType(TsType source, string name, out TsType type)
    {
        source = source is TsGenericType generic ? generic.Definition : source;

        switch (source)
        {
            case TsClassType cls:
                for (var current = cls; current != null; current = current.BaseType)
                {
                    if (current.Fields.TryGetValue(name, out var field) && !field.IsPrivateName)
                    {
                        type = field.Type;
                        return true;
                    }
                    if (current.Properties.TryGetValue(name, out var property) && !property.IsPrivateName)
                    {
                        type = property.Type;
                        return true;
                    }
                    if (current.Methods.TryGetValue(name, out var method) && !method.IsPrivateName)
                    {
                        type = new TsFunctionType(method.Parameters, method.ReturnType);
                        return true;
                    }
                }
                break;
            case TsInterfaceType iface:
                if (iface.Properties.TryGetValue(name, out var ifaceProperty))
                {
                    type = ifaceProperty.Type;
                    return true;
                }
                if (iface.Methods.TryGetValue(name, out var ifaceMethod))
                {
                    type = new TsFunctionType(ifaceMethod.Parameters, ifaceMethod.ReturnType);
                    return true;
                }
                foreach (var extended in iface.ExtendedInterfaces)
                {
                    if (TryGetReadableMemberType(extended, name, out type))
                        return true;
                }
                break;
            case TsArrayType array:
                if (name == "length")
                {
                    type = TsType.Int32;
                    return true;
                }
                if (int.TryParse(name, out _))
                {
                    type = array.ElementType;
                    return true;
                }
                break;
            case TsTupleType tuple:
                if (int.TryParse(name, out var index) && index >= 0 && index < tuple.ElementTypes.Count)
                {
                    type = tuple.ElementTypes[index];
                    return true;
                }
                if (name == "length")
                {
                    type = TsType.Int32;
                    return true;
                }
                break;
        }

        type = TsType.Void;
        return false;
    }

    private static List<TsType> GetPropertyKeys(TsType type)
    {
        type = type is TsGenericType generic ? generic.Definition : type;
        var keys = new List<TsType>();
        void AddName(string name) => keys.Add(new TsLiteralType(name, TsType.String));

        switch (type)
        {
            case TsClassType cls:
                for (var current = cls; current != null; current = current.BaseType)
                {
                    foreach (var field in current.Fields.Values.Where(f => !f.IsPrivateName))
                        AddName(field.Name);
                    foreach (var property in current.Properties.Values.Where(p => !p.IsPrivateName))
                        AddName(property.Name);
                    foreach (var method in current.Methods.Values.Where(m => !m.IsPrivateName))
                        AddName(method.Name);
                    foreach (var signature in current.IndexSignatures)
                        keys.Add(signature.KeyType);
                }
                break;
            case TsInterfaceType iface:
                foreach (var property in iface.Properties.Values)
                    AddName(property.Name);
                foreach (var method in iface.Methods.Values)
                    AddName(method.Name);
                foreach (var extended in iface.ExtendedInterfaces)
                    keys.AddRange(GetPropertyKeys(extended));
                break;
            case TsArrayType:
                keys.Add(TsType.Number);
                AddName("length");
                break;
            case TsTupleType tuple:
                for (int i = 0; i < tuple.ElementTypes.Count; i++)
                    AddName(i.ToString());
                AddName("length");
                break;
            case TsUnionType union:
                foreach (var member in union.Types)
                    keys.AddRange(GetPropertyKeys(member));
                break;
            case TsIntersectionType intersection:
                foreach (var member in intersection.Types)
                    keys.AddRange(GetPropertyKeys(member));
                break;
        }

        return DistinctTypes(keys);
    }

    private void PushTypeParameters(IEnumerable<GenericParameterSyntax> parameters)
    {
        var parameterList = parameters.ToList();
        var scope = new Dictionary<string, TsTypeParameter>(StringComparer.Ordinal);
        foreach (var p in parameterList)
            scope[p.Name] = new TsTypeParameter(p.Name);
        _typeParameterScopes.Add(scope);

        foreach (var p in parameterList)
        {
            var parameter = scope[p.Name];
            if (p.Constraints.Count > 0)
                parameter.Constraint = ResolveType(p.Constraints[0]);
            if (p.DefaultType != null)
                parameter.DefaultType = ResolveType(p.DefaultType);
        }
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

    private Symbol? BindMapMember(TsMapType mapType, string name, SourceRange range)
    {
        switch (name)
        {
            case "size":
                return new PropertySymbol("size", TsType.Number, range) { IsReadonly = true };
            case "set":
            {
                var set = new MethodSymbol("set", mapType, range) { DeclaringClassName = "Map" };
                set.Parameters.Add(new ParameterSymbol("key", mapType.KeyType, range));
                set.Parameters.Add(new ParameterSymbol("value", mapType.ValueType, range));
                return set;
            }
            case "get":
            {
                var get = new MethodSymbol("get", new TsNullableType(mapType.ValueType), range) { DeclaringClassName = "Map" };
                get.Parameters.Add(new ParameterSymbol("key", mapType.KeyType, range));
                return get;
            }
            case "has":
            {
                var has = new MethodSymbol("has", TsType.Bool, range) { DeclaringClassName = "Map" };
                has.Parameters.Add(new ParameterSymbol("key", mapType.KeyType, range));
                return has;
            }
            case "delete":
            {
                var delete = new MethodSymbol("delete", TsType.Bool, range) { DeclaringClassName = "Map" };
                delete.Parameters.Add(new ParameterSymbol("key", mapType.KeyType, range));
                return delete;
            }
            case "clear":
                return new MethodSymbol("clear", TsType.Void, range) { DeclaringClassName = "Map" };
            case "values":
                return new MethodSymbol("values", new TsArrayType(mapType.ValueType), range) { DeclaringClassName = "Map" };
            case "forEach":
            {
                var forEach = new MethodSymbol("forEach", TsType.Void, range) { DeclaringClassName = "Map" };
                forEach.Parameters.Add(new ParameterSymbol("callback", TsType.Any, range));
                return forEach;
            }
            default:
                return null;
        }
    }

    private Symbol? BindSetMember(TsSetType setType, string name, SourceRange range)
    {
        switch (name)
        {
            case "size":
                return new PropertySymbol("size", TsType.Number, range) { IsReadonly = true };
            case "add":
            {
                var add = new MethodSymbol("add", setType, range) { DeclaringClassName = "Set" };
                add.Parameters.Add(new ParameterSymbol("value", setType.ElementType, range));
                return add;
            }
            case "has":
            {
                var has = new MethodSymbol("has", TsType.Bool, range) { DeclaringClassName = "Set" };
                has.Parameters.Add(new ParameterSymbol("value", setType.ElementType, range));
                return has;
            }
            case "delete":
            {
                var delete = new MethodSymbol("delete", TsType.Bool, range) { DeclaringClassName = "Set" };
                delete.Parameters.Add(new ParameterSymbol("value", setType.ElementType, range));
                return delete;
            }
            case "clear":
                return new MethodSymbol("clear", TsType.Void, range) { DeclaringClassName = "Set" };
            default:
                return null;
        }
    }

    private TsType ResolveNamedType(NamedTypeSyntax named)
    {
        if (named.Name is "null" or "undefined")
            return TsType.Null;
        if (named.Name == "unknown")
            return TsType.Unknown;
        if (named.Name == "never")
            return TsType.Never;
        if (named.Name == "symbol")
            return TsType.Symbol;

        for (int i = _typeValueScopes.Count - 1; i >= 0; i--)
        {
            if (_typeValueScopes[i].TryGetValue(named.Name, out var typeValue))
                return typeValue;
        }

        if (LookupTypeParameter(named.Name) is TsTypeParameter typeParam)
            return typeParam;

        if (named.Name == "Array")
        {
            return new TsArrayType(named.TypeArguments.Count > 0
                ? ResolveType(named.TypeArguments[0])
                : TsType.Any);
        }

        if (named.Name == "ReadonlyArray")
        {
            return new TsArrayType(
                named.TypeArguments.Count > 0 ? ResolveType(named.TypeArguments[0]) : TsType.Any,
                isReadonly: true);
        }

        if (named.Name == "Promise")
        {
            return new TsPromiseType(named.TypeArguments.Count > 0
                ? ResolveType(named.TypeArguments[0])
                : TsType.Any);
        }

        if (named.Name == "Map")
        {
            return new TsMapType(
                named.TypeArguments.Count > 0 ? ResolveType(named.TypeArguments[0]) : TsType.Any,
                named.TypeArguments.Count > 1 ? ResolveType(named.TypeArguments[1]) : TsType.Any);
        }

        if (named.Name == "Set")
        {
            return new TsSetType(
                named.TypeArguments.Count > 0 ? ResolveType(named.TypeArguments[0]) : TsType.Any);
        }

        if (named.Name is "Uint8Array" or "Uint8ClampedArray")
            return _classTypes.TryGetValue("Uint8Array", out var bytesType)
                ? bytesType
                : new TsClassType("Uint8Array");

        if (named.Name == "ArrayBuffer")
            return _classTypes.TryGetValue("ArrayBuffer", out var bufferType)
                ? bufferType
                : new TsClassType("ArrayBuffer");

        if (_typeAliasDeclarations.TryGetValue(named.Name, out var aliasDeclaration))
        {
            return ResolveTypeAliasReference(named, aliasDeclaration);
        }

        if (_typeAliases.TryGetValue(named.Name, out var aliased))
            return aliased;

        if (_symbolTable.Lookup(named.Name) is LocalSymbol { Type: TsClassType localClassType })
            return localClassType;

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

    private TsType ResolveTypeAliasReference(NamedTypeSyntax named, TypeAliasDeclarationSyntax alias)
    {
        if (alias.GenericParameters.Count > 0 && named.TypeArguments.Count > alias.GenericParameters.Count)
        {
            _diagnostics.Error(
                $"Type alias '{alias.Name}' expected {alias.GenericParameters.Count} type argument(s), got {named.TypeArguments.Count}",
                named.Range.Start);
            return TsType.Any;
        }

        if (alias.GenericParameters.Count == 0)
        {
            if (_typeAliases.TryGetValue(alias.Name, out var cached))
                return cached;

            if (!_resolvingTypeAliases.Add(alias.Name))
            {
                _diagnostics.Error($"Recursive type alias '{alias.Name}' cannot be resolved", named.Range.Start);
                return TsType.Any;
            }

            try
            {
                var resolved = ResolveType(alias.Type);
                _typeAliases[alias.Name] = resolved;
                return resolved;
            }
            finally
            {
                _resolvingTypeAliases.Remove(alias.Name);
            }
        }

        var mappedArguments = ResolveGenericTypeArguments(alias.Name, alias.GenericParameters, named.TypeArguments, named.Range.Start);

        _typeValueScopes.Add(mappedArguments);
        try
        {
            return ResolveType(alias.Type);
        }
        finally
        {
            _typeValueScopes.RemoveAt(_typeValueScopes.Count - 1);
        }
    }

    private Dictionary<string, TsType> ResolveGenericTypeArguments(
        string ownerName,
        IReadOnlyList<GenericParameterSyntax> parameters,
        IReadOnlyList<TypeSyntax> providedArguments,
        SourceLocation location)
    {
        var result = new Dictionary<string, TsType>(StringComparer.Ordinal);
        for (int i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            TsType argumentType;
            if (i < providedArguments.Count)
            {
                argumentType = ResolveType(providedArguments[i]);
            }
            else if (parameter.DefaultType != null)
            {
                _typeValueScopes.Add(new Dictionary<string, TsType>(result, StringComparer.Ordinal));
                try
                {
                    argumentType = ResolveType(parameter.DefaultType);
                }
                finally
                {
                    _typeValueScopes.RemoveAt(_typeValueScopes.Count - 1);
                }
            }
            else
            {
                _diagnostics.Error(
                    $"Type alias '{ownerName}' expected {parameters.Count} type argument(s), got {providedArguments.Count}",
                    location);
                argumentType = TsType.Any;
            }

            if (parameter.Constraints.Count > 0)
            {
                _typeValueScopes.Add(new Dictionary<string, TsType>(result, StringComparer.Ordinal));
                try
                {
                    var constraint = ResolveType(parameter.Constraints[0]);
                    if (!TsType.IsCompatibleWith(argumentType, constraint))
                    {
                        _diagnostics.Error(
                            $"Type argument '{argumentType}' does not satisfy constraint '{constraint}' for '{parameter.Name}'",
                            location);
                    }
                }
                finally
                {
                    _typeValueScopes.RemoveAt(_typeValueScopes.Count - 1);
                }
            }

            result[parameter.Name] = argumentType;
        }

        return result;
    }

    // Type inference helpers
    private static TsType InferBinaryResultType(TsType left, TsType right, TokenKind op)
    {
        if (op == TokenKind.QuestionQuestion)
        {
            if (left is TsNullableType nullableLeft)
            {
                if (right is TsNullType or TsPrimitiveType { Name: "void" })
                {
                    return left;
                }

                if (nullableLeft.ElementType.IsNumeric && right.IsNumeric)
                {
                    return WiderNumeric(nullableLeft.ElementType, right);
                }

                return TsType.IsCompatibleWith(nullableLeft.ElementType, right)
                    ? nullableLeft.ElementType
                    : new TsUnionType(new List<TsType> { nullableLeft.ElementType, right });
            }

            if (left is TsNullType or TsAnyType)
            {
                return right;
            }

            return TsType.IsCompatibleWith(left, right) ? left : right;
        }

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

        if (op == TokenKind.Plus && (left == TsType.String || right == TsType.String))
            return TsType.String;

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

            TokenKind.StarStar => TsType.Number,
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
