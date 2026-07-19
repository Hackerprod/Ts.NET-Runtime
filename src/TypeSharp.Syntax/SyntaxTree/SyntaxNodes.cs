namespace TypeSharp.Syntax.SyntaxTree;

public enum SyntaxNodeType
{
    // Expressions
    LiteralExpression,
    IdentifierExpression,
    BinaryExpression,
    UnaryExpression,
    CallExpression,
    MemberAccessExpression,
    IndexExpression,
    ConditionalExpression,
    AssignmentExpression,
    NewExpression,
    ThisExpression,
    SuperExpression,
    LambdaExpression,
    AwaitExpression,
    TypeofExpression,
    ObjectLiteralExpression,
    ArrayLiteralExpression,
    AsExpression,
    ForOfStatement,
    VariableDeclarationList,
    TypeofExpressionNode,

    // Statements
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
    MatchStatement,

    // Declarations
    FunctionDeclaration,
    ClassDeclaration,
    InterfaceDeclaration,
    EnumDeclaration,
    TypeAliasDeclaration,
    ImportDeclaration,
    ExportDeclaration,
    ModuleDeclaration,

    // Parameters & Members
    Parameter,
    TypeAnnotation,
    GenericParameter,
    GenericConstraint,
    PropertyDeclaration,
    MethodDeclaration,
    ConstructorDeclaration,
    FieldDeclaration,
    EnumMember,
    Modifier,

    // Types
    PrimitiveType,
    NamedType,
    ArrayType,
    MapType,
    PromiseType,
    NullableType,
    UnionType,
    IntersectionType,
    TupleType,
    FunctionType,

    // Module
    SourceFile,
    NamedImport,
    WildcardImport,

    // Misc
    Decorator,
    CommentTrivia,
}

public abstract class SyntaxNode
{
    public SyntaxNodeType NodeType { get; }
    public SourceRange Range { get; set; }

    protected SyntaxNode(SyntaxNodeType nodeType, SourceRange range)
    {
        NodeType = nodeType;
        Range = range;
    }

    public abstract IEnumerable<SyntaxNode> GetChildren();
}

public abstract class ExpressionSyntax : SyntaxNode
{
    protected ExpressionSyntax(SyntaxNodeType nodeType, SourceRange range) : base(nodeType, range) { }
}

public abstract class StatementSyntax : SyntaxNode
{
    protected StatementSyntax(SyntaxNodeType nodeType, SourceRange range) : base(nodeType, range) { }
}

public abstract class DeclarationSyntax : SyntaxNode
{
    public List<SyntaxToken> Modifiers { get; set; } = new();
    protected DeclarationSyntax(SyntaxNodeType nodeType, SourceRange range) : base(nodeType, range) { }
}

public abstract class TypeSyntax : SyntaxNode
{
    protected TypeSyntax(SyntaxNodeType nodeType, SourceRange range) : base(nodeType, range) { }
}

public sealed class SyntaxToken
{
    public Token Token { get; }
    public SyntaxToken(Token token) => Token = token;
}

// Source file root
public sealed class SourceFileSyntax : SyntaxNode
{
    public string FileName { get; }
    public List<SyntaxNode> Members { get; }

    public SourceFileSyntax(string fileName, List<SyntaxNode> members, SourceRange range)
        : base(SyntaxNodeType.SourceFile, range)
    {
        FileName = fileName;
        Members = members;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Members;
}

// Expressions
public sealed class LiteralExpressionSyntax : ExpressionSyntax
{
    public Token Token { get; }

    public LiteralExpressionSyntax(Token token, SourceRange range)
        : base(SyntaxNodeType.LiteralExpression, range)
    {
        Token = token;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Enumerable.Empty<SyntaxNode>();
}

public sealed class IdentifierExpressionSyntax : ExpressionSyntax
{
    public string Name { get; }

