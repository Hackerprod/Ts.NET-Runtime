using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Diagnostics;
using TypeSharp.Syntax.SyntaxTree;

namespace TypeSharp.Semantics.Binder;

public class BoundNode
{
    public BoundNodeKind Kind { get; }
    public TsType Type { get; }

    public BoundNode(BoundNodeKind kind, TsType type)
    {
        Kind = kind;
        Type = type;
    }
}

public enum BoundNodeKind
{
    LiteralExpression,
    VariableExpression,
    BinaryExpression,
    UnaryExpression,
    CallExpression,
    MemberAccessExpression,
    AssignmentExpression,
    ConditionalExpression,
    NewExpression,
    ThisExpression,
    SuperExpression,
    AwaitExpression,
    ArrayLiteralExpression,
    ObjectLiteralExpression,
    IndexExpression,
    ObjectProperty,
    LambdaExpression,
    CastExpression,
    TypeofExpression,

    ExpressionStatement,
    ReturnStatement,
    IfStatement,
    WhileStatement,
    ForStatement,
    BreakStatement,
    ContinueStatement,
    BlockStatement,
    VariableDeclaration,
    ThrowStatement,
    TryStatement,

    FunctionDeclaration,
    MethodDeclaration,
    ConstructorDeclaration,
    FieldInitializer,
    ClassDeclaration,
    InterfaceDeclaration,
    EnumDeclaration,
    ImportDeclaration,
    SourceFile,
}

// Bound expressions
public sealed class BoundLiteralExpression : BoundNode
{
    public object? Value { get; }
    public BoundLiteralExpression(object? value, TsType type) : base(BoundNodeKind.LiteralExpression, type)
    {
        Value = value;
    }
}

public sealed class BoundVariableExpression : BoundNode
{
    public Symbol Symbol { get; }
    public BoundVariableExpression(Symbol symbol) : base(BoundNodeKind.VariableExpression, symbol.Type)
    {
        Symbol = symbol;
    }
}

public sealed class BoundBinaryExpression : BoundNode
{
    public BoundNode Left { get; }
    public TokenKind Operator { get; }
    public BoundNode Right { get; }

    public BoundBinaryExpression(BoundNode left, TokenKind op, BoundNode right, TsType resultType)
        : base(BoundNodeKind.BinaryExpression, resultType)
    {
        Left = left;
        Operator = op;
        Right = right;
    }
}

public sealed class BoundUnaryExpression : BoundNode
{
    public TokenKind Operator { get; }
    public BoundNode Operand { get; }
    public bool IsPrefix { get; }

    public BoundUnaryExpression(TokenKind op, BoundNode operand, bool isPrefix, TsType type)
        : base(BoundNodeKind.UnaryExpression, type)
    {
        Operator = op;
        Operand = operand;
        IsPrefix = isPrefix;
    }
}

public sealed class BoundCallExpression : BoundNode
{
    public BoundNode Callee { get; }
    public List<BoundNode> Arguments { get; }

    public BoundCallExpression(BoundNode callee, List<BoundNode> arguments, TsType returnType)
        : base(BoundNodeKind.CallExpression, returnType)
    {
        Callee = callee;
        Arguments = arguments;
    }
}

public sealed class BoundMemberAccessExpression : BoundNode
{
    public BoundNode Object { get; }
    public Symbol Member { get; }
    public bool IsNullConditional { get; }

    public BoundMemberAccessExpression(BoundNode obj, Symbol member, bool isNullConditional = false, TsType? resultType = null)
        : base(BoundNodeKind.MemberAccessExpression, resultType ?? member.Type)
    {
        Object = obj;
        Member = member;
        IsNullConditional = isNullConditional;
    }
}

// An inline lambda: carries its lifted function declaration; evaluating the
// expression yields a first-class function value referring to it.
public sealed class BoundLambdaExpression : BoundNode
{
    public BoundFunctionDeclaration Function { get; }

