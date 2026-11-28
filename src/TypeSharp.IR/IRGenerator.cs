using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;

namespace TypeSharp.IR;

public sealed partial class IRGenerator
{
    private FunctionIR? _currentFunction;
    private BasicBlock? _currentBlock;
    private int _tempCounter;
    private Dictionary<string, int> _localMap = new();

    // Slots that hold a closure box (heap cell with field "v") rather than the
    // value itself: captured variables and locals captured by inner functions.
    private Dictionary<string, int> _boxSlots = new();

    // Lambdas and nested function declarations are lifted: queued here while
    // the enclosing function generates, then emitted as siblings afterwards.
    private readonly Queue<BoundFunctionDeclaration> _liftedFunctions = new();
    private readonly Queue<BoundClassDeclaration> _liftedClasses = new();
    private readonly HashSet<string> _generatedFunctions = new();
    private readonly HashSet<string> _generatedClasses = new();

    // funcName -> captured variable names; direct calls to capturing functions
    // must build a closure carrying the current boxes.
    private readonly Dictionary<string, List<string>> _capturesByFunction = new();
    private BoundFunctionDeclaration? _currentDeclaration;
    private readonly Dictionary<Symbol, BoundNode> _moduleConstantInitializers = new();
    private readonly Stack<LoopTargets> _loopTargets = new();
    // `break` applies to both loops and switches, while `continue` applies to
    // loops only.  Keep separate stacks so a switch nested in a loop preserves
    // the enclosing loop's continue target.
    private readonly Stack<int> _breakTargets = new();
    // Labels are installed by the loop/switch generator after it creates its
    // concrete blocks. A label is visible only while its statement is emitted.
    private readonly Dictionary<string, (int BreakTarget, int? ContinueTarget)> _labelTargets = new();
    private readonly List<string> _pendingLabels = new();

    private readonly record struct LoopTargets(int BreakBlockId, int ContinueBlockId);

    public ModuleIR Generate(BoundSourceFile sourceFile)
    {
        var module = new ModuleIR(sourceFile.FileName);
        _moduleConstantInitializers.Clear();

        CollectCaptureInfo(sourceFile);
        CollectModuleConstants(sourceFile);

        var moduleInit = GenerateModuleInitializer(sourceFile);
        if (moduleInit != null)
        {
            module.AddFunction(moduleInit);
            DrainLiftedFunctions(module);
        }

        foreach (var member in sourceFile.Members)
        {
            if (member is BoundFunctionDeclaration func)
            {
                var funcIR = GenerateFunction(func);
                _generatedFunctions.Add(funcIR.Name);
                module.AddFunction(funcIR);
                DrainLiftedFunctions(module);
            }
            else if (member is BoundClassDeclaration cls)
            {
                if (_generatedClasses.Add(cls.Symbol.Name))
                    GenerateClass(module, cls);
                DrainLiftedFunctions(module);
            }
        }

        return module;
    }

    private FunctionIR? GenerateModuleInitializer(BoundSourceFile sourceFile)
    {
        var executableMembers = sourceFile.Members
            .Where(member => member is BoundVariableDeclaration or BoundExpressionStatement ||
                             member is BoundClassDeclaration cls &&
                             (cls.Decorators.Count > 0 || cls.Members.Any(m =>
                                 m is BoundStaticBlock ||
                                 m is BoundMethodDeclaration { Decorators.Count: > 0 } ||
                                 m is BoundAccessorDeclaration { Decorators.Count: > 0 } ||
                                 m is BoundFieldInitializer { Decorators.Count: > 0 } ||
                                 m is BoundFieldInitializer { Field.IsStatic: true, Initializer: not null })))
            .ToList();
        if (executableMembers.Count == 0)
            return null;

        var previousFunction = _currentFunction;
        var previousDeclaration = _currentDeclaration;
        var previousBlock = _currentBlock;
        var previousLocalMap = _localMap;
        var previousBoxSlots = _boxSlots;
        var previousTempCounter = _tempCounter;

        var function = new FunctionIR(GetModuleInitializerName(sourceFile.FileName), TsType.Void, new List<ParameterInfo>())
        {
            LocalCount = 0
        };

        _currentFunction = function;
        _currentDeclaration = null;
        _tempCounter = 0;
        _localMap = new Dictionary<string, int>();
        _boxSlots = new Dictionary<string, int>();
        _currentBlock = function.CreateBlock();

        foreach (var member in executableMembers)
            GenerateStatement(member);

        function.LocalCount = _tempCounter;
        if (!_currentBlock.EndsInBranch)
            EmitReturnVoid();

        _currentFunction = previousFunction;
        _currentDeclaration = previousDeclaration;
        _currentBlock = previousBlock;
        _localMap = previousLocalMap;
        _boxSlots = previousBoxSlots;
        _tempCounter = previousTempCounter;

        return function;
    }