    public IdentifierExpressionSyntax(string name, SourceRange range)
        : base(SyntaxNodeType.IdentifierExpression, range)
    {
        Name = name;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Enumerable.Empty<SyntaxNode>();
}

public sealed class BinaryExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Left { get; }
    public Token OperatorToken { get; }
    public ExpressionSyntax Right { get; }

    public BinaryExpressionSyntax(ExpressionSyntax left, Token op, ExpressionSyntax right, SourceRange range)
        : base(SyntaxNodeType.BinaryExpression, range)
    {
        Left = left;
        OperatorToken = op;
        Right = right;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new SyntaxNode[] { Left, Right };
}

public sealed class UnaryExpressionSyntax : ExpressionSyntax
{
    public Token OperatorToken { get; }
    public ExpressionSyntax Operand { get; }
    public bool IsPrefix { get; }

    public UnaryExpressionSyntax(Token op, ExpressionSyntax operand, bool isPrefix, SourceRange range)
        : base(SyntaxNodeType.UnaryExpression, range)
    {
        OperatorToken = op;
        Operand = operand;
        IsPrefix = isPrefix;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new[] { Operand };
}

public sealed class CallExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Callee { get; }
    public List<TypeSyntax> TypeArguments { get; }
    public List<ExpressionSyntax> Arguments { get; }

    public CallExpressionSyntax(
        ExpressionSyntax callee,
        List<TypeSyntax> typeArguments,
        List<ExpressionSyntax> arguments,
        SourceRange range)
        : base(SyntaxNodeType.CallExpression, range)
    {
        Callee = callee;
        TypeArguments = typeArguments;
        Arguments = arguments;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return Callee;
        foreach (var typeArg in TypeArguments) yield return typeArg;
        foreach (var arg in Arguments) yield return arg;
    }
}

public sealed class MemberAccessExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Object { get; }
    public string MemberName { get; }
    public bool IsNullConditional { get; }

    public MemberAccessExpressionSyntax(ExpressionSyntax obj, string memberName, bool isNullConditional, SourceRange range)
        : base(SyntaxNodeType.MemberAccessExpression, range)
    {
        Object = obj;
        MemberName = memberName;
        IsNullConditional = isNullConditional;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new[] { Object };
}

public sealed class IndexExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Object { get; }
    public ExpressionSyntax Index { get; }

    public IndexExpressionSyntax(ExpressionSyntax obj, ExpressionSyntax index, SourceRange range)
        : base(SyntaxNodeType.IndexExpression, range)
    {
        Object = obj;
        Index = index;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new SyntaxNode[] { Object, Index };
}

public sealed class ConditionalExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Condition { get; }
    public ExpressionSyntax WhenTrue { get; }
    public ExpressionSyntax WhenFalse { get; }

    public ConditionalExpressionSyntax(ExpressionSyntax condition, ExpressionSyntax whenTrue, ExpressionSyntax whenFalse, SourceRange range)
        : base(SyntaxNodeType.ConditionalExpression, range)
    {
        Condition = condition;
        WhenTrue = whenTrue;
        WhenFalse = whenFalse;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new SyntaxNode[] { Condition, WhenTrue, WhenFalse };
}

public sealed class AssignmentExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Target { get; }
    public Token OperatorToken { get; }
    public ExpressionSyntax Value { get; }

    public AssignmentExpressionSyntax(ExpressionSyntax target, Token op, ExpressionSyntax value, SourceRange range)
        : base(SyntaxNodeType.AssignmentExpression, range)
    {
        Target = target;
        OperatorToken = op;
        Value = value;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new SyntaxNode[] { Target, Value };
}

public sealed class NewExpressionSyntax : ExpressionSyntax
{
    public TypeSyntax Type { get; }
    public List<ExpressionSyntax> Arguments { get; }