    public BoundLambdaExpression(BoundFunctionDeclaration function, TsType type)
        : base(BoundNodeKind.LambdaExpression, type)
    {
        Function = function;
    }
}

// `typeof expr` needing runtime inspection (dynamic operand).
public sealed class BoundTypeofExpression : BoundNode
{
    public BoundNode Operand { get; }

    public BoundTypeofExpression(BoundNode operand)
        : base(BoundNodeKind.TypeofExpression, TsType.String)
    {
        Operand = operand;
    }
}

// `expr as T` — pure static retype; no runtime conversion is emitted.
public sealed class BoundCastExpression : BoundNode
{
    public BoundNode Operand { get; }

    public BoundCastExpression(BoundNode operand, TsType type)
        : base(BoundNodeKind.CastExpression, type)
    {
        Operand = operand;
    }
}

// `new Array(n)` / `Array(n)` — sized array construction.
public sealed class BoundArrayConstructionExpression : BoundNode
{
    public List<BoundNode> Arguments { get; }

    public BoundArrayConstructionExpression(List<BoundNode> arguments, TsType type)
        : base(BoundNodeKind.ArrayLiteralExpression, type)
    {
        Arguments = arguments;
    }
}

public sealed class BoundArrayLiteralExpression : BoundNode
{
    public List<BoundNode> Elements { get; }

    public BoundArrayLiteralExpression(List<BoundNode> elements, TsType type)
        : base(BoundNodeKind.ArrayLiteralExpression, type)
    {
        Elements = elements;
    }
}

public sealed class BoundAssignmentExpression : BoundNode
{
    public BoundNode Target { get; }
    public BoundNode Value { get; }

    public BoundAssignmentExpression(BoundNode target, BoundNode value)
        : base(BoundNodeKind.AssignmentExpression, value.Type)
    {
        Target = target;
        Value = value;
    }
}

public sealed class BoundConditionalExpression : BoundNode
{
    public BoundNode Condition { get; }
    public BoundNode WhenTrue { get; }
    public BoundNode WhenFalse { get; }

    public BoundConditionalExpression(BoundNode condition, BoundNode whenTrue, BoundNode whenFalse, TsType type)
        : base(BoundNodeKind.ConditionalExpression, type)
    {
        Condition = condition;
        WhenTrue = whenTrue;
        WhenFalse = whenFalse;
    }
}

public sealed class BoundNewExpression : BoundNode
{
    public TsType ConstructedType { get; }
    public List<BoundNode> Arguments { get; }

    public BoundNewExpression(TsType type, List<BoundNode> arguments)
        : base(BoundNodeKind.NewExpression, type)
    {
        ConstructedType = type;
        Arguments = arguments;
    }
}

public sealed class BoundThisExpression : BoundNode
{
    public BoundThisExpression(TsType type) : base(BoundNodeKind.ThisExpression, type) { }
}

public sealed class BoundSuperExpression : BoundNode
{
    public TsClassType BaseClass { get; }

    public BoundSuperExpression(TsClassType baseClass)
        : base(BoundNodeKind.SuperExpression, baseClass)
    {
        BaseClass = baseClass;
    }
}

public sealed class BoundAwaitExpression : BoundNode
{
    public BoundNode Expression { get; }

    public BoundAwaitExpression(BoundNode expression, TsType elementType)
        : base(BoundNodeKind.AwaitExpression, elementType)
    {
        Expression = expression;
    }
}

public sealed class BoundObjectPropertyNode : BoundNode
{
    public string Key { get; }
    public BoundNode Value { get; }
    public bool IsSpread { get; }

    public BoundObjectPropertyNode(string key, BoundNode value, TsType type, bool isSpread = false)
        : base(BoundNodeKind.ObjectProperty, type)
    {
        Key = key;
        Value = value;
        IsSpread = isSpread;
    }
}

public sealed class BoundObjectLiteralExpression : BoundNode
{
    public List<BoundObjectPropertyNode> Properties { get; }

