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

    public ModuleIR Generate(BoundSourceFile sourceFile)
    {
        var module = new ModuleIR(sourceFile.FileName);

        foreach (var member in sourceFile.Members)
        {
            if (member is BoundFunctionDeclaration func)
            {
                var funcIR = GenerateFunction(func);
                module.AddFunction(funcIR);
            }
            else if (member is BoundClassDeclaration cls)
            {
                GenerateClass(module, cls);
            }
        }

        return module;
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

        _currentFunction = funcIR;
        _tempCounter = 0;
        _localMap = new Dictionary<string, int>();

        var entryBlock = funcIR.CreateBlock();
        _currentBlock = entryBlock;

        foreach (var param in func.Symbol.Parameters)
        {
            int idx = func.Symbol.Parameters.IndexOf(param);
            _localMap[param.Name] = idx;
        }
        _tempCounter = func.Symbol.Parameters.Count;

        GenerateStatement(func.Body);

        funcIR.LocalCount = _tempCounter;

        if (!_currentBlock.EndsInBranch)
        {
            EmitReturnVoid();
        }

        return funcIR;
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

            case BoundFunctionDeclaration func:
                GenerateFunction(func);
                break;

            default:
                EmitNop();
                break;
        }
    }

    private void GenerateVariableDeclaration(BoundVariableDeclaration varDecl)
    {
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

        int condBranchIdx = _currentBlock!.Instructions.Count;
        EmitBranchFalse((elseBlock ?? continuationBlock).Id);

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
            case BoundAwaitExpression awaitExpr:
                GenerateExpression(awaitExpr.Expression);
                EmitAwait();
                break;
            default:
                EmitNop();
                break;
        }
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
                else if (lit.Type == TsType.UInt64)
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
        if (varExpr.Symbol is LocalSymbol local)
        {
            int localIndex = GetLocalIndex(local);
            EmitLoadLocal(localIndex);
        }
        else if (varExpr.Symbol is ParameterSymbol param)
        {
            int paramIndex = GetParameterIndex(param);
            EmitLoadArg(paramIndex);
        }
    }

    private void GenerateBinary(BoundBinaryExpression bin)
    {
        GenerateExpression(bin.Left);
        GenerateExpression(bin.Right);

        var opcode = InferBinaryOpcode(bin.Left.Type, bin.Operator);
        _currentBlock!.Instructions.Add(new Instruction(opcode));
    }

    private void GenerateUnary(BoundUnaryExpression unary)
    {
        GenerateExpression(unary.Operand);

        var opcode = unary.Operator switch
        {
            TokenKind.Minus => unary.Operand.Type == TsType.Int32 ? Opcode.Neg_I32 :
                              unary.Operand.Type == TsType.Int64 ? Opcode.Neg_I64 :
                              unary.Operand.Type == TsType.Float32 ? Opcode.Neg_F32 :
                              unary.Operand.Type == TsType.Decimal ? Opcode.Neg_Decimal : Opcode.Neg_F64,
            TokenKind.Bang => Opcode.Not_Bool,
            _ => Opcode.Nop
        };
        _currentBlock!.Instructions.Add(new Instruction(opcode));
    }

    private void GenerateCall(BoundCallExpression call)
    {
        if (call.Callee is BoundMemberAccessExpression memberAccess)
        {
            GenerateExpression(memberAccess.Object);
            for (int i = 0; i < call.Arguments.Count; i++)
                GenerateExpression(call.Arguments[i]);

            string funcName = "";
            string className = GetClassName(memberAccess.Object.Type);

            if (memberAccess.Member is MethodSymbol methodSym)
                funcName = $"{className}::{methodSym.Name}";
            else if (memberAccess.Member is PropertySymbol propSym)
                funcName = $"{className}::{propSym.Name}";

            int totalArgs = 1 + call.Arguments.Count;
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
        else
        {
            for (int i = 0; i < call.Arguments.Count; i++)
                GenerateExpression(call.Arguments[i]);

            string funcName = "";
            if (call.Callee is BoundVariableExpression varExpr && varExpr.Symbol is FunctionSymbol funcSym)
                funcName = funcSym.Name;
            else if (call.Callee is BoundVariableExpression varExpr2)
                funcName = varExpr2.Symbol.Name;

            int argCount = call.Arguments.Count;
            _currentBlock!.Instructions.Add(new Instruction(Opcode.Call, 0, argCount, funcName));
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
            memberAccess.Member is FieldSymbol field)
        {
            GenerateExpression(memberAccess.Object);
            GenerateExpression(assign.Value);
            _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreField, 0, 0, field.Name));
        }
        else
        {
            GenerateExpression(assign.Value);

            if (assign.Target is BoundVariableExpression varExpr && varExpr.Symbol is LocalSymbol local)
            {
                int localIndex = GetLocalIndex(local);
                _currentBlock!.Instructions.Add(new Instruction(Opcode.StoreLocal, localIndex));
            }
        }
    }

    private void GenerateMemberAccess(BoundMemberAccessExpression member)
    {
        GenerateExpression(member.Object);

        if (member.Member is FieldSymbol field)
        {
            _currentBlock!.Instructions.Add(new Instruction(Opcode.LoadField, 0, 0, field.Name));
        }
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
            TokenKind.DoubleEquals or TokenKind.StrictNotEquals => Opcode.CmpEq_I32,
            TokenKind.NotEquals => Opcode.CmpNe_I32,
            TokenKind.LessThan => Opcode.CmpLt_I32,
            TokenKind.LessOrEqual => Opcode.CmpLe_I32,
            TokenKind.GreaterThan => Opcode.CmpGt_I32,
            TokenKind.GreaterOrEqual => Opcode.CmpGe_I32,
            TokenKind.Ampersand => Opcode.And_I32,
            TokenKind.Pipe => Opcode.Or_I32,
            TokenKind.Caret => Opcode.Xor_I32,
            TokenKind.ShiftLeft => Opcode.Shl_I32,
            TokenKind.ShiftRight => Opcode.Shr_I32,
            _ => Opcode.Nop
        };

        if (type == TsType.Int64) return op switch
        {
            TokenKind.Plus => Opcode.Add_I64,
            TokenKind.Minus => Opcode.Sub_I64,
            TokenKind.Star => Opcode.Mul_I64,
            TokenKind.Slash => Opcode.Div_I64,
            TokenKind.Percent => Opcode.Mod_I64,
            _ => Opcode.Nop
        };

        if (type == TsType.UInt64) return op switch
        {
            TokenKind.Plus => Opcode.Add_U64,
            TokenKind.Minus => Opcode.Sub_U64,
            TokenKind.Star => Opcode.Mul_U64,
            TokenKind.Slash => Opcode.Div_U64,
            TokenKind.Percent => Opcode.Mod_U64,
            _ => Opcode.Nop
        };

        if (type == TsType.Float64) return op switch
        {
            TokenKind.Plus => Opcode.Add_F64,
            TokenKind.Minus => Opcode.Sub_F64,
            TokenKind.Star => Opcode.Mul_F64,
            TokenKind.Slash => Opcode.Div_F64,
            _ => Opcode.Nop
        };

        if (type == TsType.Decimal) return op switch
        {
            TokenKind.Plus => Opcode.Add_Decimal,
            TokenKind.Minus => Opcode.Sub_Decimal,
            TokenKind.Star => Opcode.Mul_Decimal,
            TokenKind.Slash => Opcode.Div_Decimal,
            TokenKind.Percent => Opcode.Mod_Decimal,
            TokenKind.DoubleEquals or TokenKind.StrictNotEquals => Opcode.CmpEq_Decimal,
            TokenKind.NotEquals => Opcode.CmpNe_Decimal,
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
            _ => Opcode.Nop
        };

        if (type == TsType.String && op == TokenKind.Plus)
            return Opcode.ConcatString;

        return Opcode.Nop;
    }
}