    public NewExpressionSyntax(TypeSyntax type, List<ExpressionSyntax> arguments, SourceRange range)
        : base(SyntaxNodeType.NewExpression, range)
    {
        Type = type;
        Arguments = arguments;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return Type;
        foreach (var arg in Arguments) yield return arg;
    }
}

public sealed class ThisExpressionSyntax : ExpressionSyntax
{
    public ThisExpressionSyntax(SourceRange range) : base(SyntaxNodeType.ThisExpression, range) { }
    public override IEnumerable<SyntaxNode> GetChildren() => Enumerable.Empty<SyntaxNode>();
}

public sealed class SuperExpressionSyntax : ExpressionSyntax
{
    public SuperExpressionSyntax(SourceRange range) : base(SyntaxNodeType.SuperExpression, range) { }
    public override IEnumerable<SyntaxNode> GetChildren() => Enumerable.Empty<SyntaxNode>();
}

public sealed class LambdaExpressionSyntax : ExpressionSyntax
{
    public List<ParameterSyntax> Parameters { get; }
    public TypeSyntax? ReturnType { get; }
    public SyntaxNode Body { get; }
    public bool IsAsync { get; }
    public List<GenericParameterSyntax> GenericParameters { get; } = new();

    public LambdaExpressionSyntax(List<ParameterSyntax> parameters, TypeSyntax? returnType, SyntaxNode body, bool isAsync, SourceRange range)
        : base(SyntaxNodeType.LambdaExpression, range)
    {
        Parameters = parameters;
        ReturnType = returnType;
        Body = body;
        IsAsync = isAsync;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        foreach (var p in Parameters) yield return p;
        if (ReturnType != null) yield return ReturnType;
        yield return Body;
    }
}

public sealed class AsExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Expression { get; }
    public TypeSyntax TargetType { get; }

    public AsExpressionSyntax(ExpressionSyntax expression, TypeSyntax targetType, SourceRange range)
        : base(SyntaxNodeType.AsExpression, range)
    {
        Expression = expression;
        TargetType = targetType;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new SyntaxNode[] { Expression, TargetType };
}

public sealed class AwaitExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Expression { get; }

    public AwaitExpressionSyntax(ExpressionSyntax expression, SourceRange range)
        : base(SyntaxNodeType.AwaitExpression, range)
    {
        Expression = expression;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new[] { Expression };
}

public sealed class ObjectPropertySyntax : SyntaxNode
{
    public string Key { get; }
    public ExpressionSyntax Value { get; }

    public ObjectPropertySyntax(string key, ExpressionSyntax value, SourceRange range)
        : base(SyntaxNodeType.PropertyDeclaration, range)
    {
        Key = key;
        Value = value;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new[] { Value };
}

public sealed class ArrayLiteralExpressionSyntax : ExpressionSyntax
{
    public List<ExpressionSyntax> Elements { get; }

    public ArrayLiteralExpressionSyntax(List<ExpressionSyntax> elements, SourceRange range)
        : base(SyntaxNodeType.ArrayLiteralExpression, range)
    {
        Elements = elements;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Elements.Cast<SyntaxNode>();
}

public sealed class ObjectLiteralExpressionSyntax : ExpressionSyntax
{
    public List<ObjectPropertySyntax> Properties { get; }

    public ObjectLiteralExpressionSyntax(List<ObjectPropertySyntax> properties, SourceRange range)
        : base(SyntaxNodeType.ObjectLiteralExpression, range)
    {
        Properties = properties;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Properties.Cast<SyntaxNode>();
}

// Statements
public sealed class ExpressionStatementSyntax : StatementSyntax
{
    public ExpressionSyntax Expression { get; }

    public ExpressionStatementSyntax(ExpressionSyntax expression, SourceRange range)
        : base(SyntaxNodeType.ExpressionStatement, range)
    {
        Expression = expression;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new[] { Expression };
}

public sealed class ReturnStatementSyntax : StatementSyntax
{
    public ExpressionSyntax? Value { get; }