    public BoundObjectLiteralExpression(List<BoundObjectPropertyNode> properties, TsType type)
        : base(BoundNodeKind.ObjectLiteralExpression, type)
    {
        Properties = properties;
    }
}

public sealed class BoundIndexExpression : BoundNode
{
    public BoundNode Object { get; }
    public BoundNode Index { get; }

    public BoundIndexExpression(BoundNode obj, BoundNode index, TsType type)
        : base(BoundNodeKind.IndexExpression, type)
    {
        Object = obj;
        Index = index;
    }
}

// Bound statements
public sealed class BoundExpressionStatement : BoundNode
{
    public BoundNode Expression { get; }
    public BoundExpressionStatement(BoundNode expression)
        : base(BoundNodeKind.ExpressionStatement, TsType.Void)
    {
        Expression = expression;
    }
}

public sealed class BoundReturnStatement : BoundNode
{
    public BoundNode? Value { get; }
    public BoundReturnStatement(BoundNode? value)
        : base(BoundNodeKind.ReturnStatement, TsType.Void)
    {
        Value = value;
    }
}

public sealed class BoundIfStatement : BoundNode
{
    public BoundNode Condition { get; }
    public BoundNode ThenBranch { get; }
    public BoundNode? ElseBranch { get; }

    public BoundIfStatement(BoundNode condition, BoundNode thenBranch, BoundNode? elseBranch)
        : base(BoundNodeKind.IfStatement, TsType.Void)
    {
        Condition = condition;
        ThenBranch = thenBranch;
        ElseBranch = elseBranch;
    }
}

public sealed class BoundWhileStatement : BoundNode
{
    public BoundNode Condition { get; }
    public BoundNode Body { get; }

    public BoundWhileStatement(BoundNode condition, BoundNode body)
        : base(BoundNodeKind.WhileStatement, TsType.Void)
    {
        Condition = condition;
        Body = body;
    }
}

public sealed class BoundForStatement : BoundNode
{
    public BoundNode? Initializer { get; }
    public BoundNode? Condition { get; }
    public BoundNode? Iterator { get; }
    public BoundNode Body { get; }

    public BoundForStatement(BoundNode? initializer, BoundNode? condition, BoundNode? iterator, BoundNode body)
        : base(BoundNodeKind.ForStatement, TsType.Void)
    {
        Initializer = initializer;
        Condition = condition;
        Iterator = iterator;
        Body = body;
    }
}

public sealed class BoundBreakStatement : BoundNode
{
    public BoundBreakStatement()
        : base(BoundNodeKind.BreakStatement, TsType.Void)
    {
    }
}

public sealed class BoundContinueStatement : BoundNode
{
    public BoundContinueStatement()
        : base(BoundNodeKind.ContinueStatement, TsType.Void)
    {
    }
}

public sealed class BoundBlockStatement : BoundNode
{
    public List<BoundNode> Statements { get; }

    public BoundBlockStatement(List<BoundNode> statements)
        : base(BoundNodeKind.BlockStatement, TsType.Void)
    {
        Statements = statements;
    }
}

public sealed class BoundVariableDeclaration : BoundNode
{
    public LocalSymbol Symbol { get; }
    public BoundNode? Initializer { get; }

    public BoundVariableDeclaration(LocalSymbol symbol, BoundNode? initializer)
        : base(BoundNodeKind.VariableDeclaration, TsType.Void)
    {
        Symbol = symbol;
        Initializer = initializer;
    }
}

public sealed class BoundThrowStatement : BoundNode
{
    public BoundNode Expression { get; }
    public BoundThrowStatement(BoundNode expression)
        : base(BoundNodeKind.ThrowStatement, TsType.Void)
    {
        Expression = expression;
    }
}

public sealed class BoundTryStatement : BoundNode
{
    public BoundNode TryBlock { get; }
    public Symbol? CatchVariable { get; }
    public BoundNode? CatchBlock { get; }
    public BoundNode? FinallyBlock { get; }

