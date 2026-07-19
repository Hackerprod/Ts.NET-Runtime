using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;

namespace TypeSharp.IR;

public sealed class IRGenerator
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
    private readonly HashSet<string> _generatedFunctions = new();

    // funcName -> captured variable names; direct calls to capturing functions
    // must build a closure carrying the current boxes.
    private readonly Dictionary<string, List<string>> _capturesByFunction = new();
    private BoundFunctionDeclaration? _currentDeclaration;
    private readonly Dictionary<Symbol, BoundNode> _moduleConstantInitializers = new();

    public ModuleIR Generate(BoundSourceFile sourceFile)
    {
        var module = new ModuleIR(sourceFile.FileName);
        _moduleConstantInitializers.Clear();

        CollectCaptureInfo(sourceFile);
        CollectModuleConstants(sourceFile);

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
                GenerateClass(module, cls);
                DrainLiftedFunctions(module);
            }
        }

        return module;
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
            BoundArrayLiteralExpression array => array.Elements.All(IsCompileTimeConstant),
            BoundObjectLiteralExpression obj => obj.Properties.All(property => IsCompileTimeConstant(property.Value)),
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
                CollectCaptureInfo(func.Body);
                break;
            case BoundClassDeclaration cls:
                foreach (var member in cls.Members) CollectCaptureInfo(member);
                break;
            case BoundMethodDeclaration method:
                CollectCaptureInfo(method.Body);
                break;
            case BoundConstructorDeclaration ctor:
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
            case BoundWhileStatement whileStmt:
                CollectCaptureInfo(whileStmt.Condition);
                CollectCaptureInfo(whileStmt.Body);
                break;
            case BoundForStatement forStmt:
                if (forStmt.Initializer != null) CollectCaptureInfo(forStmt.Initializer);
                if (forStmt.Condition != null) CollectCaptureInfo(forStmt.Condition);
                if (forStmt.Iterator != null) CollectCaptureInfo(forStmt.Iterator);
                CollectCaptureInfo(forStmt.Body);
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
        while (_liftedFunctions.Count > 0)
        {
            var pending = _liftedFunctions.Dequeue();
            if (!_generatedFunctions.Add(pending.Symbol.Name))
                continue;
            module.AddFunction(GenerateFunction(pending));
        }
    }

    private FunctionIR GenerateFunction(BoundFunctionDeclaration func)
    {
        var parameters = func.Symbol.Parameters
            .Select(p => new ParameterInfo(p.Name, p.Type))
            .ToList();

        var funcIR = new FunctionIR(func.Symbol.Name, func.Symbol.Type, parameters)
        {
            IsAsync = func.Symbol.IsAsync,
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

        return funcIR;
    }

    private bool IsBoxedSymbol(Symbol symbol) => symbol switch
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
        List<ParameterSymbol> explicitParams, BoundNode body, bool isAsync = false)
    {
        var parameters = new List<ParameterInfo>();
        parameters.Add(new ParameterInfo("this", new TsClassType(className)));
        foreach (var p in explicitParams)
            parameters.Add(new ParameterInfo(p.Name, p.Type));

        var qualifiedName = $"{className}::{methodName}";
        var funcIR = new FunctionIR(qualifiedName, returnType, parameters)
        {
            IsAsync = isAsync,
            LocalCount = 0
        };

        _currentFunction = funcIR;
        _tempCounter = 0;
        _localMap = new Dictionary<string, int>();

        var entryBlock = funcIR.CreateBlock();
        _currentBlock = entryBlock;

        _localMap["this"] = 0;
        for (int i = 0; i < explicitParams.Count; i++)
            _localMap[explicitParams[i].Name] = i + 1;
        _tempCounter = 1 + explicitParams.Count;

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
                        ctor.Symbol.Parameters, ctor.Body, ctor.Symbol.IsAsync);
                    module.AddFunction(funcIR);
                    break;
                }
                case BoundMethodDeclaration method:
                {
                    var funcIR = GenerateMethodFunction(
                        cls.Symbol.Name, method.Symbol.Name, method.Symbol.Type,
                        method.Symbol.Parameters, method.Body, method.Symbol.IsAsync);
                    module.AddFunction(funcIR);
                    break;
                }
                case BoundFieldInitializer:
                    break;
            }
        }
    }

    private void GenerateStatement(BoundNode node)
    {
        switch (node)
        {
            case BoundBlockStatement block:
                foreach (var stmt in block.Statements)
                    GenerateStatement(stmt);
                break;

            case BoundVariableDeclaration varDecl:
                GenerateVariableDeclaration(varDecl);
                break;

            case BoundExpressionStatement exprStmt:
                GenerateExpression(exprStmt.Expression);
                // Every non-assignment expression leaves a value on the stack;
                // discard it so statement-position expressions don't leak slots.
                if (exprStmt.Expression is not BoundAssignmentExpression)
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.Pop));
                break;

            case BoundReturnStatement ret:
                GenerateReturn(ret);
                break;

            case BoundIfStatement ifStmt:
                GenerateIf(ifStmt);
                break;

            case BoundWhileStatement whileStmt:
                GenerateWhile(whileStmt);
                break;

            case BoundForStatement forStmt:
                GenerateFor(forStmt);
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

    private void GenerateVariableDeclaration(BoundVariableDeclaration varDecl)
    {
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

    private void GenerateWhile(BoundWhileStatement whileStmt)
    {
        var func = _currentFunction!;
        var conditionBlock = func.CreateBlock();
        var bodyBlock = func.CreateBlock();
        var afterBlock = func.CreateBlock();

        if (!_currentBlock!.EndsInBranch)
            EmitBranch(conditionBlock.Id);

        _currentBlock = conditionBlock;
        GenerateExpression(whileStmt.Condition);
        EmitBranchTrue(bodyBlock.Id);
        EmitBranch(afterBlock.Id);

        _currentBlock = bodyBlock;
        GenerateStatement(whileStmt.Body);
        if (!_currentBlock.EndsInBranch)
            EmitBranch(conditionBlock.Id);

        _currentBlock = afterBlock;
    }

    private void GenerateFor(BoundForStatement forStmt)
    {
        var func = _currentFunction!;
        var conditionBlock = func.CreateBlock();
        var bodyBlock = func.CreateBlock();
        var iteratorBlock = func.CreateBlock();
        var afterBlock = func.CreateBlock();

        if (forStmt.Initializer != null)
            GenerateStatement(forStmt.Initializer);

        if (!_currentBlock!.EndsInBranch)
            EmitBranch(conditionBlock.Id);

        _currentBlock = conditionBlock;
        if (forStmt.Condition != null)
        {
            GenerateExpression(forStmt.Condition);
            EmitBranchTrue(bodyBlock.Id);
        }
        EmitBranch(afterBlock.Id);

        _currentBlock = bodyBlock;
        GenerateStatement(forStmt.Body);
        EmitBranch(iteratorBlock.Id);

        _currentBlock = iteratorBlock;
        if (forStmt.Iterator != null)
            GenerateExpression(forStmt.Iterator);
        EmitBranch(conditionBlock.Id);

        _currentBlock = afterBlock;
    }

    private void GenerateThrow(BoundThrowStatement throwStmt)
    {
        GenerateExpression(throwStmt.Expression);
        EmitThrow();
    }

    private void GenerateTry(BoundTryStatement tryStmt)
    {
        var func = _currentFunction!;
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
            case BoundThisExpression:
                EmitLoadThis();
                break;
            case BoundSuperExpression:
                EmitLoadThis();
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
            case BoundIndexExpression indexExpr:
                GenerateExpression(indexExpr.Object);
                GenerateExpression(indexExpr.Index);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadElement));
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
            case BoundCastExpression cast:
                GenerateExpression(cast.Operand);
                break;
            case BoundTypeofExpression typeofExpr:
                GenerateExpression(typeofExpr.Operand);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.TypeOf));
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
        switch (lit.Type)
        {
            case TsPrimitiveType { IsNumericType: true } p:
                if (lit.Type == TsType.Int32 || lit.Type == TsType.Int16 || lit.Type == TsType.Int8)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_I32, Convert.ToInt32(lit.Value)));
                }
                else if (lit.Type == TsType.Int64)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_I64, 0, 0, Convert.ToInt64(lit.Value)));
                }
                else if (lit.Type == TsType.UInt64 || lit.Type == TsType.BigInt)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_U64, 0, 0, Convert.ToUInt64(lit.Value)));
                }
                else if (lit.Type == TsType.Float32)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadConst_F32, 0, 0, Convert.ToSingle(lit.Value)));
                }
                else if (lit.Type == TsType.Decimal)
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
        if (varExpr.Symbol is LocalSymbol { ConstantInitializer: BoundNode exportedConstant })
        {
            GenerateExpression(exportedConstant);
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
    }

    private void GenerateBinary(BoundBinaryExpression bin)
    {
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

        GenerateExpression(bin.Left);
        GenerateExpression(bin.Right);

        var operandType = bin.Left.Type is TsAnyType ? bin.Right.Type : bin.Left.Type;
        // Dynamic (host) operands carry their width at runtime; compute in the
        // widest integer form so an int64 payload never truncates through i32 ops.
        if ((bin.Left.Type is TsAnyType || bin.Right.Type is TsAnyType) &&
            (operandType == TsType.Int32 || operandType is TsAnyType))
        {
            operandType = TsType.Int64;
        }
        else if (bin.Left.Type.IsNumeric && bin.Right.Type.IsNumeric)
        {
            // Mixed widths compute in the wider representation (int literal
            // against a `number` variable must not truncate to i32 ops).
            operandType = Binder.WiderNumeric(bin.Left.Type, bin.Right.Type);
        }
        var opcode = InferBinaryOpcode(operandType, bin.Operator);
        _currentBlock!.Instructions.Add(new Instruction(opcode));
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
            TokenKind.Tilde => unary.Operand.Type == TsType.Int64 ? Opcode.Not_I64 : Opcode.Not_I32,
            TokenKind.Plus => Opcode.Nop,
            _ => Opcode.Nop
        };
        if (opcode != Opcode.Nop)
            _currentBlock!.Instructions.Add(new Instruction(opcode));
    }

    private void GenerateIncrement(BoundUnaryExpression unary)
    {
        if (unary.Operand is not BoundVariableExpression target ||
            target.Symbol is not (LocalSymbol or ParameterSymbol))
        {
            // Unsupported target: evaluate the operand so the stack stays balanced.
            GenerateExpression(unary.Operand);
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
        var addOp = unary.Operator == TokenKind.PlusPlus
            ? (isInt64 ? Opcode.Add_I64 : isFloat ? Opcode.Add_F64 : Opcode.Add_I32)
            : (isInt64 ? Opcode.Sub_I64 : isFloat ? Opcode.Sub_F64 : Opcode.Sub_I32);

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
        if (call.Callee is BoundMemberAccessExpression memberAccess)
        {
            if (memberAccess.Member is MethodSymbol { DeclaringClassName: null } structuralMethod)
            {
                GenerateExpression(memberAccess.Object);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadField, 0, 0, structuralMethod.Name));
                for (int i = 0; i < call.Arguments.Count; i++)
                    GenerateExpression(call.Arguments[i]);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.CallDynamic, call.Arguments.Count));
                return;
            }

            if (memberAccess.Member is not MethodSymbol)
            {
                GenerateExpression(memberAccess);
                for (int i = 0; i < call.Arguments.Count; i++)
                    GenerateExpression(call.Arguments[i]);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.CallDynamic, call.Arguments.Count));
                return;
            }

            // Static namespace calls (Math.floor, console.log) have no
            // receiver value: skip the object load and pass args only.
            bool isStatic = memberAccess.Object is BoundVariableExpression { Symbol: ClassSymbol };
            if (!isStatic)
                GenerateExpression(memberAccess.Object);
            for (int i = 0; i < call.Arguments.Count; i++)
                GenerateExpression(call.Arguments[i]);

            string funcName = "";
            string className = isStatic
                ? ((ClassSymbol)((BoundVariableExpression)memberAccess.Object).Symbol).Name
                : GetClassName(memberAccess.Object.Type);

            if (memberAccess.Member is MethodSymbol methodSym)
                funcName = $"{methodSym.DeclaringClassName ?? className}::{methodSym.Name}";
            else if (memberAccess.Member is PropertySymbol propSym)
                funcName = $"{className}::{propSym.Name}";

            int totalArgs = (isStatic ? 0 : 1) + call.Arguments.Count;
            _currentBlock!.Instructions.Add(new Instruction(Opcode.Call, 0, totalArgs, funcName));
        }
        else if (call.Callee is BoundSuperExpression superExpr)
        {
            EmitLoadThis();
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
            GenerateExpression(prop.Value);
            _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, prop.Key));
        }
    }

    private void GenerateAssignment(BoundAssignmentExpression assign)
    {
        if (assign.Target is BoundMemberAccessExpression memberAccess &&
            memberAccess.Member is FieldSymbol or PropertySymbol)
        {
            GenerateExpression(memberAccess.Object);
            GenerateExpression(assign.Value);
            _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, memberAccess.Member.Name));
        }
        else if (assign.Target is BoundIndexExpression indexTarget)
        {
            GenerateExpression(indexTarget.Object);
            GenerateExpression(indexTarget.Index);
            GenerateExpression(assign.Value);
            _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreElement));
        }
        else if (assign.Target is BoundVariableExpression boxedTarget && IsBoxedSymbol(boxedTarget.Symbol))
        {
            EmitLoadLocal(GetBoxSlot(boxedTarget.Symbol.Name));
            GenerateExpression(assign.Value);
            _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, "v"));
        }
        else
        {
            GenerateExpression(assign.Value);

            if (assign.Target is BoundVariableExpression varExpr)
            {
                if (varExpr.Symbol is LocalSymbol local)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreLocal, GetLocalIndex(local)));
                }
                else if (varExpr.Symbol is ParameterSymbol param)
                {
                    _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreLocal, GetParameterIndex(param)));
                }
            }
        }
    }

    private void GenerateMemberAccess(BoundMemberAccessExpression member)
    {
        GenerateExpression(member.Object);

        if (member.Member is not (FieldSymbol or PropertySymbol))
            return;

        if (member.IsNullConditional)
        {
            // obj?.field — yield null without touching the field when obj is null.
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
            _currentBlock.Instructions.Add(new Instruction(Opcode.LoadConst_Null));
            EmitBranch(endBlock.Id);

            _currentBlock = loadBlock;
            _currentBlock.Instructions.Add(new Instruction(Opcode.Pop)); // duplicated obj
            _currentBlock.Instructions.Add(new Instruction(Opcode.LoadField, 0, 0, member.Member.Name));
            EmitBranch(endBlock.Id);

            _currentBlock = endBlock;
            return;
        }

        _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadField, 0, 0, member.Member.Name));
    }

    private string GetClassName(TsType type)
    {
        return type switch
        {
            TsClassType cls => cls.Name,
            TsNullableType nullable => GetClassName(nullable.ElementType),
            TsGenericType generic => GetClassName(generic.Definition),
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

    private void EmitLoadThis() =>
        _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadThis));

    private void EmitStoreLocal(LocalSymbol local) =>
        EmitStoreLocal(GetLocalIndex(local));

    private void EmitReturn() =>
        _currentBlock!.Instructions.Add(new Instruction(Opcode.Return));

    private void EmitReturnVoid() =>
        _currentBlock!.Instructions.Add(new Instruction(Opcode.ReturnVoid));

    private void EmitBranch(int targetLabel) =>
        _currentBlock!.Instructions.Add(new Instruction(Opcode.Branch, targetLabel));

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
            TokenKind.DoubleEquals or TokenKind.TripleEquals => Opcode.CmpEq_Any,
            TokenKind.NotEquals or TokenKind.StrictNotEquals => Opcode.CmpNe_Any,
            _ => Opcode.Nop
        };

        if (type == TsType.String) return op switch
        {
            TokenKind.Plus => Opcode.ConcatString,
            TokenKind.DoubleEquals or TokenKind.TripleEquals => Opcode.CmpEq_Any,
            TokenKind.NotEquals or TokenKind.StrictNotEquals => Opcode.CmpNe_Any,
            _ => Opcode.Nop
        };

        // Reference-like and dynamic operands (nullable, objects, null, any)
        // still support equality via generic value comparison.
        return op switch
        {
            TokenKind.DoubleEquals or TokenKind.TripleEquals => Opcode.CmpEq_Any,
            TokenKind.NotEquals or TokenKind.StrictNotEquals => Opcode.CmpNe_Any,
            TokenKind.StarStar => Opcode.Pow_F64,
            _ => Opcode.Nop
        };
    }
}