    public ReturnStatementSyntax(ExpressionSyntax? value, SourceRange range)
        : base(SyntaxNodeType.ReturnStatement, range)
    {
        Value = value;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        if (Value != null) yield return Value;
    }
}

public sealed class IfStatementSyntax : StatementSyntax
{
    public ExpressionSyntax Condition { get; }
    public StatementSyntax ThenBranch { get; }
    public StatementSyntax? ElseBranch { get; }

    public IfStatementSyntax(ExpressionSyntax condition, StatementSyntax thenBranch, StatementSyntax? elseBranch, SourceRange range)
        : base(SyntaxNodeType.IfStatement, range)
    {
        Condition = condition;
        ThenBranch = thenBranch;
        ElseBranch = elseBranch;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return Condition;
        yield return ThenBranch;
        if (ElseBranch != null) yield return ElseBranch;
    }
}

public sealed class WhileStatementSyntax : StatementSyntax
{
    public ExpressionSyntax Condition { get; }
    public StatementSyntax Body { get; }

    public WhileStatementSyntax(ExpressionSyntax condition, StatementSyntax body, SourceRange range)
        : base(SyntaxNodeType.WhileStatement, range)
    {
        Condition = condition;
        Body = body;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new SyntaxNode[] { Condition, Body };
}

public sealed class ForStatementSyntax : StatementSyntax
{
    public SyntaxNode? Initializer { get; }
    public ExpressionSyntax? Condition { get; }
    public ExpressionSyntax? Iterator { get; }
    public StatementSyntax Body { get; }

    public ForStatementSyntax(SyntaxNode? initializer, ExpressionSyntax? condition, ExpressionSyntax? iterator, StatementSyntax body, SourceRange range)
        : base(SyntaxNodeType.ForStatement, range)
    {
        Initializer = initializer;
        Condition = condition;
        Iterator = iterator;
        Body = body;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        if (Initializer != null) yield return Initializer;
        if (Condition != null) yield return Condition;
        if (Iterator != null) yield return Iterator;
        yield return Body;
    }
}

public sealed class BreakStatementSyntax : StatementSyntax
{
    public BreakStatementSyntax(SourceRange range)
        : base(SyntaxNodeType.BreakStatement, range)
    {
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield break;
    }
}

public sealed class ContinueStatementSyntax : StatementSyntax
{
    public ContinueStatementSyntax(SourceRange range)
        : base(SyntaxNodeType.ContinueStatement, range)
    {
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield break;
    }
}

public sealed class BlockStatementSyntax : StatementSyntax
{
    public List<SyntaxNode> Statements { get; }

    public BlockStatementSyntax(List<SyntaxNode> statements, SourceRange range)
        : base(SyntaxNodeType.BlockStatement, range)
    {
        Statements = statements;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Statements;
}

public sealed class VariableDeclarationSyntax : StatementSyntax
{
    public string Name { get; }
    public TypeSyntax? TypeAnnotation { get; }
    public ExpressionSyntax? Initializer { get; }
    public bool IsConst { get; }
    public bool IsExported { get; set; }

    public VariableDeclarationSyntax(string name, TypeSyntax? typeAnnotation, ExpressionSyntax? initializer, bool isConst, SourceRange range)
        : base(SyntaxNodeType.VariableDeclaration, range)
    {
        Name = name;
        TypeAnnotation = typeAnnotation;
        Initializer = initializer;
        IsConst = isConst;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        if (TypeAnnotation != null) yield return TypeAnnotation;
        if (Initializer != null) yield return Initializer;
    }
}

public sealed class ThrowStatementSyntax : StatementSyntax
{
    public ExpressionSyntax Expression { get; }

    public ThrowStatementSyntax(ExpressionSyntax expression, SourceRange range)
        : base(SyntaxNodeType.ThrowStatement, range)
    {
        Expression = expression;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new[] { Expression };
}

public sealed class TryStatementSyntax : StatementSyntax
{
    public BlockStatementSyntax TryBlock { get; }
    public string? CatchVariable { get; }
    public TypeSyntax? CatchType { get; }
    public BlockStatementSyntax? CatchBlock { get; }
    public BlockStatementSyntax? FinallyBlock { get; }