    public BoundTryStatement(BoundNode tryBlock, Symbol? catchVariable, BoundNode? catchBlock, BoundNode? finallyBlock)
        : base(BoundNodeKind.TryStatement, TsType.Void)
    {
        TryBlock = tryBlock;
        CatchVariable = catchVariable;
        CatchBlock = catchBlock;
        FinallyBlock = finallyBlock;
    }
}

// Bound declarations
public sealed class BoundFunctionDeclaration : BoundNode
{
    public FunctionSymbol Symbol { get; }
    public BoundNode Body { get; }

    // Variables captured from enclosing functions, in stable order; the
    // closure environment threads their boxes into the callee frame.
    public List<Symbol> CapturedVariables { get; } = new();

    public BoundFunctionDeclaration(FunctionSymbol symbol, BoundNode body)
        : base(BoundNodeKind.FunctionDeclaration, TsType.Void)
    {
        Symbol = symbol;
        Body = body;
    }
}

public sealed class BoundMethodDeclaration : BoundNode
{
    public string ClassName { get; }
    public MethodSymbol Symbol { get; }
    public BoundNode Body { get; }

    public BoundMethodDeclaration(string className, MethodSymbol symbol, BoundNode body)
        : base(BoundNodeKind.MethodDeclaration, TsType.Void)
    {
        ClassName = className;
        Symbol = symbol;
        Body = body;
    }
}

public sealed class BoundConstructorDeclaration : BoundNode
{
    public string ClassName { get; }
    public MethodSymbol Symbol { get; }
    public BoundNode Body { get; }

    public BoundConstructorDeclaration(string className, MethodSymbol symbol, BoundNode body)
        : base(BoundNodeKind.ConstructorDeclaration, TsType.Void)
    {
        ClassName = className;
        Symbol = symbol;
        Body = body;
    }
}

public sealed class BoundFieldInitializer : BoundNode
{
    public string ClassName { get; }
    public FieldSymbol Field { get; }

    public BoundFieldInitializer(string className, FieldSymbol field)
        : base(BoundNodeKind.FieldInitializer, TsType.Void)
    {
        ClassName = className;
        Field = field;
    }
}

public sealed class BoundClassDeclaration : BoundNode
{
    public ClassSymbol Symbol { get; }
    public List<BoundNode> Members { get; }

    public BoundClassDeclaration(ClassSymbol symbol, List<BoundNode> members)
        : base(BoundNodeKind.ClassDeclaration, TsType.Void)
    {
        Symbol = symbol;
        Members = members;
    }
}

public sealed class BoundInterfaceDeclaration : BoundNode
{
    public InterfaceSymbol Symbol { get; }
    public BoundInterfaceDeclaration(InterfaceSymbol symbol)
        : base(BoundNodeKind.InterfaceDeclaration, TsType.Void)
    {
        Symbol = symbol;
    }
}

public sealed class BoundEnumDeclaration : BoundNode
{
    public EnumSymbol Symbol { get; }
    public BoundEnumDeclaration(EnumSymbol symbol)
        : base(BoundNodeKind.EnumDeclaration, TsType.Void)
    {
        Symbol = symbol;
    }
}

public sealed class BoundImportDeclaration : BoundNode
{
    public string ModulePath { get; }
    public List<string> ImportedNames { get; }

    public BoundImportDeclaration(string modulePath, List<string> importedNames)
        : base(BoundNodeKind.ImportDeclaration, TsType.Void)
    {
        ModulePath = modulePath;
        ImportedNames = importedNames;
    }
}

public sealed class BoundSourceFile : BoundNode
{
    public string FileName { get; }
    public List<BoundNode> Members { get; }

    public BoundSourceFile(string fileName, List<BoundNode> members)
        : base(BoundNodeKind.SourceFile, TsType.Void)
    {
        FileName = fileName;
        Members = members;
    }
}