    private void CollectModuleConstants(BoundSourceFile sourceFile)
    {
        foreach (var declaration in sourceFile.Members.OfType<BoundVariableDeclaration>())
        {
            if (declaration.Symbol.IsConst
                && declaration.Initializer != null
                && IsCompileTimeConstant(declaration.Initializer))
            {
                _moduleConstantInitializers[declaration.Symbol] = declaration.Initializer;
            }
        }
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
            _ => false
        };
    }

    private void CollectCaptureInfo(BoundNode node)
    {
        switch (node)
        {
            case BoundSourceFile file:
                foreach (var member in file.Members) CollectCaptureInfo(member);
                break;
            case BoundFunctionDeclaration func:
                if (func.CapturedVariables.Count > 0)
                    _capturesByFunction[func.Symbol.Name] = func.CapturedVariables.Select(s => s.Name).ToList();
                foreach (var parameter in func.Symbol.Parameters)
                    if (parameter.DefaultExpression != null)
                        CollectCaptureInfo(parameter.DefaultExpression);
                CollectCaptureInfo(func.Body);
                break;
            case BoundClassDeclaration cls:
                foreach (var decorator in cls.Decorators) CollectCaptureInfo(decorator);
                foreach (var member in cls.Members) CollectCaptureInfo(member);
                break;
            case BoundMethodDeclaration method:
                foreach (var decorator in method.Decorators) CollectCaptureInfo(decorator);
                foreach (var parameter in method.Symbol.Parameters)
                    if (parameter.DefaultExpression != null)
                        CollectCaptureInfo(parameter.DefaultExpression);
                CollectCaptureInfo(method.Body);
                break;
            case BoundAccessorDeclaration accessor:
                foreach (var decorator in accessor.Decorators) CollectCaptureInfo(decorator);
                if (accessor.Parameter?.DefaultExpression is { } accessorDefault)
                    CollectCaptureInfo(accessorDefault);
                CollectCaptureInfo(accessor.Body);
                break;
            case BoundFieldInitializer field:
                foreach (var decorator in field.Decorators) CollectCaptureInfo(decorator);
                if (field.Initializer != null) CollectCaptureInfo(field.Initializer);
                break;
            case BoundStaticBlock staticBlock:
                CollectCaptureInfo(staticBlock.Body);
                break;
            case BoundConstructorDeclaration ctor:
                foreach (var parameter in ctor.Symbol.Parameters)
                    if (parameter.DefaultExpression != null)
                        CollectCaptureInfo(parameter.DefaultExpression);
                CollectCaptureInfo(ctor.Body);
                break;
            case BoundBlockStatement block:
                foreach (var stmt in block.Statements) CollectCaptureInfo(stmt);
                break;
            case BoundVariableDeclaration { Initializer: not null } varDecl:
                CollectCaptureInfo(varDecl.Initializer);
                break;
            case BoundExpressionStatement stmt:
                CollectCaptureInfo(stmt.Expression);
                break;
            case BoundReturnStatement { Value: not null } ret:
                CollectCaptureInfo(ret.Value);
                break;
            case BoundIfStatement ifStmt:
                CollectCaptureInfo(ifStmt.Condition);
                CollectCaptureInfo(ifStmt.ThenBranch);
                if (ifStmt.ElseBranch != null) CollectCaptureInfo(ifStmt.ElseBranch);
                break;
            case BoundSwitchStatement switchStmt:
                CollectCaptureInfo(switchStmt.Expression);
                foreach (var clause in switchStmt.Clauses)
                {
                    if (clause.Test != null) CollectCaptureInfo(clause.Test);
                    foreach (var statement in clause.Statements) CollectCaptureInfo(statement);
                }
                break;
            case BoundWhileStatement whileStmt:
                CollectCaptureInfo(whileStmt.Condition);
                CollectCaptureInfo(whileStmt.Body);
                break;
            case BoundDoWhileStatement doWhileStmt:
                CollectCaptureInfo(doWhileStmt.Body);
                CollectCaptureInfo(doWhileStmt.Condition);
                break;
            case BoundForStatement forStmt:
                if (forStmt.Initializer != null) CollectCaptureInfo(forStmt.Initializer);
                if (forStmt.Condition != null) CollectCaptureInfo(forStmt.Condition);
                if (forStmt.Iterator != null) CollectCaptureInfo(forStmt.Iterator);
                CollectCaptureInfo(forStmt.Body);
                break;
            case BoundBreakStatement:
            case BoundContinueStatement:
                break;
            case BoundTryStatement tryStmt:
                CollectCaptureInfo(tryStmt.TryBlock);
                if (tryStmt.CatchBlock != null) CollectCaptureInfo(tryStmt.CatchBlock);
                if (tryStmt.FinallyBlock != null) CollectCaptureInfo(tryStmt.FinallyBlock);
                break;
            case BoundThrowStatement throwStmt:
                CollectCaptureInfo(throwStmt.Expression);
                break;
            case BoundLambdaExpression lambda:
                CollectCaptureInfo(lambda.Function);
                break;
            case BoundBinaryExpression bin:
                CollectCaptureInfo(bin.Left);
                CollectCaptureInfo(bin.Right);
                break;
            case BoundUnaryExpression unary:
                CollectCaptureInfo(unary.Operand);
                break;
            case BoundCallExpression call:
                CollectCaptureInfo(call.Callee);
                foreach (var arg in call.Arguments) CollectCaptureInfo(arg);
                break;
            case BoundAssignmentExpression assign:
                CollectCaptureInfo(assign.Target);
                CollectCaptureInfo(assign.Value);
                break;
            case BoundConditionalExpression cond:
                CollectCaptureInfo(cond.Condition);
                CollectCaptureInfo(cond.WhenTrue);
                CollectCaptureInfo(cond.WhenFalse);
                break;
            case BoundMemberAccessExpression member:
                CollectCaptureInfo(member.Object);
                break;
            case BoundIndexExpression index:
                CollectCaptureInfo(index.Object);
                CollectCaptureInfo(index.Index);
                break;
            case BoundNormalizeObjectRestKeyExpression normalize:
                CollectCaptureInfo(normalize.Source);
                CollectCaptureInfo(normalize.RawKey);
                break;
            case BoundLoadObjectRestKeyExpression scopedRead:
                CollectCaptureInfo(scopedRead.Source);
                CollectCaptureInfo(scopedRead.RawKey);
                CollectCaptureInfo(scopedRead.NormalizedKey);
                break;
            case BoundObjectRestExpression { ScopedExcludedKeys: { } scoped } rest:
                CollectCaptureInfo(rest.Source);
                foreach (var key in scoped) CollectCaptureInfo(key);
                break;
            case BoundDestructuringAssignmentExpression assignment when CanEmitScopedDestructuring(assignment):
                foreach (var temp in assignment.Temporaries) CollectCaptureInfo(temp);
                foreach (var step in assignment.ScopedSteps!) CollectCaptureInfo(step);
                CollectCaptureInfo(assignment.Result);
                break;
            case BoundArrayLiteralExpression arr:
                foreach (var element in arr.Elements) CollectCaptureInfo(element);
                break;
            case BoundArrayConstructionExpression arrCtor:
                foreach (var arg in arrCtor.Arguments) CollectCaptureInfo(arg);
                break;
            case BoundObjectLiteralExpression obj:
                foreach (var prop in obj.Properties) CollectCaptureInfo(prop.Value);
                break;
            case BoundCastExpression cast:
                CollectCaptureInfo(cast.Operand);
                break;
            case BoundTypeofExpression typeofExpr:
                CollectCaptureInfo(typeofExpr.Operand);
                break;
            case BoundVoidExpression voidExpr:
                CollectCaptureInfo(voidExpr.Operand);
                break;
            case BoundDeleteFieldExpression deleteField:
                CollectCaptureInfo(deleteField.Object);
                break;
            case BoundDeleteIndexExpression deleteIndex:
                CollectCaptureInfo(deleteIndex.Object);
                CollectCaptureInfo(deleteIndex.Index);
                break;
            case BoundDeleteNonReferenceExpression deleteNonRef:
                CollectCaptureInfo(deleteNonRef.Operand);
                break;
            case BoundNewExpression newExpr:
                foreach (var arg in newExpr.Arguments) CollectCaptureInfo(arg);
                break;
            case BoundAwaitExpression awaitExpr:
                CollectCaptureInfo(awaitExpr.Expression);
                break;
        }
    }

    private void DrainLiftedFunctions(ModuleIR module)
    {
        while (_liftedFunctions.Count > 0 || _liftedClasses.Count > 0)
        {
            while (_liftedFunctions.Count > 0)
            {
                var pending = _liftedFunctions.Dequeue();
                if (!_generatedFunctions.Add(pending.Symbol.Name))
                    continue;
                module.AddFunction(GenerateFunction(pending));
            }

            while (_liftedClasses.Count > 0)
            {
                var pendingClass = _liftedClasses.Dequeue();
                if (!_generatedClasses.Add(pendingClass.Symbol.Name))
                    continue;
                GenerateClass(module, pendingClass);
            }
        }
    }

    private static bool ContainsCapturedThis(BoundNode node)
    {
        switch (node)
        {
            case BoundFunctionDeclaration func:
                if (func.CapturedVariables.Any(symbol => symbol.Name == "this"))
                    return true;
                return ContainsCapturedThis(func.Body);
            case BoundLambdaExpression lambda:
                return ContainsCapturedThis(lambda.Function);
            case BoundBlockStatement block:
                return block.Statements.Any(ContainsCapturedThis);
            case BoundVariableDeclaration { Initializer: not null } varDecl:
                return ContainsCapturedThis(varDecl.Initializer);
            case BoundExpressionStatement stmt:
                return ContainsCapturedThis(stmt.Expression);
            case BoundReturnStatement { Value: not null } ret:
                return ContainsCapturedThis(ret.Value);
            case BoundSwitchStatement switchStmt:
                return ContainsCapturedThis(switchStmt.Expression)
                    || switchStmt.Clauses.Any(clause =>
                        (clause.Test != null && ContainsCapturedThis(clause.Test))
                        || clause.Statements.Any(ContainsCapturedThis));
            case BoundIfStatement ifStmt:
                return ContainsCapturedThis(ifStmt.Condition)
                    || ContainsCapturedThis(ifStmt.ThenBranch)
                    || (ifStmt.ElseBranch != null && ContainsCapturedThis(ifStmt.ElseBranch));
            case BoundWhileStatement whileStmt:
                return ContainsCapturedThis(whileStmt.Condition) || ContainsCapturedThis(whileStmt.Body);
            case BoundDoWhileStatement doWhileStmt:
                return ContainsCapturedThis(doWhileStmt.Body) || ContainsCapturedThis(doWhileStmt.Condition);
            case BoundForStatement forStmt:
                return (forStmt.Initializer != null && ContainsCapturedThis(forStmt.Initializer))
                    || (forStmt.Condition != null && ContainsCapturedThis(forStmt.Condition))
                    || (forStmt.Iterator != null && ContainsCapturedThis(forStmt.Iterator))
                    || ContainsCapturedThis(forStmt.Body);
            case BoundTryStatement tryStmt:
                return ContainsCapturedThis(tryStmt.TryBlock)
                    || (tryStmt.CatchBlock != null && ContainsCapturedThis(tryStmt.CatchBlock))
                    || (tryStmt.FinallyBlock != null && ContainsCapturedThis(tryStmt.FinallyBlock));
            case BoundThrowStatement throwStmt:
                return ContainsCapturedThis(throwStmt.Expression);
            case BoundBinaryExpression bin:
                return ContainsCapturedThis(bin.Left) || ContainsCapturedThis(bin.Right);
            case BoundUnaryExpression unary:
                return ContainsCapturedThis(unary.Operand);
            case BoundCallExpression call:
                return ContainsCapturedThis(call.Callee) || call.Arguments.Any(ContainsCapturedThis);
            case BoundAssignmentExpression assign:
                return ContainsCapturedThis(assign.Target) || ContainsCapturedThis(assign.Value);
            case BoundConditionalExpression cond:
                return ContainsCapturedThis(cond.Condition)
                    || ContainsCapturedThis(cond.WhenTrue)
                    || ContainsCapturedThis(cond.WhenFalse);
            case BoundMemberAccessExpression member:
                return ContainsCapturedThis(member.Object);
            case BoundIndexExpression index:
                return ContainsCapturedThis(index.Object) || ContainsCapturedThis(index.Index);
            case BoundNormalizeObjectRestKeyExpression normalize:
                return ContainsCapturedThis(normalize.Source) || ContainsCapturedThis(normalize.RawKey);
            case BoundLoadObjectRestKeyExpression scopedRead:
                return ContainsCapturedThis(scopedRead.Source) || ContainsCapturedThis(scopedRead.RawKey)
                    || ContainsCapturedThis(scopedRead.NormalizedKey);
            case BoundObjectRestExpression { ScopedExcludedKeys: { } scoped } rest:
                return ContainsCapturedThis(rest.Source) || scoped.Any(ContainsCapturedThis);
            case BoundDestructuringAssignmentExpression assignment when CanEmitScopedDestructuring(assignment):
                return assignment.Temporaries.Any(ContainsCapturedThis) || assignment.ScopedSteps!.Any(ContainsCapturedThis)
                    || ContainsCapturedThis(assignment.Result);
            case BoundArrayLiteralExpression arr:
                return arr.Elements.Any(ContainsCapturedThis);
            case BoundArrayConstructionExpression arrCtor:
                return arrCtor.Arguments.Any(ContainsCapturedThis);
            case BoundObjectLiteralExpression obj:
                return obj.Properties.Any(property => ContainsCapturedThis(property.Value));
            case BoundCastExpression cast:
                return ContainsCapturedThis(cast.Operand);
            case BoundTypeofExpression typeofExpr:
                return ContainsCapturedThis(typeofExpr.Operand);
            case BoundVoidExpression voidExpr:
                return ContainsCapturedThis(voidExpr.Operand);
            case BoundNewExpression newExpr:
                return newExpr.Arguments.Any(ContainsCapturedThis);
            case BoundAwaitExpression awaitExpr:
                return ContainsCapturedThis(awaitExpr.Expression);
            default:
                return false;
        }
    }

    private FunctionIR GenerateFunction(BoundFunctionDeclaration func)
    {
        var parameters = func.Symbol.Parameters
            .Select(p => new ParameterInfo(p.Name, p.Type, p.IsRest))
            .ToList();

        var funcIR = new FunctionIR(func.Symbol.Name, func.Symbol.Type, parameters)
        {
            IsAsync = func.Symbol.IsAsync,
            IsGenerator = func.Symbol.IsGenerator,
            UsesGeneratorRegions = func.Symbol.IsGenerator && !func.Symbol.IsAsync && RequiresGeneratorRegions(func.Body),
            LocalCount = 0
        };
        funcIR.CapturedVariables.AddRange(func.CapturedVariables.Select(s => s.Name));

        _currentFunction = funcIR;
        _currentDeclaration = func;
        _tempCounter = 0;
        _localMap = new Dictionary<string, int>();
        _boxSlots = new Dictionary<string, int>();

        var entryBlock = funcIR.CreateBlock();
        _currentBlock = entryBlock;

        foreach (var param in func.Symbol.Parameters)
        {
            int idx = func.Symbol.Parameters.IndexOf(param);
            _localMap[param.Name] = idx;
        }
        _tempCounter = func.Symbol.Parameters.Count;

        // Slots for captured boxes follow the parameters; the VM installs them
        // from the closure environment on invocation.
        foreach (var captured in func.CapturedVariables)
        {
            int slot = _tempCounter++;
            _localMap[captured.Name] = slot;
            _boxSlots[captured.Name] = slot;
        }

        EmitOwnedReceiverBox(func.Receiver);
        GenerateDefaultParameterInitializers(func.Symbol.Parameters, argumentOffset: 0);

        // Parameters that inner functions capture are re-homed into boxes.
        foreach (var param in func.Symbol.Parameters)
        {
            if (!param.IsCaptured)
                continue;
            int paramIdx = func.Symbol.Parameters.IndexOf(param);
            int boxSlot = _tempCounter++;
            _currentBlock.Instructions.Add(new Instruction(Opcode.NewObject, 0, 0, "$Box"));
            _currentBlock.Instructions.Add(new Instruction(Opcode.Dup));
            EmitLoadArg(paramIdx);
            _currentBlock.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, "v"));
            EmitStoreLocal(boxSlot);
            _localMap[param.Name] = boxSlot;
            _boxSlots[param.Name] = boxSlot;
        }

        GenerateStatement(func.Body);

        funcIR.LocalCount = _tempCounter;

        if (!_currentBlock.EndsInBranch)
        {
            EmitReturnVoid();
        }

        if (funcIR.UsesGeneratorRegions)
            FinishGeneratorRegions(funcIR);
        return funcIR;
    }

    private static bool IsBoxedSymbol(Symbol symbol) => symbol switch
    {
        LocalSymbol { IsCaptured: true } => true,
        ParameterSymbol { IsCaptured: true } => true,
        _ => false
    };

    private int GetBoxSlot(string name)
    {
        if (_boxSlots.TryGetValue(name, out int slot))
            return slot;
        throw new InvalidOperationException(
            $"Captured variable '{name}' has no box in function '{_currentFunction?.Name}'");
    }

    private void EmitClosureCreation(string functionName, IReadOnlyList<string> capturedNames)
    {
        foreach (var name in capturedNames)
            EmitLoadLocal(GetBoxSlot(name));
        _currentBlock!.Instructions.Add(new Instruction(Opcode.MakeClosure, 0, capturedNames.Count, functionName));
    }

    private FunctionIR GenerateMethodFunction(string className, string methodName, TsType returnType,
        List<ParameterSymbol> explicitParams, BoundNode body, BoundReceiver? receiver, bool isAsync = false)
    {
        var parameters = new List<ParameterInfo>();
        parameters.Add(new ParameterInfo("this", new TsClassType(className)));
        foreach (var p in explicitParams)
            parameters.Add(new ParameterInfo(p.Name, p.Type, p.IsRest));

        var qualifiedName = $"{className}::{methodName}";
        var funcIR = new FunctionIR(qualifiedName, returnType, parameters)
        {
            IsAsync = isAsync,
            IsGenerator = false,
            LocalCount = 0
        };

        _currentFunction = funcIR;
        _tempCounter = 0;
        _localMap = new Dictionary<string, int>();
        _boxSlots = new Dictionary<string, int>();

        var entryBlock = funcIR.CreateBlock();
        _currentBlock = entryBlock;

        _localMap["this"] = 0;
        for (int i = 0; i < explicitParams.Count; i++)
            _localMap[explicitParams[i].Name] = i + 1;
        _tempCounter = 1 + explicitParams.Count;

        // Only legacy manually-built bound trees lack the explicit owner.
        // Their original slot-0 capture convention remains supported.
        if (receiver == null && ContainsCapturedThis(body))
        {
            int boxSlot = _tempCounter++;
            _currentBlock.Instructions.Add(new Instruction(Opcode.NewObject, 0, 0, "$Box"));
            _currentBlock.Instructions.Add(new Instruction(Opcode.Dup));
            EmitLoadArg(0);
            _currentBlock.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, "v"));
            EmitStoreLocal(boxSlot);
            _localMap["this"] = boxSlot;
            _boxSlots["this"] = boxSlot;
        }

        EmitOwnedReceiverBox(receiver);
        GenerateDefaultParameterInitializers(explicitParams, argumentOffset: 1);

        foreach (var param in explicitParams)
        {
            if (!param.IsCaptured)
                continue;
            int paramIdx = explicitParams.IndexOf(param) + 1;
            int boxSlot = _tempCounter++;
            _currentBlock.Instructions.Add(new Instruction(Opcode.NewObject, 0, 0, "$Box"));
            _currentBlock.Instructions.Add(new Instruction(Opcode.Dup));
            EmitLoadArg(paramIdx);
            _currentBlock.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, "v"));
            EmitStoreLocal(boxSlot);
            _localMap[param.Name] = boxSlot;
            _boxSlots[param.Name] = boxSlot;
        }

        GenerateStatement(body);

        funcIR.LocalCount = _tempCounter;

        if (!_currentBlock.EndsInBranch)
        {
            EmitReturnVoid();
        }

        return funcIR;
    }

    private void GenerateClass(ModuleIR module, BoundClassDeclaration cls)
    {
        foreach (var member in cls.Members)
        {
            switch (member)
            {
                case BoundConstructorDeclaration ctor:
                {
                    var funcIR = GenerateMethodFunction(
                        cls.Symbol.Name, ".ctor", TsType.Void,
                        ctor.Symbol.Parameters, ctor.Body, ctor.Receiver, ctor.Symbol.IsAsync);
                    module.AddFunction(funcIR);
                    break;
                }
                case BoundMethodDeclaration method:
                {
                    var funcIR = GenerateMethodFunction(
                        cls.Symbol.Name, method.Symbol.RuntimeName ?? method.Symbol.Name, method.Symbol.Type,
                        method.Symbol.Parameters, method.Body, method.Receiver, method.Symbol.IsAsync);
                    module.AddFunction(funcIR);
                    break;
                }
                case BoundAccessorDeclaration accessor:
                {
                    var funcIR = GenerateAccessorFunction(accessor);
                    module.AddFunction(funcIR);
                    break;
                }
                case BoundStaticBlock staticBlock:
                {
                    var funcIR = GenerateStaticBlockFunction(staticBlock);
                    module.AddFunction(funcIR);
                    break;
                }
                case BoundFieldInitializer:
                    break;
            }
        }
    }

    private FunctionIR GenerateAccessorFunction(BoundAccessorDeclaration accessor)
    {
        var parameters = new List<ParameterInfo>
        {
            new("this", new TsClassType(accessor.ClassName))
        };
        if (!accessor.IsGetter && accessor.Parameter != null)
            parameters.Add(new ParameterInfo(accessor.Parameter.Name, accessor.Parameter.Type, accessor.Parameter.IsRest));

        var functionName = accessor.IsGetter
            ? accessor.Symbol.GetterName ?? $"{accessor.ClassName}::get:{accessor.Symbol.Name}"
            : accessor.Symbol.SetterName ?? $"{accessor.ClassName}::set:{accessor.Symbol.Name}";
        var funcIR = new FunctionIR(functionName, accessor.IsGetter ? accessor.Symbol.Type : TsType.Void, parameters)
        {
            LocalCount = 0
        };

        var previousFunction = _currentFunction;
        var previousBlock = _currentBlock;
        var previousLocalMap = _localMap;
        var previousBoxSlots = _boxSlots;
        var previousTempCounter = _tempCounter;

        _currentFunction = funcIR;
        _tempCounter = 0;
        _localMap = new Dictionary<string, int> { ["this"] = 0 };
        if (!accessor.IsGetter && accessor.Parameter != null)
            _localMap[accessor.Parameter.Name] = 1;
        _boxSlots = new Dictionary<string, int>();
        _tempCounter = parameters.Count;
        _currentBlock = funcIR.CreateBlock();

        EmitOwnedReceiverBox(accessor.Receiver);
        if (accessor.Parameter != null)
            GenerateDefaultParameterInitializers([accessor.Parameter], argumentOffset: 1);
        GenerateStatement(accessor.Body);
        funcIR.LocalCount = _tempCounter;
        if (!_currentBlock.EndsInBranch)
            EmitReturnVoid();

        _currentFunction = previousFunction;
        _currentBlock = previousBlock;
        _localMap = previousLocalMap;
        _boxSlots = previousBoxSlots;
        _tempCounter = previousTempCounter;

        return funcIR;
    }

    private FunctionIR GenerateStaticBlockFunction(BoundStaticBlock staticBlock)
    {
        var functionName = $"{staticBlock.ClassName}::static:{staticBlock.Ordinal}";
        var funcIR = new FunctionIR(functionName, TsType.Void, new List<ParameterInfo>())
        {
            LocalCount = 0
        };

        var previousFunction = _currentFunction;
        var previousBlock = _currentBlock;
        var previousLocalMap = _localMap;
        var previousBoxSlots = _boxSlots;
        var previousTempCounter = _tempCounter;

        _currentFunction = funcIR;
        _tempCounter = 0;
        _localMap = new Dictionary<string, int>();
        _boxSlots = new Dictionary<string, int>();
        _currentBlock = funcIR.CreateBlock();

        GenerateStatement(staticBlock.Body);
        funcIR.LocalCount = _tempCounter;
        if (!_currentBlock.EndsInBranch)
            EmitReturnVoid();

        _currentFunction = previousFunction;
        _currentBlock = previousBlock;
        _localMap = previousLocalMap;
        _boxSlots = previousBoxSlots;
        _tempCounter = previousTempCounter;

        return funcIR;
    }

    private void GenerateClassRuntimeInitializer(BoundClassDeclaration cls)
    {
        // Z-D8: inicializadores estáticos intercalados con bloques `static` por
        // posición textual (una sola pasada sobre cls.Members, en orden de
        // declaración). La clave global es la misma que construye BindMemberAccess
        // para campos estáticos ($static:Clase:campo, Binder.cs:3757-3760).
        foreach (var member in cls.Members)
        {
            switch (member)
            {
                case BoundStaticBlock staticBlock:
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.Call, 0, 0,
                        $"{staticBlock.ClassName}::static:{staticBlock.Ordinal}"));
                    break;
                case BoundFieldInitializer field when field.Field.IsStatic && field.Initializer != null:
                    GenerateExpression(field.Initializer);
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreGlobal, 0, 0,
                        $"$static:{cls.Symbol.Name}:{field.Field.Name}"));
                    break;
            }
        }

        foreach (var member in cls.Members)
        {
            switch (member)
            {
                case BoundMethodDeclaration method:
                    foreach (var decorator in method.Decorators)
                        GenerateDecoratorCall(
                            decorator,
                            TsValueTargetName($"{method.ClassName}::{method.Symbol.RuntimeName ?? method.Symbol.Name}"),
                            "method",
                            method.Symbol.Name,
                            method.Symbol.IsStatic,
                            method.Symbol.IsPrivateName);
                    break;
                case BoundAccessorDeclaration accessor:
                    foreach (var decorator in accessor.Decorators)
                        GenerateDecoratorCall(
                            decorator,
                            TsValueTargetName(accessor.IsGetter
                                ? accessor.Symbol.GetterName ?? $"{accessor.ClassName}::get:{accessor.Symbol.Name}"
                                : accessor.Symbol.SetterName ?? $"{accessor.ClassName}::set:{accessor.Symbol.Name}"),
                            accessor.IsGetter ? "getter" : "setter",
                            accessor.Symbol.Name,
                            accessor.Symbol.IsStatic,
                            accessor.Symbol.IsPrivateName);
                    break;
                case BoundFieldInitializer field:
                    foreach (var decorator in field.Decorators)
                        GenerateDecoratorCall(
                            decorator,
                            field.Field.RuntimeName ?? field.Field.Name,
                            "field",
                            field.Field.Name,
                            field.Field.IsStatic,
                            field.Field.IsPrivateName);
                    break;
            }
        }

        foreach (var decorator in cls.Decorators)
        {
            GenerateDecoratorCall(decorator, cls.Symbol.Name, "class", cls.Symbol.Name, isStatic: false, isPrivate: false);
        }
    }

    private static string TsValueTargetName(string name) => name;

    private void GenerateDecoratorCall(BoundNode decorator, string targetName, string kind, string name, bool isStatic, bool isPrivate)
    {
        GenerateExpression(decorator);
        _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_String, 0, 0, targetName));
        GenerateDecoratorContext(kind, name, isStatic, isPrivate);
        _currentBlock.Instructions.Add(new Instruction(Opcode.CallDynamic, 2));
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
    }

    private void GenerateDecoratorContext(string kind, string name, bool isStatic, bool isPrivate)
    {
        _currentBlock!.Instructions.Add(new Instruction(Opcode.NewObject, 0, 0, "Object"));

        _currentBlock.Instructions.Add(new Instruction(Opcode.Dup));
        _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_String, 0, 0, kind));
        _currentBlock.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, "kind"));

        _currentBlock.Instructions.Add(new Instruction(Opcode.Dup));
        _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_String, 0, 0, name));
        _currentBlock.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, "name"));

        _currentBlock.Instructions.Add(new Instruction(Opcode.Dup));
        _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_Bool, isStatic ? 1 : 0));
        _currentBlock.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, "static"));

        _currentBlock.Instructions.Add(new Instruction(Opcode.Dup));
        _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_Bool, isPrivate ? 1 : 0));
        _currentBlock.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, "private"));
    }

    private void GenerateStatement(BoundNode node)
    {
        if (_currentFunction!.UsesGeneratorRegions && _currentBlock!.EndsInBranch)
            _currentBlock = _currentFunction.CreateBlock(); // kept only if an explicit edge reaches it
        switch (node)
        {
            case BoundBlockStatement block:
                foreach (var stmt in block.Statements)
                    GenerateStatement(stmt);
                break;
            case BoundClassDeclaration cls:
                // AG: la declaración de clase local emite su inicializador de
                // ejecución en el punto de la declaración (estáticos y bloques
                // `static`, como a nivel superior) y se encola para que
                // `DrainLiftedFunctions` emita sus funciones (.ctor, métodos).
                _liftedClasses.Enqueue(cls);
                GenerateClassRuntimeInitializer(cls);
                break;

            case BoundVariableDeclaration varDecl:
                GenerateVariableDeclaration(varDecl);
                break;

            case BoundExpressionStatement exprStmt:
                GenerateExpression(exprStmt.Expression);
                // Every expression leaves a value on the stack; discard it so
                // statement-position expressions don't leak slots. (Ciclo 8:
                // las asignaciones también dejan su valor, neto +1.)
                _currentBlock!.Instructions.Add(new Instruction(Opcode.Pop));
                break;

            case BoundReturnStatement ret:
                GenerateReturn(ret);
                break;

            case BoundYieldStatement yield:
                GenerateYield(yield);
                break;

            case BoundIfStatement ifStmt:
                GenerateIf(ifStmt);
                break;

            case BoundSwitchStatement switchStmt:
                GenerateSwitch(switchStmt);
                break;

            case BoundWhileStatement whileStmt:
                GenerateWhile(whileStmt);
                break;

            case BoundDoWhileStatement doWhileStmt:
                GenerateDoWhile(doWhileStmt);
                break;

            case BoundForStatement forStmt:
                GenerateFor(forStmt);
                break;

            case BoundLabelledStatement labelled:
                GenerateLabelled(labelled);
                break;

            case BoundBreakStatement breakStmt:
                GenerateBreak(breakStmt.Label);
                break;

            case BoundContinueStatement continueStmt:
                GenerateContinue(continueStmt.Label);
                break;

            case BoundThrowStatement throwStmt:
                GenerateThrow(throwStmt);
                break;

            case BoundTryStatement tryStmt:
                GenerateTry(tryStmt);
                break;

            case BoundFunctionDeclaration func:
                // Local `const f = () => …` — lift to a sibling module function
                // instead of generating inline (which would corrupt state).
                _liftedFunctions.Enqueue(func);
                break;

            default:
                EmitNop();
                break;
        }
    }

    private void GenerateDefaultParameterInitializers(IReadOnlyList<ParameterSymbol> parameters, int argumentOffset)
    {
        for (int i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            if (!parameter.HasDefault || parameter.DefaultExpression == null)
                continue;

            var nextBlock = _currentFunction!.CreateBlock();
            EmitLoadArg(i + argumentOffset);
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
            _currentBlock.Instructions.Add(new Instruction(Opcode.CmpStrictEq_Any));
            EmitBranchFalse(nextBlock.Id);
            if (_currentFunction.UsesGeneratorRegions)
            {
                var defaultBlock = _currentFunction.CreateBlock();
                EmitBranch(defaultBlock.Id);
                _currentBlock = defaultBlock;
            }
            GenerateExpression(parameter.DefaultExpression);
            EmitStoreLocal(i + argumentOffset);
            if (_currentFunction.UsesGeneratorRegions)
                EmitBranch(nextBlock.Id);
            _currentBlock = nextBlock;
        }
    }

    private void GenerateYield(BoundYieldStatement yield)
    {
        if (yield.Value != null)
            GenerateExpression(yield.Value);
        else
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
        _currentBlock!.Instructions.Add(new Instruction(Opcode.Yield));
    }

    private void GenerateVariableDeclaration(BoundVariableDeclaration varDecl)
    {
        if (IsModuleGlobalSymbol(varDecl.Symbol))
        {
            if (varDecl.Initializer != null)
                GenerateExpression(varDecl.Initializer);
            else
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_Null));
            _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreGlobal, 0, 0, GetModuleGlobalKey(varDecl.Symbol)));
            return;
        }

        if (varDecl.Symbol.IsCaptured)
        {
            // Captured local: storage is a heap box shared with the closures.
            int slot = GetLocalIndex(varDecl.Symbol);
            _boxSlots[varDecl.Symbol.Name] = slot;
            _currentBlock!.Instructions.Add(new Instruction(Opcode.NewObject, 0, 0, "$Box"));
            _currentBlock.Instructions.Add(new Instruction(Opcode.Dup));
            if (varDecl.Initializer != null)
                GenerateExpression(varDecl.Initializer);
            else
                _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_Null));
            _currentBlock.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, "v"));
            EmitStoreLocal(slot);
            return;
        }

        if (varDecl.Initializer != null)
        {
            GenerateExpression(varDecl.Initializer);
            EmitStoreLocal(varDecl.Symbol);
        }
    }

    private void GenerateReturn(BoundReturnStatement ret)
    {
        if (ret.Value != null)
        {
            GenerateExpression(ret.Value);
            EmitReturn();
        }
        else
        {
            EmitReturnVoid();
        }
    }

    private void GenerateIf(BoundIfStatement ifStmt)
    {
        GenerateExpression(ifStmt.Condition);

        var thenBlock = _currentFunction!.CreateBlock();
        var elseBlock = ifStmt.ElseBranch != null ? _currentFunction.CreateBlock() : null;
        var continuationBlock = _currentFunction.CreateBlock();

        // Blocks can be created by enclosing control flow before these blocks.
        // Use explicit edges rather than relying on bytecode fallthrough order.
        EmitBranchTrue(thenBlock.Id);
        EmitBranch((elseBlock ?? continuationBlock).Id);

        _currentBlock = thenBlock;
        GenerateStatement(ifStmt.ThenBranch);
        if (!_currentBlock.EndsInBranch)
            EmitBranch(continuationBlock.Id);

        if (ifStmt.ElseBranch != null && elseBlock != null)
        {
            _currentBlock = elseBlock;
            GenerateStatement(ifStmt.ElseBranch);
            if (!_currentBlock.EndsInBranch)
                EmitBranch(continuationBlock.Id);
        }

        _currentBlock = continuationBlock;
    }

    private void GenerateSwitch(BoundSwitchStatement switchStmt)
    {
        var function = _currentFunction!;
        var afterBlock = function.CreateBlock();
        var clauseBlocks = switchStmt.Clauses.Select(_ => function.CreateBlock()).ToList();
        InstallPendingLabel(afterBlock.Id, null);

        // Evaluate the discriminant exactly once. Case expressions are then
        // evaluated in source order, which preserves their side effects.
        GenerateExpression(switchStmt.Expression);
        int discriminantSlot = _tempCounter++;
        EmitStoreLocal(discriminantSlot);

        int defaultClause = -1;
        for (int i = 0; i < switchStmt.Clauses.Count; i++)
        {
            if (switchStmt.Clauses[i].Test == null)
            {
                defaultClause = i;
                continue;
            }

            var nextComparison = function.CreateBlock();
            EmitLoadLocal(discriminantSlot);
            GenerateExpression(switchStmt.Clauses[i].Test!);
            _currentBlock!.Instructions.Add(new Instruction(Opcode.CmpStrictEq_Any));
            EmitBranchTrue(clauseBlocks[i].Id);
            EmitBranch(nextComparison.Id);
            _currentBlock = nextComparison;
        }

        EmitBranch(defaultClause >= 0 ? clauseBlocks[defaultClause].Id : afterBlock.Id);

        _breakTargets.Push(afterBlock.Id);
        try
        {
            for (int i = 0; i < switchStmt.Clauses.Count; i++)
            {
                _currentBlock = clauseBlocks[i];
                foreach (var statement in switchStmt.Clauses[i].Statements)
                    GenerateStatement(statement);

                // Cases fall through unless their body explicitly branches
                // (break, return, throw or continue in an enclosing loop).
                if (!_currentBlock.EndsInBranch)
                {
                    int next = i + 1 < clauseBlocks.Count ? clauseBlocks[i + 1].Id : afterBlock.Id;
                    EmitBranch(next);
                }
            }
        }
        finally
        {
            _breakTargets.Pop();
        }

        _currentBlock = afterBlock;
    }

    private void GenerateWhile(BoundWhileStatement whileStmt)
    {
        var func = _currentFunction!;
        var conditionBlock = func.CreateBlock();
        var bodyBlock = func.CreateBlock();
        var afterBlock = func.CreateBlock();
        InstallPendingLabel(afterBlock.Id, conditionBlock.Id);

        if (!_currentBlock!.EndsInBranch)
            EmitBranch(conditionBlock.Id);

        _currentBlock = conditionBlock;
        GenerateExpression(whileStmt.Condition);
        EmitBranchTrue(bodyBlock.Id);
        EmitBranch(afterBlock.Id);

        _currentBlock = bodyBlock;
        _breakTargets.Push(afterBlock.Id);
        _loopTargets.Push(new LoopTargets(afterBlock.Id, conditionBlock.Id));
        try
        {
            GenerateStatement(whileStmt.Body);
            if (!_currentBlock.EndsInBranch)
                EmitBranch(conditionBlock.Id);
        }
        finally
        {
            _loopTargets.Pop();
            _breakTargets.Pop();
        }

        _currentBlock = afterBlock;
    }

    private void GenerateDoWhile(BoundDoWhileStatement doWhileStmt)
    {
        var func = _currentFunction!;
        var bodyBlock = func.CreateBlock();
        var conditionBlock = func.CreateBlock();
        var afterBlock = func.CreateBlock();
        InstallPendingLabel(afterBlock.Id, conditionBlock.Id);

        if (!_currentBlock!.EndsInBranch)
            EmitBranch(bodyBlock.Id);

        _currentBlock = bodyBlock;
        _breakTargets.Push(afterBlock.Id);
        _loopTargets.Push(new LoopTargets(afterBlock.Id, conditionBlock.Id));
        try
        {
            GenerateStatement(doWhileStmt.Body);
            if (!_currentBlock.EndsInBranch)
                EmitBranch(conditionBlock.Id);
        }
        finally
        {
            _loopTargets.Pop();
            _breakTargets.Pop();
        }

        _currentBlock = conditionBlock;
        GenerateExpression(doWhileStmt.Condition);
        EmitBranchTrue(bodyBlock.Id);
        EmitBranch(afterBlock.Id);

        _currentBlock = afterBlock;
    }

    private void GenerateFor(BoundForStatement forStmt)
    {
        var func = _currentFunction!;
        var conditionBlock = func.CreateBlock();
        var bodyBlock = func.CreateBlock();
        var iteratorBlock = func.CreateBlock();
        var afterBlock = func.CreateBlock();
        InstallPendingLabel(afterBlock.Id, iteratorBlock.Id);

        if (forStmt.Initializer != null)
            GenerateStatement(forStmt.Initializer);

        if (!_currentBlock!.EndsInBranch)
            EmitBranch(conditionBlock.Id);

        _currentBlock = conditionBlock;
        if (forStmt.Condition != null)
        {
            GenerateExpression(forStmt.Condition);
            EmitBranchTrue(bodyBlock.Id);
            EmitBranch(afterBlock.Id);
        }
        else
        {
            // F: sin condición el bucle es infinito (solo `break`/`return` salen).
            EmitBranch(bodyBlock.Id);
        }

        _currentBlock = bodyBlock;
        _breakTargets.Push(afterBlock.Id);
        _loopTargets.Push(new LoopTargets(afterBlock.Id, iteratorBlock.Id));
        try
        {
            GenerateStatement(forStmt.Body);
            if (!_currentBlock.EndsInBranch)
                EmitBranch(iteratorBlock.Id);
        }
        finally
        {
            _loopTargets.Pop();
            _breakTargets.Pop();
        }

        _currentBlock = iteratorBlock;
        if (forStmt.Iterator != null)
        {
            GenerateExpression(forStmt.Iterator);
            // Ciclo 8: el iterador deja su valor en la pila (i++, llamadas,
            // asignaciones, desestructuraciones); descartarlo para no filtrar
            // un slot por iteración.
            _currentBlock!.Instructions.Add(new Instruction(Opcode.Pop));
        }
        EmitBranch(conditionBlock.Id);

        _currentBlock = afterBlock;
    }

    private void GenerateLabelled(BoundLabelledStatement labelled)
    {
        if (labelled.Statement is not (BoundWhileStatement or BoundDoWhileStatement or BoundForStatement or BoundSwitchStatement or BoundLabelledStatement))
        {
            var after = _currentFunction!.CreateBlock();
            _labelTargets[labelled.Label] = (after.Id, null);
            try
            {
                GenerateStatement(labelled.Statement);
                if (!_currentBlock!.EndsInBranch)
                    EmitBranch(after.Id);
                _currentBlock = after;
            }
            finally { _labelTargets.Remove(labelled.Label); }
            return;
        }

        _pendingLabels.Add(labelled.Label);
        try
        {
            GenerateStatement(labelled.Statement);
        }
        finally
        {
            _pendingLabels.Remove(labelled.Label);
            _labelTargets.Remove(labelled.Label);
        }
    }

    private void InstallPendingLabel(int breakTarget, int? continueTarget)
    {
        if (_pendingLabels.Count == 0)
            return;

        foreach (var label in _pendingLabels)
            _labelTargets[label] = (breakTarget, continueTarget);
        _pendingLabels.Clear();
    }

    private void GenerateBreak(string? label)
    {
        if (label != null && _labelTargets.TryGetValue(label, out var labelledTarget))
        {
            EmitBranch(labelledTarget.BreakTarget);
            return;
        }
        if (_breakTargets.Count == 0)
        {
            throw new InvalidOperationException("'break' emitted outside a loop or switch");
        }

        EmitBranch(_breakTargets.Peek());
    }

    private void GenerateContinue(string? label)
    {
        if (label != null && _labelTargets.TryGetValue(label, out var labelledTarget))
        {
            if (labelledTarget.ContinueTarget == null)
                throw new InvalidOperationException($"Label '{label}' does not name an iteration statement");
            EmitBranch(labelledTarget.ContinueTarget.Value);
            return;
        }
        if (_loopTargets.Count == 0)
        {
            throw new InvalidOperationException("'continue' emitted outside a loop");
        }

        EmitBranch(_loopTargets.Peek().ContinueBlockId);
    }

    private void GenerateThrow(BoundThrowStatement throwStmt)
    {
        GenerateExpression(throwStmt.Expression);
        EmitThrow();
    }

    private void GenerateTry(BoundTryStatement tryStmt)
    {
        var func = _currentFunction!;
        if (func.UsesGeneratorRegions)
        {
            GenerateGeneratorTry(tryStmt, func);
            return;
        }
        var handlerBlock = func.CreateBlock();
        var finallyBlock = tryStmt.FinallyBlock != null ? func.CreateBlock() : null;
        var afterBlock = func.CreateBlock();
        var exitBlock = finallyBlock ?? afterBlock;

        // Protected region: the VM dispatches thrown values to handlerBlock
        // with the exception value pushed on a stack reset to EnterTry depth.
        _currentBlock!.Instructions.Add(new Instruction(Opcode.EnterTry, handlerBlock.Id));
        GenerateStatement(tryStmt.TryBlock);
        if (!_currentBlock.EndsInBranch)
        {
            _currentBlock.Instructions.Add(new Instruction(Opcode.LeaveTry));
            EmitBranch(exitBlock.Id);
        }

        _currentBlock = handlerBlock;
        if (tryStmt.CatchBlock != null)
        {
            if (tryStmt.CatchVariable is LocalSymbol catchLocal)
                EmitStoreLocal(GetLocalIndex(catchLocal));
            else
                _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
            GenerateStatement(tryStmt.CatchBlock);
            if (!_currentBlock.EndsInBranch)
                EmitBranch(exitBlock.Id);
        }
        else
        {
            // try/finally without catch: run finally, then rethrow the value.
            if (tryStmt.FinallyBlock != null)
                GenerateStatement(tryStmt.FinallyBlock);
            EmitThrow();
        }

        if (finallyBlock != null)
        {
            _currentBlock = finallyBlock;
            GenerateStatement(tryStmt.FinallyBlock!);
            if (!_currentBlock.EndsInBranch)
                EmitBranch(afterBlock.Id);
        }

        _currentBlock = afterBlock;
    }

    private void GenerateExpression(BoundNode expr)
    {
        switch (expr)
        {
            case BoundLiteralExpression lit:
                GenerateLiteral(lit);
                break;
            case BoundVariableExpression varExpr:
                GenerateVariableLoad(varExpr);
                break;
            case BoundBinaryExpression bin:
                GenerateBinary(bin);
                break;
            case BoundUnaryExpression unary:
                GenerateUnary(unary);
                break;
            case BoundCallExpression call:
                GenerateCall(call);
                break;
            case BoundAssignmentExpression assign:
                GenerateAssignment(assign);
                break;
            case BoundDestructuringAssignmentExpression destructuringAssign:
                GenerateDestructuringAssignment(destructuringAssign);
                break;
            case BoundThisExpression thisExpression:
                EmitBoundReceiver(thisExpression.Receiver);
                break;
            case BoundSuperExpression superExpression:
                EmitSuperReceiver(superExpression);
                break;
            case BoundMemberAccessExpression member:
                GenerateMemberAccess(member);
                break;
            case BoundNewExpression newExpr:
                GenerateNew(newExpr);
                break;
            case BoundObjectLiteralExpression objLit:
                GenerateObjectLiteral(objLit);
                break;
            case BoundArrayLiteralExpression arrLit:
                GenerateArrayLiteral(arrLit);
                break;
            case BoundSpreadExpression spread:
                GenerateExpression(spread.Expression);
                break;
            case BoundRegexLiteralExpression regex:
                _currentBlock!.Instructions.Add(new Instruction(
                    Opcode.LoadConst_Regex, 0, 0, $"{regex.Pattern}\0{regex.Flags}"));
                break;
            case BoundIndexExpression indexExpr:
                GenerateIndex(indexExpr);
                break;
            case BoundEnumerateKeysExpression enumerateKeys:
                GenerateExpression(enumerateKeys.Operand);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.EnumerateKeys));
                break;
            case BoundArraySliceExpression slice:
                GenerateExpression(slice.Source);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.ArraySliceFrom, slice.Start));
                break;
            case BoundNormalizeObjectRestKeyExpression normalize:
                GenerateExpression(normalize.Source);
                GenerateExpression(normalize.RawKey);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.NormalizeObjectRestKey));
                break;
            case BoundLoadObjectRestKeyExpression scopedRead:
                GenerateExpression(scopedRead.Source);
                GenerateExpression(scopedRead.RawKey);
                GenerateExpression(scopedRead.NormalizedKey);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadObjectRestKey));
                break;
            case BoundObjectRestExpression rest:
                GenerateExpression(rest.Source);
                if (rest.ScopedExcludedKeys is { } scopedKeys)
                {
                    foreach (var key in scopedKeys) GenerateExpression(key);
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.ObjectRestComputed, scopedKeys.Count));
                }
                else
                {
                    foreach (var key in rest.ExcludedKeys)
                        _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_String, 0, 0, key));
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.ObjectRest, rest.ExcludedKeys.Count));
                }
                break;
            case BoundIterableValuesExpression iterable:
                GenerateExpression(iterable.Source);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.IterableValues));
                break;
            case BoundConditionalExpression cond:
                GenerateConditional(cond);
                break;
            case BoundLambdaExpression lambda:
                _liftedFunctions.Enqueue(lambda.Function);
                if (lambda.Function.CapturedVariables.Count > 0)
                    EmitClosureCreation(lambda.Function.Symbol.Name,
                        lambda.Function.CapturedVariables.Select(s => s.Name).ToList());
                else
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadFunc, 0, 0, lambda.Function.Symbol.Name));
                break;
            case BoundClassExpression classExpression:
                _liftedClasses.Enqueue(classExpression.Declaration);
                // AG: los estáticos de la expresión de clase corren al evaluarla
                // (AG-D4), y su valor es el constructor, como una clase declarada
                // como valor (W-7). El `LoadConst_String` anterior filtraba el
                // nombre interno como valor (`typeof` → "string").
                GenerateClassRuntimeInitializer(classExpression.Declaration);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadFunc, 0, 0,
                    $"{classExpression.Declaration.Symbol.Name}::.ctor"));
                break;
            case BoundCastExpression cast:
                GenerateExpression(cast.Operand);
                break;
            case BoundTypeofExpression typeofExpr:
                GenerateExpression(typeofExpr.Operand);
                if (typeofExpr.KnownTag is { } knownTag)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.Pop));
                    _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_String, 0, 0, knownTag));
                }
                else
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.TypeOf));
                }
                break;
            case BoundVoidExpression voidExpr:
                GenerateExpression(voidExpr.Operand);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.Pop));
                _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
                break;
            case BoundDeleteFieldExpression deleteField:
            {
                GenerateExpression(deleteField.Object);
                if (deleteField.IsNullConditional)
                {
                    var func = _currentFunction!;
                    var nullBlock = func.CreateBlock();
                    var doDeleteBlock = func.CreateBlock();
                    var endBlock = func.CreateBlock();

                    _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
                    _currentBlock.Instructions.Add(new Instruction(Opcode.NullCheck));
                    EmitBranchTrue(nullBlock.Id);
                    EmitBranch(doDeleteBlock.Id);

                    _currentBlock = nullBlock;
                    _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
                    _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
                    _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_Bool, 1));
                    EmitBranch(endBlock.Id);

                    _currentBlock = doDeleteBlock;
                    _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.DeleteField, 0, 0, deleteField.FieldName));
                    EmitBranch(endBlock.Id);

                    _currentBlock = endBlock;
                }
                else
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.DeleteField, 0, 0, deleteField.FieldName));
                }
                break;
            }
            case BoundDeleteIndexExpression deleteIndex:
            {
                GenerateExpression(deleteIndex.Object);
                if (!deleteIndex.IsNullConditional)
                {
                    GenerateExpression(deleteIndex.Index);
                    EmitDeleteIndex(deleteIndex.IsMapStorage);
                    break;
                }

                var func = _currentFunction!;
                var nullBlock = func.CreateBlock();
                var doDeleteBlock = func.CreateBlock();
                var endBlock = func.CreateBlock();

                // NullCheck peeks at the receiver; BranchTrue consumes only the condition.
                _currentBlock!.Instructions.Add(new Instruction(Opcode.NullCheck));
                EmitBranchTrue(nullBlock.Id);
                EmitBranch(doDeleteBlock.Id);

                _currentBlock = nullBlock;
                _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
                _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_Bool, 1));
                EmitBranch(endBlock.Id);

                _currentBlock = doDeleteBlock;
                GenerateExpression(deleteIndex.Index);
                EmitDeleteIndex(deleteIndex.IsMapStorage);
                EmitBranch(endBlock.Id);

                _currentBlock = endBlock;
                break;
            }
            case BoundDeleteNonReferenceExpression deleteNonRef:
                GenerateExpression(deleteNonRef.Operand);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.Pop));
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_Bool, 0));
                break;
            case BoundArrayConstructionExpression arrCtor:
                foreach (var arg in arrCtor.Arguments)
                    GenerateExpression(arg);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.Call, 0, arrCtor.Arguments.Count, "Array::ctor"));
                break;
            case BoundAwaitExpression awaitExpr:
                GenerateExpression(awaitExpr.Expression);
                EmitAwait();
                break;
            default:
                EmitNop();
                break;
        }
    }

    private void GenerateArrayLiteral(BoundArrayLiteralExpression arrLit)
    {
        if (arrLit.Elements.Any(element => element is BoundSpreadExpression))
        {
            _currentBlock!.Instructions.Add(new Instruction(Opcode.NewArray, 0));
            foreach (var element in arrLit.Elements)
            {
                _currentBlock.Instructions.Add(new Instruction(Opcode.Dup));
                if (element is BoundSpreadExpression spread)
                {
                    GenerateExpression(spread.Expression);
                    _currentBlock.Instructions.Add(new Instruction(Opcode.ArrayAppendSpread));
                }
                else
                {
                    GenerateExpression(element);
                    _currentBlock.Instructions.Add(new Instruction(Opcode.ArrayAppend));
                }
            }
            return;
        }

        foreach (var element in arrLit.Elements)
            GenerateExpression(element);
        _currentBlock!.Instructions.Add(new Instruction(Opcode.NewArray, arrLit.Elements.Count));
    }

    private void GenerateConditional(BoundConditionalExpression cond)
    {
        var func = _currentFunction!;
        var trueBlock = func.CreateBlock();
        var falseBlock = func.CreateBlock();
        var mergeBlock = func.CreateBlock();

        GenerateExpression(cond.Condition);
        EmitBranchTrue(trueBlock.Id);
        EmitBranch(falseBlock.Id);

        _currentBlock = trueBlock;
        GenerateExpression(cond.WhenTrue);
        EmitBranch(mergeBlock.Id);

        _currentBlock = falseBlock;
        GenerateExpression(cond.WhenFalse);
        EmitBranch(mergeBlock.Id);

        _currentBlock = mergeBlock;
    }

    private void GenerateLiteral(BoundLiteralExpression lit)
    {
        var type = GetRuntimeType(lit.Type);
        switch (type)
        {
            case TsPrimitiveType { IsNumericType: true } p:
                if (type == TsType.Int32 || type == TsType.Int16 || type == TsType.Int8)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_I32, Convert.ToInt32(lit.Value)));
                }
                else if (type == TsType.Int64)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_I64, 0, 0, Convert.ToInt64(lit.Value)));
                }
                else if (type == TsType.UInt64)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_U64, 0, 0, Convert.ToUInt64(lit.Value)));
                }
                else if (type == TsType.BigInt)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_BigInt, 0, 0, lit.Value?.ToString() ?? "0"));
                }
                else if (type == TsType.Float32)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_F32, 0, 0, Convert.ToSingle(lit.Value)));
                }
                else if (type == TsType.Decimal)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_Decimal, 0, 0, Convert.ToDecimal(lit.Value)));
                }
                else
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_F64, 0, 0, Convert.ToDouble(lit.Value)));
                }
                break;
            case TsPrimitiveType { Name: "string" }:
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_String, 0, 0, lit.Value?.ToString() ?? ""));
                break;
            case TsPrimitiveType { Name: "bool" }:
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_Bool, Convert.ToBoolean(lit.Value) ? 1 : 0));
                break;
            case TsPrimitiveType { Name: "void" }:
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
                break;
            case TsUndefinedType:
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
                break;
            default:
                if (lit.Value == null)
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_Null));
                else
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_String, 0, 0, lit.Value.ToString()));
                break;
        }
    }

    private void GenerateVariableLoad(BoundVariableExpression varExpr)
    {
        if (varExpr.Symbol is LocalSymbol { ConstantInitializer: BoundNode exportedConstant }
            && IsCompileTimeConstant(exportedConstant))
        {
            GenerateExpression(exportedConstant);
            return;
        }

        if (varExpr.Symbol is LocalSymbol moduleGlobal && IsModuleGlobalSymbol(moduleGlobal))
        {
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadGlobal, 0, 0, GetModuleGlobalKey(moduleGlobal)));
            return;
        }

        if (_moduleConstantInitializers.TryGetValue(varExpr.Symbol, out var moduleConstant))
        {
            GenerateExpression(moduleConstant);
            return;
        }

        if (IsBoxedSymbol(varExpr.Symbol))
        {
            EmitLoadLocal(GetBoxSlot(varExpr.Symbol.Name));
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadField, 0, 0, "v"));
            return;
        }

        if (varExpr.Symbol is LocalSymbol local)
        {
            int localIndex = GetLocalIndex(local);
            EmitLoadLocal(localIndex);
        }
        else if (varExpr.Symbol is ParameterSymbol param)
        {
            int paramIndex = GetParameterIndex(param);
            if (paramIndex < 0)
                throw new InvalidOperationException(
                    $"Parameter '{param.Name}' is not visible in function '{_currentFunction?.Name}'");
            EmitLoadArg(paramIndex);
        }
        else if (varExpr.Symbol is FunctionSymbol fn)
        {
            // A function referenced as a value (passed as a callback); if it
            // captures, materialize the closure with the current boxes.
            var target = fn.TargetName ?? fn.Name;
            if (_capturesByFunction.TryGetValue(target, out var captured))
                EmitClosureCreation(target, captured);
            else
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadFunc, 0, 0, target));
        }
        else if (varExpr.Symbol is ClassSymbol cls)
        {
            // Ciclo 10 (W, W-7): la clase como valor es su constructor.
            // `NewObject` no pasa por aquí (toma el nombre de tipo como
            // operando), así que esta rama solo cubre la clase como valor
            // (argumento, alias, retorno, `typeof`, `===`).
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadFunc, 0, 0, $"{cls.Name}::.ctor"));
        }
    }

    private void GenerateBinary(BoundBinaryExpression bin, Action? emitLeft = null)
    {
        if (bin.Operator == TokenKind.QuestionQuestion)
        {
            GenerateNullishCoalescing(bin);
            return;
        }

        if (bin.Operator == TokenKind.InKeyword)
        {
            GenerateInOperator(bin);
            return;
        }

        // `&&` and `||` must short-circuit: the right operand may have host
        // side effects that TypeScript semantics require skipping.
        if (bin.Operator is TokenKind.AmpersandAmpersand or TokenKind.PipePipe)
        {
            var func = _currentFunction!;
            var rhsBlock = func.CreateBlock();
            var endBlock = func.CreateBlock();

            GenerateExpression(bin.Left);
            _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
            if (bin.Operator == TokenKind.AmpersandAmpersand)
            {
                EmitBranchFalse(endBlock.Id);
            }
            else
            {
                EmitBranchTrue(endBlock.Id);
            }
            EmitBranch(rhsBlock.Id);

            _currentBlock = rhsBlock;
            _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
            GenerateExpression(bin.Right);
            EmitBranch(endBlock.Id);

            _currentBlock = endBlock;
            return;
        }

        if (emitLeft is null)
            GenerateExpression(bin.Left);
        else
            emitLeft();
        GenerateExpression(bin.Right);

        var leftType = GetRuntimeType(bin.Left.Type);
        var rightType = GetRuntimeType(bin.Right.Type);
        var operandType = leftType is TsAnyType ? rightType : leftType;
        if (bin.Operator == TokenKind.Plus && (leftType == TsType.String || rightType == TsType.String))
        {
            operandType = TsType.String;
        }
        else if (bin.Operator is TokenKind.TripleEquals or TokenKind.StrictNotEquals &&
            (leftType is TsAnyType || rightType is TsAnyType))
        {
            operandType = TsType.Any;
        }
        // Dynamic (host) operands carry their width at runtime; compute in the
        // widest integer form so an int64 payload never truncates through i32 ops.
        else if ((leftType is TsAnyType || rightType is TsAnyType) &&
            (operandType == TsType.Int32 || operandType is TsAnyType))
        {
            operandType = TsType.Int64;
        }
        else if (leftType.IsNumeric && rightType.IsNumeric)
        {
            // Mixed widths compute in the wider representation (int literal
            // against a `number` variable must not truncate to i32 ops).
            operandType = Binder.WiderNumeric(leftType, rightType);
        }
        var opcode = InferBinaryOpcode(operandType, bin.Operator);
        if (opcode == Opcode.Nop)
            throw new InvalidOperationException(
                $"Unsupported binary operator '{bin.Operator}' for operand type '{operandType}'");
        _currentBlock!.Instructions.Add(new Instruction(opcode));
    }

    private void GenerateInOperator(BoundBinaryExpression bin)
    {
        if (bin.Left is not BoundLiteralExpression { Value: string propertyName })
            throw new InvalidOperationException("The 'in' operator currently requires a string literal property name");

        GenerateExpression(bin.Right);
        _currentBlock!.Instructions.Add(new Instruction(Opcode.HasFieldLiteral, 0, 0, propertyName));
    }

    private static TsType GetRuntimeType(TsType type) =>
        type is TsLiteralType literal ? literal.BaseType : type;

    private void GenerateNullishCoalescing(BoundBinaryExpression bin)
    {
        var func = _currentFunction!;
        var rightBlock = func.CreateBlock();
        var keepLeftBlock = func.CreateBlock();
        var endBlock = func.CreateBlock();

        GenerateExpression(bin.Left);
        _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
        _currentBlock.Instructions.Add(new Instruction(Opcode.NullCheck));
        EmitBranchTrue(rightBlock.Id);
        EmitBranch(keepLeftBlock.Id);

        _currentBlock = rightBlock;
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop)); // duplicated left
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop)); // original left
        GenerateExpression(bin.Right);
        EmitBranch(endBlock.Id);

        _currentBlock = keepLeftBlock;
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop)); // duplicated left
        EmitBranch(endBlock.Id);

        _currentBlock = endBlock;
    }

    private void GenerateUnary(BoundUnaryExpression unary)
    {
        if (unary.Operator is TokenKind.PlusPlus or TokenKind.MinusMinus)
        {
            GenerateIncrement(unary);
            return;
        }

        GenerateExpression(unary.Operand);

        var opcode = unary.Operator switch
        {
            TokenKind.Minus => unary.Operand.Type == TsType.Int32 ? Opcode.Neg_I32 :
                              unary.Operand.Type == TsType.Int64 ? Opcode.Neg_I64 :
                              unary.Operand.Type == TsType.Float32 ? Opcode.Neg_F32 :
                              unary.Operand.Type == TsType.Decimal ? Opcode.Neg_Decimal : Opcode.Neg_F64,
            TokenKind.Bang => Opcode.Not_Bool,
            TokenKind.Tilde => unary.Operand.Type == TsType.Int64 ? Opcode.Not_I64 :
                               unary.Operand.Type == TsType.UInt64 || unary.Operand.Type == TsType.BigInt
                                   ? Opcode.Not_U64
                                   : Opcode.Not_I32,
            TokenKind.Plus => Opcode.Nop,
            _ => Opcode.Nop
        };
        if (opcode != Opcode.Nop)
            _currentBlock!.Instructions.Add(new Instruction(opcode));
    }

    private void GenerateIncrement(BoundUnaryExpression unary)
    {
        if (unary.Operand is BoundVariableExpression target &&
            target.Symbol is (LocalSymbol or ParameterSymbol))
        {
            if (target.Symbol is LocalSymbol moduleGlobal && IsModuleGlobalSymbol(moduleGlobal))
            {
                // Ciclo 10 (X): la global de módulo vive en la tabla de
                // globales, no en el frame. Misma disciplina de pila que el
                // campo estático de GenerateMemberIncrement: prefijo deja el
                // valor nuevo, postfijo deja el valor viejo; en posición de
                // sentencia lo descarta el Pop de GenerateStatement.
                string key = GetModuleGlobalKey(moduleGlobal);
                var globalAddOp = IncrementAddOp(moduleGlobal.Type, unary.Operator);
                bool globalIsInt64 = moduleGlobal.Type == TsType.Int64;
                bool globalIsFloat = moduleGlobal.Type == TsType.Float64 || moduleGlobal.Type == TsType.Number || moduleGlobal.Type == TsType.Float32;

                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadGlobal, 0, 0, key));
                if (unary.IsPrefix)
                {
                    EmitConstOne(globalIsInt64, globalIsFloat);
                    _currentBlock!.Instructions.Add(new Instruction(globalAddOp));
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
                }
                else
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
                    EmitConstOne(globalIsInt64, globalIsFloat);
                    _currentBlock!.Instructions.Add(new Instruction(globalAddOp));
                }
                _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreGlobal, 0, 0, key));
                return;
            }

            if (IsBoxedSymbol(target.Symbol))
            {
                GenerateBoxedIncrement(unary, target.Symbol);
                return;
            }

            int slot = target.Symbol is LocalSymbol local
                ? GetLocalIndex(local)
                : GetParameterIndex((ParameterSymbol)target.Symbol);

            var slotType = target.Symbol.Type;
            bool isInt64 = slotType == TsType.Int64;
            bool isFloat = slotType == TsType.Float64 || slotType == TsType.Number || slotType == TsType.Float32;
            var addOp = IncrementAddOp(slotType, unary.Operator);

            EmitLoadLocal(slot);
            if (unary.IsPrefix)
            {
                EmitConstOne(isInt64, isFloat);
                _currentBlock!.Instructions.Add(new Instruction(addOp));
                _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
                EmitStoreLocal(slot);
            }
            else
            {
                _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
                EmitConstOne(isInt64, isFloat);
                _currentBlock!.Instructions.Add(new Instruction(addOp));
                EmitStoreLocal(slot);
            }
            return;
        }

        if (unary.Operand is BoundMemberAccessExpression member &&
            !member.IsNullConditional &&
            IsWritableFieldMember(member.Member))
        {
            GenerateMemberIncrement(unary, member);
            return;
        }

        if (unary.Operand is BoundIndexExpression index && !index.IsNullConditional)
        {
            GenerateIndexIncrement(unary, index);
            return;
        }

        // Unsupported target: evaluate the operand so the stack stays balanced.
        GenerateExpression(unary.Operand);
    }

    // A member is writable through LoadField/StoreField when it is a field
    // (static or not) or a property without getter or setter that is not
    // static. Properties with accessors are rejected by the binder with a
    // diagnostic; a static property without accessors has no assigned path
    // (the static path applies only to FieldSymbol) and falls through to
    // the unsupported-target fallback below.
    private static bool IsWritableFieldMember(Symbol member) => member switch
    {
        FieldSymbol => true,
        PropertySymbol prop => !prop.HasGetter && !prop.HasSetter && !prop.IsStatic,
        _ => false
    };

    // Same opcode selection as the local/parameter path: int64 -> I64,
    // float64/number/float32 -> F64, everything else (including decimal,
    // bigint, uint32, uint64, int16, any) -> I32.
    private static Opcode IncrementAddOp(TsType operandType, TokenKind op)
    {
        bool isInt64 = operandType == TsType.Int64;
        bool isFloat = operandType == TsType.Float64 || operandType == TsType.Number || operandType == TsType.Float32;
        return op == TokenKind.PlusPlus
            ? (isInt64 ? Opcode.Add_I64 : isFloat ? Opcode.Add_F64 : Opcode.Add_I32)
            : (isInt64 ? Opcode.Sub_I64 : isFloat ? Opcode.Sub_F64 : Opcode.Sub_I32);
    }

    private void GenerateMemberIncrement(BoundUnaryExpression unary, BoundMemberAccessExpression member)
    {
        var operandType = unary.Operand.Type;
        bool isInt64 = operandType == TsType.Int64;
        bool isFloat = operandType == TsType.Float64 || operandType == TsType.Number || operandType == TsType.Float32;
        var addOp = IncrementAddOp(operandType, unary.Operator);

        if (member.Member is FieldSymbol { IsStatic: true } staticField)
        {
            string globalName = staticField.RuntimeName ?? $"$static:{staticField.Name}";
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadGlobal, 0, 0, globalName));
            if (unary.IsPrefix)
            {
                EmitConstOne(isInt64, isFloat);
                _currentBlock!.Instructions.Add(new Instruction(addOp));
                _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
            }
            else
            {
                _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
                EmitConstOne(isInt64, isFloat);
                _currentBlock!.Instructions.Add(new Instruction(addOp));
            }
            _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreGlobal, 0, 0, globalName));
            return;
        }

        string fieldName = GetRuntimeFieldName(member.Member);
        int tmpObj = _tempCounter++;
        int tmpVal = _tempCounter++;

        GenerateExpression(member.Object);
        EmitStoreLocal(tmpObj);
        EmitLoadLocal(tmpObj);
        _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadField, 0, 0, fieldName));
        if (unary.IsPrefix)
        {
            EmitConstOne(isInt64, isFloat);
            _currentBlock!.Instructions.Add(new Instruction(addOp));
            _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
        }
        else
        {
            _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
            EmitConstOne(isInt64, isFloat);
            _currentBlock!.Instructions.Add(new Instruction(addOp));
        }
        EmitStoreLocal(tmpVal);
        EmitLoadLocal(tmpObj);
        EmitLoadLocal(tmpVal);
        _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, fieldName));
    }

    private void GenerateIndexIncrement(BoundUnaryExpression unary, BoundIndexExpression index)
    {
        var operandType = unary.Operand.Type;
        bool isInt64 = operandType == TsType.Int64;
        bool isFloat = operandType == TsType.Float64 || operandType == TsType.Number || operandType == TsType.Float32;
        var addOp = IncrementAddOp(operandType, unary.Operator);

        int tmpObj = _tempCounter++;
        int tmpIdx = _tempCounter++;
        int tmpVal = _tempCounter++;

        GenerateExpression(index.Object);
        EmitStoreLocal(tmpObj);
        GenerateExpression(index.Index);
        EmitStoreLocal(tmpIdx);
        EmitLoadLocal(tmpObj);
        EmitLoadLocal(tmpIdx);
        EmitLoadIndex(index.IsMapStorage);
        if (unary.IsPrefix)
        {
            EmitConstOne(isInt64, isFloat);
            _currentBlock!.Instructions.Add(new Instruction(addOp));
            _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
        }
        else
        {
            _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
            EmitConstOne(isInt64, isFloat);
            _currentBlock!.Instructions.Add(new Instruction(addOp));
        }
        EmitStoreLocal(tmpVal);
        EmitLoadLocal(tmpObj);
        EmitLoadLocal(tmpIdx);
        EmitLoadLocal(tmpVal);
        EmitStoreIndex(index.IsMapStorage);
    }

    private void GenerateBoxedIncrement(BoundUnaryExpression unary, Symbol symbol)
    {
        int boxSlot = GetBoxSlot(symbol.Name);
        var slotType = symbol.Type;
        bool isInt64 = slotType == TsType.Int64;
        bool isFloat = slotType == TsType.Float64 || slotType == TsType.Number || slotType == TsType.Float32;
        var addOp = unary.Operator == TokenKind.PlusPlus
            ? (isInt64 ? Opcode.Add_I64 : isFloat ? Opcode.Add_F64 : Opcode.Add_I32)
            : (isInt64 ? Opcode.Sub_I64 : isFloat ? Opcode.Sub_F64 : Opcode.Sub_I32);

        if (!unary.IsPrefix)
        {
            // old value first: box.v
            EmitLoadLocal(boxSlot);
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadField, 0, 0, "v"));
        }

        EmitLoadLocal(boxSlot);
        _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
        _currentBlock.Instructions.Add(new Instruction(Opcode.LoadField, 0, 0, "v"));
        EmitConstOne(isInt64, isFloat);
        _currentBlock.Instructions.Add(new Instruction(addOp));
        _currentBlock.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, "v"));

        if (unary.IsPrefix)
        {
            EmitLoadLocal(boxSlot);
            _currentBlock.Instructions.Add(new Instruction(Opcode.LoadField, 0, 0, "v"));
        }
    }

    private void EmitConstOne(bool isInt64, bool isFloat = false)
    {
        if (isInt64)
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_I64, 0, 0, 1L));
        else if (isFloat)
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_F64, 0, 0, 1.0));
        else
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_I32, 1));
    }

    private void GenerateCall(BoundCallExpression call)
    {
        bool hasSpread = call.Arguments.Any(arg => arg is BoundSpreadExpression);
        if (!call.IsNullConditional && call.Callee is BoundMemberAccessExpression { IsNullConditional: true } optionalSet
            && IsNativeSetMember(optionalSet))
        {
            GenerateOptionalSetReceiverCall(call, optionalSet, hasSpread);
            return;
        }
        if (!(_currentFunction!.IsGenerator && _currentFunction.IsAsync) &&
            GetGeneratorReferenceTarget(call, hasSpread) is { } generatorReferenceTarget)
        {
            GenerateMemberReferenceCall(call, generatorReferenceTarget, helper: null);
            return;
        }
        if (GetSetReferenceTarget(call, hasSpread) is { } setReferenceTarget)
        {
            GenerateMemberReferenceCall(call, setReferenceTarget);
            return;
        }
        if (GetRegexReferenceTarget(call, hasSpread) is { } regexReferenceTarget)
        {
            GenerateMemberReferenceCall(call, regexReferenceTarget, InvokeRegexMemberReference);
            return;
        }
        if (call.Callee is BoundMemberAccessExpression { IsNullConditional: true } optionalString
            && IsStringMemberCandidate(optionalString) && !call.IsNullConditional && !hasSpread)
        {
            GenerateOptionalStringDotCall(call, optionalString);
            return;
        }
        if (GetStringReferenceTarget(call, hasSpread) is { } stringReferenceTarget)
        {
            GenerateMemberReferenceCall(call, stringReferenceTarget, InvokeStringMemberReference,
                guardStringReceiver: call.Callee switch
                {
                    BoundMemberAccessExpression member => member.IsNullConditional,
                    BoundIndexExpression index => index.IsNullConditional,
                    _ => false,
                });
            return;
        }
        if (call.IsNullConditional)
        {
            GenerateOptionalCall(call);
            return;
        }
        if (hasSpread)
        {
            GenerateSpreadCall(call);
            return;
        }
        if (call.Callee is BoundMemberAccessExpression memberAccess)
        {
            if (memberAccess.Member is MethodSymbol { DeclaringClassName: null } structuralMethod)
            {
                GenerateExpression(memberAccess.Object);
                for (int i = 0; i < call.Arguments.Count; i++)
                    GenerateExpression(call.Arguments[i]);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.CallVirt, 0, call.Arguments.Count + 1, structuralMethod.Name));
                return;
            }

            if (memberAccess.Member is not MethodSymbol)
            {
                // AG: una variable local con `ClassValue` también accede por
                // la puerta estática, como un `ClassSymbol`.
                if ((memberAccess.Object is BoundVariableExpression { Symbol: ClassSymbol }
                        || memberAccess.Object is BoundVariableExpression { Symbol: LocalSymbol { ClassValue: not null } })
                    && memberAccess.Member is (FieldSymbol { IsStatic: true } or PropertySymbol { IsStatic: true, HasGetter: true }))
                {
                    // Ciclo 10 (W, W-9): el miembro estático guarda un
                    // callable y no hay objeto receptor: se carga el valor
                    // del miembro (LoadGlobal para campos, Call al getter
                    // para propiedades) y se despacha dinámicamente.
                    GenerateMemberAccess(memberAccess);
                    for (int i = 0; i < call.Arguments.Count; i++)
                        GenerateExpression(call.Arguments[i]);
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.CallDynamic, call.Arguments.Count));
                    return;
                }
                GenerateExpression(memberAccess.Object);
                for (int i = 0; i < call.Arguments.Count; i++)
                    GenerateExpression(call.Arguments[i]);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.CallVirt, 0, call.Arguments.Count + 1, memberAccess.Member.Name));
                return;
            }

            // Static namespace calls (Math.floor, console.log) have no
            // receiver value: skip the object load and pass args only.
            // Ciclo 9 (V, opción (b), §2.2): un método estático de usuario se
            // genera con un parámetro fantasma `this` en el slot 0, así que
            // se emite un receptor ficticio `Void` cuando el método tiene
            // parámetros explícitos, para que los argumentos caigan en sus
            // slots. Solo se emite cuando hay parámetros que proteger: los
            // builtins de espacio de nombres (Math, Array, JSON, ...) tienen
            // listas de parámetros vacías y leerían el ficticio como primer
            // argumento. Un estático de usuario sin parámetros tampoco lo
            // necesita: su slot 0 ya es `Void` por inicialización del frame.
            // AG: una variable local con `ClassValue` también accede por la
            // puerta estática, como un `ClassSymbol`.
            bool isStatic = memberAccess.Object is BoundVariableExpression { Symbol: ClassSymbol }
                || memberAccess.Object is BoundVariableExpression { Symbol: LocalSymbol { ClassValue: not null } };
            bool emitDummyReceiver = isStatic
                && memberAccess.Member is MethodSymbol staticMethodSym
                && staticMethodSym.Parameters.Count > 0;
            if (!isStatic)
                GenerateExpression(memberAccess.Object);
            else if (emitDummyReceiver)
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
            for (int i = 0; i < call.Arguments.Count; i++)
                GenerateExpression(call.Arguments[i]);

            string funcName = "";
            // AG: el nombre de la clase sale del símbolo que corresponda
            // (`ClassSymbol` o el `ClassValue` de la variable local).
            string className = isStatic
                ? memberAccess.Object switch
                {
                    BoundVariableExpression { Symbol: ClassSymbol cs } => cs.Name,
                    BoundVariableExpression { Symbol: LocalSymbol { ClassValue: { } cv } } => cv.Name,
                    _ => GetClassName(memberAccess.Object.Type)
                }
                : GetClassName(memberAccess.Object.Type);

            if (memberAccess.Member is MethodSymbol methodSym)
                funcName = $"{methodSym.DeclaringClassName ?? className}::{methodSym.RuntimeName ?? methodSym.Name}";
            else if (memberAccess.Member is PropertySymbol propSym)
                funcName = $"{className}::{propSym.Name}";

            int totalArgs = (!isStatic || emitDummyReceiver ? 1 : 0) + call.Arguments.Count;
            _currentBlock!.Instructions.Add(new Instruction(Opcode.Call, 0, totalArgs, funcName));
        }
        else if (call.Callee is BoundSuperExpression superExpr)
        {
            EmitSuperReceiver(superExpr);
            for (int i = 0; i < call.Arguments.Count; i++)
                GenerateExpression(call.Arguments[i]);

            string baseClassName = superExpr.BaseClass.Name;
            string funcName = $"{baseClassName}::.ctor";
            int totalArgs = 1 + call.Arguments.Count;
            _currentBlock!.Instructions.Add(new Instruction(Opcode.Call, 0, totalArgs, funcName));
        }
        else if (call.Callee is BoundVariableExpression { Symbol: FunctionSymbol funcSym })
        {
            var target = funcSym.TargetName ?? funcSym.Name;
            if (_capturesByFunction.TryGetValue(target, out var calleeCaptures))
            {
                // Direct call to a capturing function: build its closure from
                // the boxes visible here, then dispatch dynamically.
                EmitClosureCreation(target, calleeCaptures);
                for (int i = 0; i < call.Arguments.Count; i++)
                    GenerateExpression(call.Arguments[i]);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.CallDynamic, call.Arguments.Count));
                return;
            }

            for (int i = 0; i < call.Arguments.Count; i++)
                GenerateExpression(call.Arguments[i]);

            _currentBlock!.Instructions.Add(new Instruction(
                Opcode.Call, 0, call.Arguments.Count, target));
        }
        else
        {
            // Function-typed value (callback parameter, local, lambda result):
            // push the callee value, then args, and dispatch dynamically.
            GenerateExpression(call.Callee);
            for (int i = 0; i < call.Arguments.Count; i++)
                GenerateExpression(call.Arguments[i]);

            _currentBlock!.Instructions.Add(new Instruction(Opcode.CallDynamic, call.Arguments.Count));
        }
    }

    // Existing qualified Call with [receiver, property value, positional array].
    // The spelling cannot be defined by TypeScript source or linked as a user function.
    private const string InvokeStringMemberReference = "String::<invoke-member-reference>";
    private const string InvokeSetMemberReference = "Set::<invoke-member-reference>";
    private const string InvokeRegexMemberReference = "RegExp::<invoke-member-reference>";
    private const string ReadTypedRegexTestReference = "RegExp::<typed-test-reference>";

    private static bool IsNativeSetMethodName(string name) => name is
        "add" or "has" or "delete" or "clear" or "keys" or "values" or "entries" or "forEach";

    private static bool IsNativeSetReceiver(TsType type)
    {
        while (type is TsNullableType nullable)
            type = nullable.ElementType;
        return type is TsSetType || (type is TsUnionType union
            && union.Types.Count(t => t is not (TsNullType or TsUndefinedType)) == 1
            && union.Types.Any(t => t is TsSetType));
    }

    private static bool IsNativeSetMember(BoundMemberAccessExpression member) =>
        member.Member is MethodSymbol { DeclaringClassName: "Set" } method
        && IsNativeSetReceiver(member.Object.Type) && IsNativeSetMethodName(method.Name);

    private static bool IsGeneratorSelector(string name) => name is "next" or "return" or "throw";

    private static BoundNode? GetGeneratorReferenceTarget(BoundCallExpression call, bool hasSpread) => call.Callee switch
    {
        BoundMemberAccessExpression { IsNullConditional: false } member
            when (hasSpread || call.IsNullConditional) && member.Object.Type is TsAnyType &&
                IsGeneratorSelector(member.Member.Name) => member.Object,
        BoundIndexExpression { IsMapStorage: false, IsNullConditional: false, Index: BoundLiteralExpression { Value: string name } } index
            when index.Object.Type is TsAnyType && IsGeneratorSelector(name) => index.Object,
        _ => null
    };

    private static BoundNode? GetSetReferenceTarget(BoundCallExpression call, bool hasSpread)
    {
        if (call.Callee is BoundIndexExpression { IsMapStorage: false, IsNullConditional: false } index
            && (index.Object.Type is TsAnyType || IsNativeSetReceiver(index.Object.Type)))
        {
            // Known unrelated literals keep the original CallDynamic route.
            // Only an evaluated key can need the runtime reference check.
            if (index.Index is not BoundLiteralExpression literal ||
                (literal.Value is string name && IsNativeSetMethodName(name)))
                return index.Object;
        }
        if ((hasSpread || call.IsNullConditional) && call.Callee is BoundMemberAccessExpression member
            && (IsNativeSetMember(member) ||
                (member.Object.Type is TsAnyType && IsNativeSetMethodName(member.Member.Name))))
            return member.Object;
        return null;
    }

    private static bool IsRegexInterface(TsType type) =>
        type is TsInterfaceType { Name: "RegExp", IsStructural: true } shape &&
        shape.Properties.TryGetValue("lastIndex", out var cursor) && !cursor.IsReadonly &&
        cursor.Type.Equals(TsType.Number) &&
        shape.Methods.TryGetValue("test", out var test) && test.ReturnType.Equals(TsType.Bool) &&
        test.Parameters.Count == 1 &&
        (test.Parameters[0].Type is TsAnyType or TsUnknownType or TsNullableType { ElementType: TsUnknownType });

    private static bool IsStructuralRegexTest(BoundMemberAccessExpression access) =>
        access.Member is MethodSymbol { Name: "test", DeclaringClassName: null } && IsRegexInterface(access.Object.Type);

    private static BoundNode? GetRegexReferenceTarget(BoundCallExpression call, bool hasSpread)
    {
        if (call.Callee is BoundIndexExpression { IsMapStorage: false } index &&
            (index.Object.Type is TsAnyType || IsRegexInterface(index.Object.Type)) &&
            (index.Index is not BoundLiteralExpression literal || literal.Value is "test"))
            return index.Object;

        if ((hasSpread || call.IsNullConditional) &&
            call.Callee is BoundMemberAccessExpression { Member.Name: "test" } member &&
            (member.Object.Type is TsAnyType || IsStructuralRegexTest(member)))
            return member.Object;

        return null;
    }

    private static bool IsStringMethodName(string name) => name is
        "charAt" or "charCodeAt" or "codePointAt" or "at" or "toUpperCase" or "toLowerCase" or
        "trim" or "trimStart" or "trimEnd" or "substring" or "slice" or "repeat" or "concat" or
        "includes" or "startsWith" or "endsWith" or "indexOf" or "lastIndexOf" or "replace" or
        "replaceAll" or "padStart" or "padEnd" or "split";

    private static bool IsStringReceiver(TsType type)
    {
        while (type is TsNullableType nullable) type = nullable.ElementType;
        return type is TsPrimitiveType { Name: "string" } ||
            (type is TsUnionType union &&
                union.Types.Any(t => t is TsPrimitiveType { Name: "string" }) &&
                union.Types.All(t => t is TsNullType or TsUndefinedType or TsPrimitiveType { Name: "string" }));
    }

    private static bool IsStringMemberCandidate(BoundMemberAccessExpression member) =>
        IsStringMethodName(member.Member.Name) &&
        (IsStringReceiver(member.Object.Type) || member.Object.Type is TsAnyType);

    private static BoundNode? GetStringReferenceTarget(BoundCallExpression call, bool hasSpread) => call.Callee switch
    {
        BoundMemberAccessExpression member when IsStringMemberCandidate(member) && (hasSpread || call.IsNullConditional)
            => member.Object,
        BoundIndexExpression { IsMapStorage: false } index when IsStringReceiver(index.Object.Type)
            => index.Object,
        BoundIndexExpression { IsMapStorage: false, Object.Type: TsAnyType, Index: BoundLiteralExpression { Value: string key } } index
            when IsStringMethodName(key) => index.Object,
        _ => null,
    };

    private void GenerateOptionalStringDotCall(BoundCallExpression call, BoundMemberAccessExpression member)
    {
        // Preserve the existing managed/erased dot fallback on a live receiver.
        // Only the syntactic receiver guard for a represented String selector is new.
        GenerateExpression(member.Object);
        var func = _currentFunction!;
        var skip = func.CreateBlock();
        var invoke = func.CreateBlock();
        var end = func.CreateBlock();
        _currentBlock!.Instructions.Add(new Instruction(Opcode.NullCheck));
        EmitBranchTrue(skip.Id);
        EmitBranch(invoke.Id);
        _currentBlock = skip;
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
        _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
        EmitBranch(end.Id);
        _currentBlock = invoke;
        foreach (var argument in call.Arguments) GenerateExpression(argument);
        var qualified = member.Member is MethodSymbol { DeclaringClassName: "String" };
        _currentBlock.Instructions.Add(new Instruction(qualified ? Opcode.Call : Opcode.CallVirt,
            0, call.Arguments.Count + 1, qualified ? $"String::{member.Member.Name}" : member.Member.Name));
        EmitBranch(end.Id);
        _currentBlock = end;
    }

    private void GenerateMemberReferenceCall(BoundCallExpression call, BoundNode target,
        string? helper = InvokeSetMemberReference, bool guardStringReceiver = false)
    {
        // Keep the original receiver under the loaded value. Evaluate the key
        // (if any) and the property lookup before the argument expressions.
        int receiverSlot = _tempCounter++;
        GenerateExpression(target);
        EmitStoreLocal(receiverSlot);
        if (!guardStringReceiver)
        {
            GenerateMemberReferenceFromSlot(call, helper, receiverSlot);
            return;
        }

        EmitLoadLocal(receiverSlot);
        var func = _currentFunction!;
        var skip = func.CreateBlock();
        var load = func.CreateBlock();
        var end = func.CreateBlock();
        _currentBlock!.Instructions.Add(new Instruction(Opcode.NullCheck));
        EmitBranchTrue(skip.Id);
        EmitBranch(load.Id);
        _currentBlock = skip;
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
        _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
        EmitBranch(end.Id);
        _currentBlock = load;
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
        GenerateMemberReferenceFromSlot(call, helper, receiverSlot);
        EmitBranch(end.Id);
        _currentBlock = end;
    }

    private void GenerateMemberReferenceFromSlot(BoundCallExpression call, string? helper, int receiverSlot)
    {
        EmitLoadLocal(receiverSlot);
        if (call.Callee is BoundIndexExpression index)
            GenerateIndex(index, receiverSlot);
        else
            GenerateMemberAccess((BoundMemberAccessExpression)call.Callee, receiverSlot);

        if (!call.IsNullConditional)
        {
            EmitMemberReferenceInvocation(call.Arguments, helper);
            return;
        }

        var function = _currentFunction!;
        var nullBlock = function.CreateBlock();
        var invokeBlock = function.CreateBlock();
        var endBlock = function.CreateBlock();
        _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
        _currentBlock.Instructions.Add(new Instruction(Opcode.NullCheck));
        EmitBranchTrue(nullBlock.Id);
        EmitBranch(invokeBlock.Id);

        _currentBlock = nullBlock;
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop)); // duplicated callable
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop)); // callable
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop)); // receiver
        _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
        EmitBranch(endBlock.Id);

        _currentBlock = invokeBlock;
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop)); // duplicated callable
        EmitMemberReferenceInvocation(call.Arguments, helper);
        EmitBranch(endBlock.Id);

        _currentBlock = endBlock;
    }

    private void GenerateOptionalSetReceiverCall(BoundCallExpression call, BoundMemberAccessExpression member, bool hasSpread)
    {
        // s?.method(args): test the actual typed Set receiver before evaluating
        // args. Do not alter generic erased/managed optional chaining.
        GenerateExpression(member.Object);
        var function = _currentFunction!;
        var nullBlock = function.CreateBlock();
        var invokeBlock = function.CreateBlock();
        var endBlock = function.CreateBlock();
        _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
        _currentBlock.Instructions.Add(new Instruction(Opcode.NullCheck));
        EmitBranchTrue(nullBlock.Id);
        EmitBranch(invokeBlock.Id);

        _currentBlock = nullBlock;
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
        _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
        EmitBranch(endBlock.Id);

        _currentBlock = invokeBlock;
        if (hasSpread)
        {
            // The extra receiver produces the member value, leaving the
            // original receiver underneath for the existing Call adapter.
            _currentBlock.Instructions.Add(new Instruction(Opcode.LoadField, 0, 0, member.Member.Name));
            EmitMemberReferenceInvocation(call.Arguments, InvokeSetMemberReference);
        }
        else
        {
            _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
            foreach (var argument in call.Arguments)
                GenerateExpression(argument);
            _currentBlock.Instructions.Add(new Instruction(Opcode.Call, 0, call.Arguments.Count + 1, $"Set::{member.Member.Name}"));
        }
        EmitBranch(endBlock.Id);
        _currentBlock = endBlock;
    }

    private void EmitMemberReferenceInvocation(IReadOnlyList<BoundNode> arguments, string? helper)
    {
        GenerateArgumentArray(arguments);
        _currentBlock!.Instructions.Add(helper is null
            ? new Instruction(Opcode.GCallMember)
            : new Instruction(Opcode.Call, 0, 3, helper));
    }

    private void GenerateSpreadCall(BoundCallExpression call)
    {
        if (call.Callee is BoundVariableExpression { Symbol: FunctionSymbol funcSym })
        {
            var target = funcSym.TargetName ?? funcSym.Name;
            if (_capturesByFunction.TryGetValue(target, out var calleeCaptures))
                EmitClosureCreation(target, calleeCaptures);
            else
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadFunc, 0, 0, target));
        }
        else
        {
            GenerateExpression(call.Callee);
        }

        GenerateArgumentArray(call.Arguments);
        _currentBlock!.Instructions.Add(new Instruction(Opcode.CallDynamicArray));
    }

    private void GenerateArgumentArray(IReadOnlyList<BoundNode> arguments)
    {
        _currentBlock!.Instructions.Add(new Instruction(Opcode.NewArray, 0));
        foreach (var argument in arguments)
        {
            _currentBlock.Instructions.Add(new Instruction(Opcode.Dup));
            if (argument is BoundSpreadExpression spread)
            {
                GenerateExpression(spread.Expression);
                _currentBlock.Instructions.Add(new Instruction(Opcode.ArrayAppendSpread));
            }
            else
            {
                GenerateExpression(argument);
                _currentBlock.Instructions.Add(new Instruction(Opcode.ArrayAppend));
            }
        }
    }

    private void GenerateOptionalCall(BoundCallExpression call)
    {
        // `fn?.(arg())`: evaluate the callee once; do not evaluate arguments
        // if it is nullish. Optional member calls use the resolved property value.
        GenerateExpression(call.Callee);
        var func = _currentFunction!;
        var nullBlock = func.CreateBlock();
        var invokeBlock = func.CreateBlock();
        var endBlock = func.CreateBlock();

        _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
        _currentBlock.Instructions.Add(new Instruction(Opcode.NullCheck));
        EmitBranchTrue(nullBlock.Id);
        EmitBranch(invokeBlock.Id);

        _currentBlock = nullBlock;
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
        // ECMAScript optional chaining produces undefined, not null.
        _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
        EmitBranch(endBlock.Id);

        _currentBlock = invokeBlock;
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
        if (call.Arguments.Any(arg => arg is BoundSpreadExpression))
        {
            GenerateArgumentArray(call.Arguments);
            _currentBlock.Instructions.Add(new Instruction(Opcode.CallDynamicArray));
        }
        else
        {
            for (int i = 0; i < call.Arguments.Count; i++)
                GenerateExpression(call.Arguments[i]);
            _currentBlock.Instructions.Add(new Instruction(Opcode.CallDynamic, call.Arguments.Count));
        }
        EmitBranch(endBlock.Id);

        _currentBlock = endBlock;
    }

    private void GenerateExpressionOrLoadCaptured(BoundNode expression, int? slot)
    {
        if (slot is int captured)
            EmitLoadLocal(captured);
        else
            GenerateExpression(expression);
    }

    // Known Maps address storage by raw key. A generic index remains a
    // runtime dispatch, where the spelling "size" is the native property.
    private void EmitLoadIndex(bool mapStorage) =>
        _currentBlock!.Instructions.Add(mapStorage
            ? new Instruction(Opcode.Call, 0, 2, "Map::get")
            : new Instruction(Opcode.LoadElement));

    private void EmitStoreIndex(bool mapStorage)
    {
        if (mapStorage)
        {
            _currentBlock!.Instructions.Add(new Instruction(Opcode.Call, 0, 3, "Map::set"));
            _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
        }
        else
        {
            _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreElement));
        }
    }

    private void EmitDeleteIndex(bool mapStorage) =>
        _currentBlock!.Instructions.Add(mapStorage
            ? new Instruction(Opcode.Call, 0, 2, "Map::delete")
            : new Instruction(Opcode.DeleteIndex));

    private void GenerateIndex(BoundIndexExpression index, int? objectSlot = null, int? indexSlot = null)
    {
        GenerateExpressionOrLoadCaptured(index.Object, objectSlot);
        if (!index.IsNullConditional)
        {
            GenerateExpressionOrLoadCaptured(index.Index, indexSlot);
            EmitLoadIndex(index.IsMapStorage);
            return;
        }

        var func = _currentFunction!;
        var nullBlock = func.CreateBlock();
        var loadBlock = func.CreateBlock();
        var endBlock = func.CreateBlock();
        _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
        _currentBlock.Instructions.Add(new Instruction(Opcode.NullCheck));
        EmitBranchTrue(nullBlock.Id);
        EmitBranch(loadBlock.Id);

        _currentBlock = nullBlock;
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
        // ECMAScript optional chaining produces undefined, not null.
        _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
        EmitBranch(endBlock.Id);

        _currentBlock = loadBlock;
        _currentBlock.Instructions.Add(new Instruction(Opcode.Pop));
        GenerateExpressionOrLoadCaptured(index.Index, indexSlot);
        EmitLoadIndex(index.IsMapStorage);
        EmitBranch(endBlock.Id);
        _currentBlock = endBlock;
    }

    private void GenerateNew(BoundNewExpression newExpr)
    {
        var className = GetClassName(newExpr.ConstructedType);

        for (int i = 0; i < newExpr.Arguments.Count; i++)
            GenerateExpression(newExpr.Arguments[i]);

        _currentBlock!.Instructions.Add(new Instruction(Opcode.NewObject, 0, newExpr.Arguments.Count, className));
    }

    private void GenerateObjectLiteral(BoundObjectLiteralExpression objLit)
    {
        _currentBlock!.Instructions.Add(new Instruction(Opcode.NewObject, 0, 0, "Object"));

        foreach (var prop in objLit.Properties)
        {
            _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
            if (prop.IsSpread)
            {
                GenerateExpression(prop.Value);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.CopyObjectFields));
            }
            else if (prop.IsComputed)
            {
                // STORE_ELEMENT takes target, key, value. Evaluate the key before
                // the value, and each exactly once, matching object-literal
                // evaluation order in JavaScript.
                GenerateExpression(prop.ComputedKey!);
                GenerateExpression(prop.Value);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreElement));
            }
            else
            {
                GenerateExpression(prop.Value);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, prop.Key));
            }
        }
    }

    private void GenerateAssignment(BoundAssignmentExpression assign)
    {
        // The Binder shares this node only when it desugars a compound
        // assignment. Textually identical operands in a plain assignment
        // are bound independently and must retain their own evaluation.
        var compound = assign.Value is BoundBinaryExpression bin &&
            ReferenceEquals(bin.Left, assign.Target) &&
            bin.Operator is TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash
                or TokenKind.Percent or TokenKind.StarStar or TokenKind.Ampersand or TokenKind.Pipe
                or TokenKind.Caret or TokenKind.ShiftLeft or TokenKind.ShiftRight
            ? bin
            : null;

        if (assign.Target is BoundMemberAccessExpression memberAccess &&
            memberAccess.Member is FieldSymbol or PropertySymbol)
        {
            if (memberAccess.Member is FieldSymbol { IsStatic: true } staticField)
            {
                GenerateExpression(assign.Value);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
                _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreGlobal, 0, 0,
                    staticField.RuntimeName ?? $"$static:{staticField.Name}"));
                return;
            }

            if (memberAccess.Member is PropertySymbol prop && prop.HasSetter)
            {
                // Ciclo 8: el Call al setter desapila receptor + valor y apila
                // Void, así que un Dup pelado antes del Call dejaría [obj, Void].
                // Se guarda una copia del valor en un temporal, se descarta el
                // Void con Pop y se recarga el valor asignado como valor de la
                // expresión. Receptor y valor se evalúan una sola vez.
                // Ciclo 9 (V, opción (b), §2.2): el setter se genera con el
                // parámetro fantasma `this` en el slot 0 y su parámetro en
                // el 1; el receptor ficticio `Void` ocupa el slot 0 cuando
                // el acceso es estático, y el `argCount` pasa a ser 2 en
                // ambos caminos.
                int tmpVal = _tempCounter++;
                if (!prop.IsStatic && compound is not null)
                {
                    int receiverSlot = _tempCounter++;
                    GenerateExpression(memberAccess.Object);
                    EmitStoreLocal(receiverSlot);
                    EmitLoadLocal(receiverSlot);
                    GenerateBinary(compound, () => GenerateMemberAccess(memberAccess, receiverSlot));
                }
                else
                {
                    if (!prop.IsStatic)
                        GenerateExpression(memberAccess.Object);
                    else
                        _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
                    GenerateExpression(assign.Value);
                }
                _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
                EmitStoreLocal(tmpVal);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.Call, 0, 2,
                    prop.SetterName ?? $"{prop.DeclaringClassName}::set:{prop.Name}"));
                _currentBlock!.Instructions.Add(new Instruction(Opcode.Pop));
                EmitLoadLocal(tmpVal);
                return;
            }

            // Ciclo 8 (corrección al plan §2.2, ver implementation-c.md):
            // StoreField desapila valor y objeto, así que un Dup pelado sobre
            // [obj, val, val] haría que el Store consumiera el valor duplicado
            // como si fuera el objeto. Se guarda el valor en un temporal y se
            // reconstruye [val, obj, val] antes del Store (mismo patrón que
            // GenerateMemberIncrement del ciclo 7). Objeto y valor se evalúan
            // una sola vez, en el mismo orden que antes.
            int tmpMemberObj = _tempCounter++;
            int tmpMemberVal = _tempCounter++;
            GenerateExpression(memberAccess.Object);
            EmitStoreLocal(tmpMemberObj);
            if (compound is not null)
                GenerateBinary(compound, () => GenerateMemberAccess(memberAccess, tmpMemberObj));
            else
                GenerateExpression(assign.Value);
            EmitStoreLocal(tmpMemberVal);
            EmitLoadLocal(tmpMemberVal);
            EmitLoadLocal(tmpMemberObj);
            EmitLoadLocal(tmpMemberVal);
            var runtimeName = memberAccess.Member is FieldSymbol field
                ? field.RuntimeName ?? field.Name
                : memberAccess.Member.Name;
            _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, runtimeName));
        }
        else if (assign.Target is BoundIndexExpression indexTarget)
        {
            // Ciclo 8 (corrección al plan §2.2, ver implementation-c.md):
            // StoreElement desapila valor, índice y objetivo; un Dup pelado
            // sobre [obj, idx, val, val] consumiría el duplicado como
            // objetivo. Se reconstruye [val, obj, idx, val] con temporales
            // (mismo patrón que GenerateIndexIncrement del ciclo 7).
            int tmpIndexObj = _tempCounter++;
            int tmpIndexIdx = _tempCounter++;
            int tmpIndexVal = _tempCounter++;
            GenerateExpression(indexTarget.Object);
            EmitStoreLocal(tmpIndexObj);
            GenerateExpression(indexTarget.Index);
            EmitStoreLocal(tmpIndexIdx);
            if (compound is not null)
                GenerateBinary(compound, () => GenerateIndex(indexTarget, tmpIndexObj, tmpIndexIdx));
            else
                GenerateExpression(assign.Value);
            EmitStoreLocal(tmpIndexVal);
            EmitLoadLocal(tmpIndexVal);
            EmitLoadLocal(tmpIndexObj);
            EmitLoadLocal(tmpIndexIdx);
            EmitLoadLocal(tmpIndexVal);
            EmitStoreIndex(indexTarget.IsMapStorage);
        }
        else if (assign.Target is BoundVariableExpression boxedTarget && IsBoxedSymbol(boxedTarget.Symbol))
        {
            // Ciclo 8 (corrección al plan §2.2, ver implementation-c.md):
            // igual que el camino miembro: StoreField desapila valor y
            // objeto (la caja); se reconstruye [val, box, val].
            int tmpBoxSlot = _tempCounter++;
            int tmpBoxVal = _tempCounter++;
            EmitLoadLocal(GetBoxSlot(boxedTarget.Symbol.Name));
            EmitStoreLocal(tmpBoxSlot);
            GenerateExpression(assign.Value);
            EmitStoreLocal(tmpBoxVal);
            EmitLoadLocal(tmpBoxVal);
            EmitLoadLocal(tmpBoxSlot);
            EmitLoadLocal(tmpBoxVal);
            _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, "v"));
        }
        else
        {
            GenerateExpression(assign.Value);

            if (assign.Target is BoundVariableExpression varExpr)
            {
                // Ciclo 8: la asignación vale el valor asignado (neto +1); el
                // Dup conserva una copia a través del Store, que consume la
                // de arriba.
                // Ciclo 9 (§8.1): el Dup va solo dentro de cada rama con
                // Store. Sin variable reconocida (objetivo inválido: función,
                // clase, ...) no hay Store y el valor ya queda como neto +1;
                // un Dup antes de la dispatch dejaría neto +2 y filtraría un
                // slot por asignación en bucle.
                if (varExpr.Symbol is LocalSymbol local)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
                    if (IsModuleGlobalSymbol(local))
                        _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreGlobal, 0, 0, GetModuleGlobalKey(local)));
                    else
                        _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreLocal, GetLocalIndex(local)));
                }
                else if (varExpr.Symbol is ParameterSymbol param)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreLocal, GetParameterIndex(param)));
                }
                // Cualquier otro símbolo (FunctionSymbol, ClassSymbol, ...):
                // sin Store y sin Dup. El valor ya queda como neto +1, que es
                // el valor de la asignación; en posición de sentencia lo
                // equilibra el Pop de GenerateStatement (ciclo 8, §2.4a).
            }
        }
    }

    private static bool CanEmitScopedDestructuring(BoundDestructuringAssignmentExpression assign) =>
        assign.ScopedSteps is not null && assign.Temporaries.Count == 1 &&
        assign.Assignments.All(a => a.Target is BoundVariableExpression
            { Symbol: LocalSymbol { IsModuleScoped: false } or ParameterSymbol } target && !IsBoxedSymbol(target.Symbol));

    private void GenerateDestructuringAssignment(BoundDestructuringAssignmentExpression assign)
    {
        foreach (var temporary in assign.Temporaries)
            GenerateVariableDeclaration(temporary);

        if (CanEmitScopedDestructuring(assign))
        {
            foreach (var step in assign.ScopedSteps!)
            {
                if (step is BoundVariableDeclaration capture)
                    GenerateVariableDeclaration(capture);
                else
                    EmitDestructuringWrite((BoundAssignmentExpression)step);
            }
        }
        else
        {
            foreach (var assignment in assign.Assignments)
                EmitDestructuringWrite(assignment);
        }

        GenerateExpression(assign.Result);
    }

    private void EmitDestructuringWrite(BoundAssignmentExpression assignment)
    {
        GenerateAssignment(assignment);
        // Each inner assignment leaves its assigned value; only the RHS is the
        // value of the whole destructuring expression.
        _currentBlock!.Instructions.Add(new Instruction(Opcode.Pop));
    }

    private void GenerateMemberAccess(BoundMemberAccessExpression member, int? objectSlot = null)
    {
        // Ciclo 10 (W, W-8, Rev 2 R1): el método como valor es su función.
        // El receptor NO se emite: la llamada por valor antepone el receptor
        // `Void` en `InvokeCallable`. Solo cuando se conoce la clase
        // declarante; con receptor de tipo interfaz (DeclaringClassName ==
        // null) se mantiene el comportamiento actual. Y solo en accesos NO
        // opcionales: este atajo no se aplica a accesos con `?.`.
        // Consecuencia: `C?.f` como valor deja el valor-clase (W-7) en vez
        // del método; arreglar ese caso es otro ciclo.
        bool nativeSetMember = IsNativeSetMember(member);
        bool nativeStringMember = member.Member is MethodSymbol { DeclaringClassName: "String" } &&
            IsStringReceiver(member.Object.Type);
        if (member.Member is MethodSymbol methodSym && methodSym.DeclaringClassName != null
            && !member.IsNullConditional && !nativeSetMember && !nativeStringMember)
        {
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadFunc, 0, 0,
                $"{methodSym.DeclaringClassName}::{methodSym.RuntimeName ?? methodSym.Name}"));
            return;
        }

        if (member.Member is FieldSymbol { IsStatic: true } staticField)
        {
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadGlobal, 0, 0,
                staticField.RuntimeName ?? $"$static:{staticField.Name}"));
            return;
        }

        if (member.Member is PropertySymbol prop && prop.HasGetter)
        {
            // Ciclo 9 (V, opción (b), §2.2): el getter se genera con el
            // parámetro fantasma `this` en el slot 0; el receptor ficticio
            // `Void` lo ocupa cuando el acceso es estático. Un getter no
            // tiene parámetros explícitos y ningún builtin tiene accessors,
            // así que el ficticio es incondicional aquí y el `argCount` pasa
            // a ser 1 en ambos caminos.
            if (!prop.IsStatic)
                GenerateExpressionOrLoadCaptured(member.Object, objectSlot);
            else
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
            _currentBlock!.Instructions.Add(new Instruction(Opcode.Call, 0, 1,
                prop.GetterName ?? $"{prop.DeclaringClassName}::get:{prop.Name}"));
            return;
        }

        GenerateExpressionOrLoadCaptured(member.Object, objectSlot);

        if (IsStructuralRegexTest(member))
        {
            // Structural shape only authorizes asking the VM. Only a nominal
            // native receiver produces test; ordinary receivers keep the
            // preexisting structural method-value result.
            _currentBlock!.Instructions.Add(new Instruction(Opcode.Call, 0, 1, ReadTypedRegexTestReference));
            return;
        }

        if (member.Member is not (FieldSymbol or PropertySymbol) && !nativeSetMember && !nativeStringMember)
            return;

        if (member.IsNullConditional)
        {
            // obj?.field — yield undefined without reading the field when obj is nullish.
            var func = _currentFunction!;
            var nullBlock = func.CreateBlock();
            var loadBlock = func.CreateBlock();
            var endBlock = func.CreateBlock();

            _currentBlock!.Instructions.Add(new Instruction(Opcode.Dup));
            _currentBlock.Instructions.Add(new Instruction(Opcode.NullCheck));
            // NullCheck peeks; the boolean sits above the duplicated object.
            EmitBranchTrue(nullBlock.Id);
            EmitBranch(loadBlock.Id);

            _currentBlock = nullBlock;
            _currentBlock.Instructions.Add(new Instruction(Opcode.Pop)); // duplicated obj
            _currentBlock.Instructions.Add(new Instruction(Opcode.Pop)); // original obj
            _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
            EmitBranch(endBlock.Id);

            _currentBlock = loadBlock;
            _currentBlock.Instructions.Add(new Instruction(Opcode.Pop)); // duplicated obj
            _currentBlock.Instructions.Add(new Instruction(Opcode.LoadField, 0, 0, GetRuntimeFieldName(member.Member)));
            EmitBranch(endBlock.Id);

            _currentBlock = endBlock;
            return;
        }

        _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadField, 0, 0, GetRuntimeFieldName(member.Member)));
    }

    private static string GetRuntimeFieldName(Symbol member) => member switch
    {
        FieldSymbol field => field.RuntimeName ?? field.Name,
        PropertySymbol property => property.RuntimeName ?? property.Name,
        _ => member.Name
    };

    private string GetClassName(TsType type)
    {
        return type switch
        {
            TsClassType cls => cls.Name,
            TsNullableType nullable => GetClassName(nullable.ElementType),
            TsGenericType generic => GetClassName(generic.Definition),
            TsMapType => "Map",
            TsSetType => "Set",
            _ => "Unknown"
        };
    }

    // Emit helpers
    private void EmitLoadConst(int value) =>
        _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_I32, value));

    private void EmitLoadLocal(int index) =>
        _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadLocal, index));

    private void EmitStoreLocal(int index) =>
        _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreLocal, index));

    private void EmitLoadArg(int index) =>
        _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadArg, index));

    private void EmitUnboxedReceiver(BoundReceiver? receiver)
    {
        switch (receiver?.Kind)
        {
            case BoundReceiverKind.OrdinaryInvocation:
            case BoundReceiverKind.InstanceMethod:
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadInvocationReceiver));
                break;
            case BoundReceiverKind.Absent:
            case BoundReceiverKind.ForbiddenStaticField:
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
                break;
            default:
                EmitLoadThis(); // Class phantom slot, including legacy BoundThis nodes.
                break;
        }
    }

    private void EmitBoundReceiver(BoundReceiver? receiver)
    {
        string? key = receiver == null ? "this" : receiver.Symbol?.Name;
        if (key != null && _boxSlots.TryGetValue(key, out int slot))
        {
            EmitLoadLocal(slot);
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadField, 0, 0, "v"));
        }
        else
        {
            EmitUnboxedReceiver(receiver);
        }
    }

    private void EmitOwnedReceiverBox(BoundReceiver? receiver)
    {
        if (receiver?.Symbol is not { IsCaptured: true } symbol)
            return;
        int slot = _tempCounter++;
        _currentBlock!.Instructions.Add(new Instruction(Opcode.NewObject, 0, 0, "$Box"));
        _currentBlock.Instructions.Add(new Instruction(Opcode.Dup));
        EmitUnboxedReceiver(receiver);
        _currentBlock.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, "v"));
        EmitStoreLocal(slot);
        _boxSlots[symbol.Name] = slot;
    }

    private void EmitSuperReceiver(BoundSuperExpression expression)
    {
        if (expression.IsLexicalReceiver)
            EmitBoundReceiver(expression.Receiver);
        else
            EmitLoadThis();
    }

    private void EmitLoadThis() =>
        _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadThis));

    private void EmitStoreLocal(LocalSymbol local) =>
        EmitStoreLocal(GetLocalIndex(local));

    private void EmitReturn() =>
        _currentBlock!.Instructions.Add(new Instruction(_currentFunction!.UsesGeneratorRegions ? Opcode.GReturn : Opcode.Return));

    private void EmitReturnVoid()
    {
        if (_currentFunction!.UsesGeneratorRegions)
        {
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_Void));
            EmitReturn();
        }
        else
            _currentBlock!.Instructions.Add(new Instruction(Opcode.ReturnVoid));
    }

    private void EmitBranch(int targetLabel)
    {
        var function = _currentFunction!;
        var block = _currentBlock!;
        if (function.UsesGeneratorRegions)
        {
            var from = block.GeneratorOwner;
            var to = function.Blocks[targetLabel].GeneratorOwner;
            int exits = 0;
            for (; !ReferenceEquals(from, to); exits++)
                from = from is null
                    ? throw new InvalidOperationException("Branch cannot enter a generator region")
                    : from.Parent;
            if (exits > 0)
            {
                block.Instructions.Add(new Instruction(Opcode.GTransfer, targetLabel, exits));
                return;
            }
        }
        block.Instructions.Add(new Instruction(Opcode.Branch, targetLabel));
    }

    private void EmitBranchTrue(int targetLabel) =>
        _currentBlock!.Instructions.Add(new Instruction(Opcode.BranchTrue, targetLabel));

    private void EmitBranchFalse(int targetLabel) =>
        _currentBlock!.Instructions.Add(new Instruction(Opcode.BranchFalse, targetLabel));

    private void EmitNop() =>
        _currentBlock!.Instructions.Add(new Instruction(Opcode.Nop));

    private void EmitAwait() =>
        _currentBlock!.Instructions.Add(new Instruction(Opcode.Await));

    private void EmitThrow() =>
        _currentBlock!.Instructions.Add(new Instruction(Opcode.Throw));

    public static string GetModuleInitializerName(string moduleName) =>
        $"$module_init_{ToSafeName(moduleName)}";

    private static bool IsModuleGlobalSymbol(LocalSymbol local) =>
        local.IsModuleScoped && !string.IsNullOrWhiteSpace(local.ModuleName);

    private static string GetModuleGlobalKey(LocalSymbol local)
    {
        var moduleName = string.IsNullOrWhiteSpace(local.ModuleName) ? "module" : local.ModuleName!;
        var runtimeName = string.IsNullOrWhiteSpace(local.RuntimeName) ? local.Name : local.RuntimeName!;
        return $"{moduleName}::{runtimeName}";
    }

    private static string ToSafeName(string value)
    {
        var safe = new string(value.Select(ch =>
            char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "module" : safe;
    }

    private int GetLocalIndex(LocalSymbol local)
    {
        if (!_localMap.TryGetValue(local.Name, out int index))
        {
            index = _tempCounter++;
            _localMap[local.Name] = index;
        }
        return index;
    }

    private int GetParameterIndex(ParameterSymbol param)
    {
        return _currentFunction!.Parameters.FindIndex(p => p.Name == param.Name);
    }

    private static Opcode InferBinaryOpcode(TsType type, TokenKind op)
    {
        if (type == TsType.Int32) return op switch
        {
            TokenKind.Plus => Opcode.Add_I32,
            TokenKind.Minus => Opcode.Sub_I32,
            TokenKind.Star => Opcode.Mul_I32,
            TokenKind.Slash => Opcode.Div_I32,
            TokenKind.Percent => Opcode.Mod_I32,
            TokenKind.DoubleEquals or TokenKind.TripleEquals => Opcode.CmpEq_I32,
            TokenKind.NotEquals or TokenKind.StrictNotEquals => Opcode.CmpNe_I32,
            TokenKind.LessThan => Opcode.CmpLt_I32,
            TokenKind.LessOrEqual => Opcode.CmpLe_I32,
            TokenKind.GreaterThan => Opcode.CmpGt_I32,
            TokenKind.GreaterOrEqual => Opcode.CmpGe_I32,
            TokenKind.Ampersand => Opcode.And_I32,
            TokenKind.Pipe => Opcode.Or_I32,
            TokenKind.Caret => Opcode.Xor_I32,
            TokenKind.ShiftLeft => Opcode.Shl_I32,
            TokenKind.ShiftRight => Opcode.Shr_I32,
            TokenKind.ShiftRightUnsigned => Opcode.Shr_U32,
            TokenKind.StarStar => Opcode.Pow_F64,
            _ => Opcode.Nop
        };

        if (type == TsType.Int64) return op switch
        {
            TokenKind.Plus => Opcode.Add_I64,
            TokenKind.Minus => Opcode.Sub_I64,
            TokenKind.Star => Opcode.Mul_I64,
            TokenKind.Slash => Opcode.Div_I64,
            TokenKind.Percent => Opcode.Mod_I64,
            TokenKind.DoubleEquals or TokenKind.TripleEquals => Opcode.CmpEq_I64,
            TokenKind.NotEquals or TokenKind.StrictNotEquals => Opcode.CmpNe_I64,
            TokenKind.LessThan => Opcode.CmpLt_I64,
            TokenKind.LessOrEqual => Opcode.CmpLe_I64,
            TokenKind.GreaterThan => Opcode.CmpGt_I64,
            TokenKind.GreaterOrEqual => Opcode.CmpGe_I64,
            TokenKind.Ampersand => Opcode.And_I64,
            TokenKind.Pipe => Opcode.Or_I64,
            TokenKind.Caret => Opcode.Xor_I64,
            TokenKind.ShiftLeft => Opcode.Shl_I64,
            TokenKind.ShiftRight => Opcode.Shr_I64,
            TokenKind.ShiftRightUnsigned => Opcode.Shr_U32,
            TokenKind.StarStar => Opcode.Pow_F64,
            _ => Opcode.Nop
        };

        if (type == TsType.UInt64 || type == TsType.BigInt) return op switch
        {
            TokenKind.Plus => Opcode.Add_U64,
            TokenKind.Minus => Opcode.Sub_U64,
            TokenKind.Star => Opcode.Mul_U64,
            TokenKind.Slash => Opcode.Div_U64,
            TokenKind.Percent => Opcode.Mod_U64,
            TokenKind.DoubleEquals or TokenKind.TripleEquals => Opcode.CmpEq_U64,
            TokenKind.NotEquals or TokenKind.StrictNotEquals => Opcode.CmpNe_U64,
            TokenKind.LessThan => Opcode.CmpLt_U64,
            TokenKind.LessOrEqual => Opcode.CmpLe_U64,
            TokenKind.GreaterThan => Opcode.CmpGt_U64,
            TokenKind.GreaterOrEqual => Opcode.CmpGe_U64,
            TokenKind.Ampersand => Opcode.And_U64,
            TokenKind.Pipe => Opcode.Or_U64,
            TokenKind.Caret => Opcode.Xor_U64,
            TokenKind.ShiftLeft => Opcode.Shl_U64,
            TokenKind.ShiftRight => Opcode.Shr_U64,
            TokenKind.ShiftRightUnsigned => Opcode.Shr_U32,
            TokenKind.StarStar => Opcode.Pow_F64,
            _ => Opcode.Nop
        };

        if (type == TsType.Float64 || type == TsType.Number) return op switch
        {
            TokenKind.Plus => Opcode.Add_F64,
            TokenKind.Minus => Opcode.Sub_F64,
            TokenKind.Star => Opcode.Mul_F64,
            TokenKind.Slash => Opcode.Div_F64,
            TokenKind.Percent => Opcode.Mod_F64,
            TokenKind.DoubleEquals or TokenKind.TripleEquals => Opcode.CmpEq_F64,
            TokenKind.NotEquals or TokenKind.StrictNotEquals => Opcode.CmpNe_F64,
            TokenKind.LessThan => Opcode.CmpLt_F64,
            TokenKind.LessOrEqual => Opcode.CmpLe_F64,
            TokenKind.GreaterThan => Opcode.CmpGt_F64,
            TokenKind.GreaterOrEqual => Opcode.CmpGe_F64,
            TokenKind.Ampersand => Opcode.And_I32,
            TokenKind.Pipe => Opcode.Or_I32,
            TokenKind.Caret => Opcode.Xor_I32,
            TokenKind.ShiftLeft => Opcode.Shl_I32,
            TokenKind.ShiftRight => Opcode.Shr_I32,
            TokenKind.ShiftRightUnsigned => Opcode.Shr_U32,
            TokenKind.StarStar => Opcode.Pow_F64,
            _ => Opcode.Nop
        };

        if (type == TsType.Float32) return op switch
        {
            TokenKind.Plus => Opcode.Add_F32,
            TokenKind.Minus => Opcode.Sub_F32,
            TokenKind.Star => Opcode.Mul_F32,
            TokenKind.Slash => Opcode.Div_F32,
            TokenKind.DoubleEquals or TokenKind.TripleEquals => Opcode.CmpEq_F32,
            TokenKind.NotEquals or TokenKind.StrictNotEquals => Opcode.CmpNe_F32,
            TokenKind.LessThan => Opcode.CmpLt_F32,
            TokenKind.LessOrEqual => Opcode.CmpLe_F32,
            TokenKind.GreaterThan => Opcode.CmpGt_F32,
            TokenKind.GreaterOrEqual => Opcode.CmpGe_F32,
            TokenKind.StarStar => Opcode.Pow_F64,
            _ => Opcode.Nop
        };

        if (type == TsType.Decimal) return op switch
        {
            TokenKind.Plus => Opcode.Add_Decimal,
            TokenKind.Minus => Opcode.Sub_Decimal,
            TokenKind.Star => Opcode.Mul_Decimal,
            TokenKind.Slash => Opcode.Div_Decimal,
            TokenKind.Percent => Opcode.Mod_Decimal,
            TokenKind.DoubleEquals or TokenKind.TripleEquals => Opcode.CmpEq_Decimal,
            TokenKind.NotEquals or TokenKind.StrictNotEquals => Opcode.CmpNe_Decimal,
            TokenKind.LessThan => Opcode.CmpLt_Decimal,
            TokenKind.LessOrEqual => Opcode.CmpLe_Decimal,
            TokenKind.GreaterThan => Opcode.CmpGt_Decimal,
            TokenKind.GreaterOrEqual => Opcode.CmpGe_Decimal,
            _ => Opcode.Nop
        };

        if (type == TsType.Bool) return op switch
        {
            TokenKind.AmpersandAmpersand => Opcode.And_Bool,
            TokenKind.PipePipe => Opcode.Or_Bool,
            TokenKind.DoubleEquals => Opcode.CmpEq_Any,
            TokenKind.NotEquals => Opcode.CmpNe_Any,
            TokenKind.TripleEquals => Opcode.CmpStrictEq_Any,
            TokenKind.StrictNotEquals => Opcode.CmpStrictNe_Any,
            _ => Opcode.Nop
        };

        if (type == TsType.String) return op switch
        {
            TokenKind.Plus => Opcode.ConcatString,
            TokenKind.DoubleEquals => Opcode.CmpEq_Any,
            TokenKind.NotEquals => Opcode.CmpNe_Any,
            TokenKind.TripleEquals => Opcode.CmpStrictEq_Any,
            TokenKind.StrictNotEquals => Opcode.CmpStrictNe_Any,
            _ => Opcode.Nop
        };

        // Reference-like and dynamic operands (nullable, objects, null, any)
        // still support equality via generic value comparison.
        return op switch
        {
            TokenKind.DoubleEquals => Opcode.CmpEq_Any,
            TokenKind.NotEquals => Opcode.CmpNe_Any,
            TokenKind.TripleEquals => Opcode.CmpStrictEq_Any,
            TokenKind.StrictNotEquals => Opcode.CmpStrictNe_Any,
            TokenKind.StarStar => Opcode.Pow_F64,
            _ => Opcode.Nop
        };
    }
}