    public TryStatementSyntax(BlockStatementSyntax tryBlock, string? catchVariable, TypeSyntax? catchType, BlockStatementSyntax? catchBlock, BlockStatementSyntax? finallyBlock, SourceRange range)
        : base(SyntaxNodeType.TryStatement, range)
    {
        TryBlock = tryBlock;
        CatchVariable = catchVariable;
        CatchType = catchType;
        CatchBlock = catchBlock;
        FinallyBlock = finallyBlock;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return TryBlock;
        if (CatchBlock != null) yield return CatchBlock;
        if (FinallyBlock != null) yield return FinallyBlock;
    }
}

// Declarations
public sealed class FunctionDeclarationSyntax : DeclarationSyntax
{
    public string Name { get; }
    public List<ParameterSyntax> Parameters { get; }
    public TypeSyntax? ReturnType { get; }
    public SyntaxNode Body { get; }
    public bool IsAsync { get; }
    public List<GenericParameterSyntax> GenericParameters { get; } = new();

    public FunctionDeclarationSyntax(string name, List<ParameterSyntax> parameters, TypeSyntax? returnType, SyntaxNode body, bool isAsync, SourceRange range)
        : base(SyntaxNodeType.FunctionDeclaration, range)
    {
        Name = name;
        Parameters = parameters;
        ReturnType = returnType;
        Body = body;
        IsAsync = isAsync;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        foreach (var p in Parameters) yield return p;
        if (ReturnType != null) yield return ReturnType;
        yield return Body;
    }
}

public sealed class ClassDeclarationSyntax : DeclarationSyntax
{
    public string Name { get; }
    public List<GenericParameterSyntax> GenericParameters { get; set; } = new();
    public TypeSyntax? BaseType { get; set; }
    public List<TypeSyntax> ImplementedInterfaces { get; } = new();
    public List<SyntaxNode> Members { get; }

    public ClassDeclarationSyntax(string name, List<SyntaxNode> members, SourceRange range)
        : base(SyntaxNodeType.ClassDeclaration, range)
    {
        Name = name;
        Members = members;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Members;
}

public sealed class InterfaceDeclarationSyntax : DeclarationSyntax
{
    public string Name { get; }
    public List<GenericParameterSyntax> GenericParameters { get; set; } = new();
    public List<TypeSyntax> ExtendedInterfaces { get; } = new();
    public List<SyntaxNode> Members { get; }

    public InterfaceDeclarationSyntax(string name, List<SyntaxNode> members, SourceRange range)
        : base(SyntaxNodeType.InterfaceDeclaration, range)
    {
        Name = name;
        Members = members;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Members;
}

public sealed class EnumDeclarationSyntax : DeclarationSyntax
{
    public string Name { get; }
    public List<EnumMemberSyntax> Members { get; }

    public EnumDeclarationSyntax(string name, List<EnumMemberSyntax> members, SourceRange range)
        : base(SyntaxNodeType.EnumDeclaration, range)
    {
        Name = name;
        Members = members;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Members.Cast<SyntaxNode>();
}

public sealed class TypeAliasDeclarationSyntax : DeclarationSyntax
{
    public string Name { get; }
    public TypeSyntax Type { get; }
    public List<GenericParameterSyntax> GenericParameters { get; } = new();

    public TypeAliasDeclarationSyntax(string name, TypeSyntax type, SourceRange range)
        : base(SyntaxNodeType.TypeAliasDeclaration, range)
    {
        Name = name;
        Type = type;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new[] { Type };
}

public sealed class ImportDeclarationSyntax : DeclarationSyntax
{
    public List<NamedImportSyntax> NamedImports { get; set; } = new();
    public bool IsWildcard { get; set; }
    public string ModulePath { get; }

    public ImportDeclarationSyntax(string modulePath, SourceRange range)
        : base(SyntaxNodeType.ImportDeclaration, range)
    {
        ModulePath = modulePath;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => NamedImports;
}

// Members
public sealed class PropertyDeclarationSyntax : SyntaxNode
{
    public string Name { get; }
    public TypeSyntax TypeAnnotation { get; }
    public ExpressionSyntax? Initializer { get; }
    public bool IsReadonly { get; }

    public PropertyDeclarationSyntax(string name, TypeSyntax typeAnnotation, ExpressionSyntax? initializer, bool isReadonly, SourceRange range)
        : base(SyntaxNodeType.PropertyDeclaration, range)
    {
        Name = name;
        TypeAnnotation = typeAnnotation;
        Initializer = initializer;
        IsReadonly = isReadonly;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return TypeAnnotation;
        if (Initializer != null) yield return Initializer;
    }
}

public sealed class MethodDeclarationSyntax : SyntaxNode
{
    public string Name { get; }
    public List<ParameterSyntax> Parameters { get; }
    public TypeSyntax? ReturnType { get; }
    public SyntaxNode? Body { get; }
    public bool IsAsync { get; }
    public List<GenericParameterSyntax> GenericParameters { get; } = new();

    public MethodDeclarationSyntax(string name, List<ParameterSyntax> parameters, TypeSyntax? returnType, SyntaxNode? body, bool isAsync, SourceRange range)
        : base(SyntaxNodeType.MethodDeclaration, range)
    {
        Name = name;
        Parameters = parameters;
        ReturnType = returnType;
        Body = body;
        IsAsync = isAsync;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        foreach (var g in GenericParameters) yield return g;
        foreach (var p in Parameters) yield return p;
        if (ReturnType != null) yield return ReturnType;
        if (Body != null) yield return Body;
    }
}

public sealed class ConstructorDeclarationSyntax : SyntaxNode
{
    public List<ParameterSyntax> Parameters { get; }
    public SyntaxNode Body { get; }

    public ConstructorDeclarationSyntax(List<ParameterSyntax> parameters, SyntaxNode body, SourceRange range)
        : base(SyntaxNodeType.ConstructorDeclaration, range)
    {
        Parameters = parameters;
        Body = body;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        foreach (var p in Parameters) yield return p;
        yield return Body;
    }
}

public sealed class FieldDeclarationSyntax : SyntaxNode
{
    public string Name { get; }
    public TypeSyntax TypeAnnotation { get; }
    public ExpressionSyntax? Initializer { get; }
    public bool IsReadonly { get; }

    public FieldDeclarationSyntax(string name, TypeSyntax typeAnnotation, ExpressionSyntax? initializer, SourceRange range, bool isReadonly = false)
        : base(SyntaxNodeType.FieldDeclaration, range)
    {
        Name = name;
        TypeAnnotation = typeAnnotation;
        Initializer = initializer;
        IsReadonly = isReadonly;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return TypeAnnotation;
        if (Initializer != null) yield return Initializer;
    }
}

public sealed class EnumMemberSyntax : SyntaxNode
{
    public string Name { get; }
    public ExpressionSyntax? Value { get; }

    public EnumMemberSyntax(string name, ExpressionSyntax? value, SourceRange range)
        : base(SyntaxNodeType.EnumMember, range)
    {
        Name = name;
        Value = value;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        if (Value != null) yield return Value;
    }
}

// Parameters & Types
public sealed class ParameterSyntax : SyntaxNode
{
    public string Name { get; }
    public TypeSyntax TypeAnnotation { get; }
    public bool TypeWasInferred { get; }
    public ExpressionSyntax? DefaultValue { get; }

    // TypeScript constructor parameter properties: `constructor(private x: T)`
    // declares a field and assigns it from the parameter.
    public bool IsPropertyParameter { get; set; }

    public ParameterSyntax(
        string name,
        TypeSyntax typeAnnotation,
        ExpressionSyntax? defaultValue,
        SourceRange range,
        bool typeWasInferred = false)
        : base(SyntaxNodeType.Parameter, range)
    {
        Name = name;
        TypeAnnotation = typeAnnotation;
        TypeWasInferred = typeWasInferred;
        DefaultValue = defaultValue;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        yield return TypeAnnotation;
        if (DefaultValue != null) yield return DefaultValue;
    }
}

public sealed class GenericParameterSyntax : SyntaxNode
{
    public string Name { get; }
    public List<TypeSyntax> Constraints { get; } = new();

    public GenericParameterSyntax(string name, SourceRange range)
        : base(SyntaxNodeType.GenericParameter, range)
    {
        Name = name;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Constraints;
}

// Type nodes
public sealed class PrimitiveTypeSyntax : TypeSyntax
{
    public Token TypeKeyword { get; }

    public PrimitiveTypeSyntax(Token typeKeyword, SourceRange range)
        : base(SyntaxNodeType.PrimitiveType, range)
    {
        TypeKeyword = typeKeyword;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Enumerable.Empty<SyntaxNode>();
}

public sealed class NamedTypeSyntax : TypeSyntax
{
    public string Name { get; }
    public List<TypeSyntax> TypeArguments { get; } = new();

    public NamedTypeSyntax(string name, SourceRange range)
        : base(SyntaxNodeType.NamedType, range)
    {
        Name = name;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => TypeArguments;
}

public sealed class ObjectTypeMemberSyntax
{
    public string Name { get; }
    public TypeSyntax Type { get; }
    public bool IsOptional { get; }
    public bool IsReadonly { get; }

    public ObjectTypeMemberSyntax(string name, TypeSyntax type, bool isOptional, bool isReadonly = false)
    {
        Name = name;
        Type = type;
        IsOptional = isOptional;
        IsReadonly = isReadonly;
    }
}

// `[A, B, …]` — fixed-shape tuple type.
public sealed class TupleTypeSyntax : TypeSyntax
{
    public List<TypeSyntax> ElementTypes { get; }

    public TupleTypeSyntax(List<TypeSyntax> elementTypes, SourceRange range)
        : base(SyntaxNodeType.TypeAnnotation, range)
    {
        ElementTypes = elementTypes;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => ElementTypes;
}

// Anonymous structural type: `{ value: T; next?: Node<T> }`
public sealed class ObjectTypeSyntax : TypeSyntax
{
    public List<ObjectTypeMemberSyntax> Members { get; }

    public ObjectTypeSyntax(List<ObjectTypeMemberSyntax> members, SourceRange range)
        : base(SyntaxNodeType.TypeAnnotation, range)
    {
        Members = members;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Members.Select(m => (SyntaxNode)m.Type);
}

public sealed class FunctionTypeSyntax : TypeSyntax
{
    public List<ParameterSyntax> Parameters { get; }
    public TypeSyntax ReturnType { get; }

    public FunctionTypeSyntax(List<ParameterSyntax> parameters, TypeSyntax returnType, SourceRange range)
        : base(SyntaxNodeType.FunctionType, range)
    {
        Parameters = parameters;
        ReturnType = returnType;
    }

    public override IEnumerable<SyntaxNode> GetChildren()
    {
        foreach (var p in Parameters) yield return p;
        yield return ReturnType;
    }
}

// `let a = 1, b = 2` — multiple declarators sharing one statement; binds
// into the CURRENT scope (unlike a block).
public sealed class VariableDeclarationListSyntax : StatementSyntax
{
    public List<VariableDeclarationSyntax> Declarations { get; }

    public VariableDeclarationListSyntax(List<VariableDeclarationSyntax> declarations, SourceRange range)
        : base(SyntaxNodeType.VariableDeclarationList, range)
    {
        Declarations = declarations;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Declarations;
}

// `expr!` — non-null assertion; strips nullability statically, no runtime effect.
public sealed class NonNullAssertionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Expression { get; }

    public NonNullAssertionSyntax(ExpressionSyntax expression, SourceRange range)
        : base(SyntaxNodeType.AsExpression, range)
    {
        Expression = expression;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new[] { Expression };
}

// Interpolated segment of a template literal; evaluates its expression and
// stringifies the result.
public sealed class TemplatePartSyntax : ExpressionSyntax
{
    public ExpressionSyntax Expression { get; }

    public TemplatePartSyntax(ExpressionSyntax expression, SourceRange range)
        : base(SyntaxNodeType.LiteralExpression, range)
    {
        Expression = expression;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new[] { Expression };
}

// `typeof expr` — evaluates to the JS-style type name string.
public sealed class TypeofExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Operand { get; }

    public TypeofExpressionSyntax(ExpressionSyntax operand, SourceRange range)
        : base(SyntaxNodeType.TypeofExpressionNode, range)
    {
        Operand = operand;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new[] { Operand };
}

public sealed class ForOfStatementSyntax : StatementSyntax
{
    public string VariableName { get; }
    public bool IsConst { get; }
    public ExpressionSyntax Iterable { get; }
    public StatementSyntax Body { get; }

    public ForOfStatementSyntax(string variableName, bool isConst, ExpressionSyntax iterable, StatementSyntax body, SourceRange range)
        : base(SyntaxNodeType.ForOfStatement, range)
    {
        VariableName = variableName;
        IsConst = isConst;
        Iterable = iterable;
        Body = body;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new SyntaxNode[] { Iterable, Body };
}

public sealed class ArrayTypeSyntax : TypeSyntax
{
    public TypeSyntax ElementType { get; }
    public bool IsReadonly { get; }

    public ArrayTypeSyntax(TypeSyntax elementType, SourceRange range, bool isReadonly = false)
        : base(SyntaxNodeType.ArrayType, range)
    {
        ElementType = elementType;
        IsReadonly = isReadonly;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new[] { ElementType };
}

public sealed class MapTypeSyntax : TypeSyntax
{
    public TypeSyntax KeyType { get; }
    public TypeSyntax ValueType { get; }

    public MapTypeSyntax(TypeSyntax keyType, TypeSyntax valueType, SourceRange range)
        : base(SyntaxNodeType.MapType, range)
    {
        KeyType = keyType;
        ValueType = valueType;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new SyntaxNode[] { KeyType, ValueType };
}

public sealed class PromiseTypeSyntax : TypeSyntax
{
    public TypeSyntax ElementType { get; }

    public PromiseTypeSyntax(TypeSyntax elementType, SourceRange range)
        : base(SyntaxNodeType.PromiseType, range)
    {
        ElementType = elementType;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new[] { ElementType };
}

public sealed class NullableTypeSyntax : TypeSyntax
{
    public TypeSyntax ElementType { get; }

    public NullableTypeSyntax(TypeSyntax elementType, SourceRange range)
        : base(SyntaxNodeType.NullableType, range)
    {
        ElementType = elementType;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => new[] { ElementType };
}

public sealed class UnionTypeSyntax : TypeSyntax
{
    public List<TypeSyntax> Types { get; }

    public UnionTypeSyntax(List<TypeSyntax> types, SourceRange range)
        : base(SyntaxNodeType.UnionType, range)
    {
        Types = types;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Types;
}

public sealed class NamedImportSyntax : SyntaxNode
{
    public string Name { get; }
    public string? Alias { get; }

    public NamedImportSyntax(string name, string? alias, SourceRange range)
        : base(SyntaxNodeType.NamedImport, range)
    {
        Name = name;
        Alias = alias;
    }

    public override IEnumerable<SyntaxNode> GetChildren() => Enumerable.Empty<SyntaxNode>();
}
