using TypeSharp.Syntax;

namespace TypeSharp.Semantics.Symbols;

public enum SymbolKind
{
    Local,
    Parameter,
    Field,
    Property,
    Method,
    Function,
    Class,
    Interface,
    Enum,
    TypeAlias,
    Module,
    TypeParameter,
    Namespace,
}

public abstract class Symbol
{
    public abstract SymbolKind Kind { get; }
    public string Name { get; }
    public TypeSystem.TsType Type { get; set; }
    public SourceRange Location { get; }

    protected Symbol(string name, TypeSystem.TsType type, SourceRange location)
    {
        Name = name;
        Type = type;
        Location = location;
    }

    public override string ToString() => $"{Kind}: {Name} : {Type}";
}

public sealed class LocalSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Local;
    public bool IsConst { get; set; }

    // Captured by a nested function: storage becomes a heap box so the
    // closure and the declaring frame share one mutable cell.
    public bool IsCaptured { get; set; }

    public LocalSymbol(string name, TypeSystem.TsType type, SourceRange location, bool isConst = false)
        : base(name, type, location)
    {
        IsConst = isConst;
    }
}

public sealed class ParameterSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Parameter;
    public bool HasDefault { get; set; }
    public object? DefaultValue { get; set; }
    public bool IsCaptured { get; set; }

    public ParameterSymbol(string name, TypeSystem.TsType type, SourceRange location)
        : base(name, type, location) { }
}

public sealed class FieldSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Field;
    public bool IsReadonly { get; set; }
    public bool IsStatic { get; set; }
    public TypeSystem.TsAccessModifier AccessModifier { get; set; }

    public FieldSymbol(string name, TypeSystem.TsType type, SourceRange location)
        : base(name, type, location) { }
}

public sealed class PropertySymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Property;
    public bool IsReadonly { get; set; }
    public TypeSystem.TsAccessModifier AccessModifier { get; set; }

    public PropertySymbol(string name, TypeSystem.TsType type, SourceRange location)
        : base(name, type, location) { }
}

public sealed class MethodSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Method;
    public List<ParameterSymbol> Parameters { get; } = new();
    public bool IsAsync { get; set; }
    public bool IsStatic { get; set; }
    public TypeSystem.TsAccessModifier AccessModifier { get; set; }

    // Class that actually declares the method (may be a base class of the
    // receiver's static type); codegen targets this class for dispatch.
    public string? DeclaringClassName { get; set; }

    public MethodSymbol(string name, TypeSystem.TsType returnType, SourceRange location)
        : base(name, returnType, location) { }
}

public sealed class FunctionSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Function;
    public List<ParameterSymbol> Parameters { get; } = new();
    public bool IsAsync { get; set; }
    public bool IsExported { get; set; }
    public bool HasDynamicSignature { get; set; }

    // Set on import aliases: the exported function name to call at runtime.
    public string? TargetName { get; set; }

    public FunctionSymbol(string name, TypeSystem.TsType returnType, SourceRange location)
        : base(name, returnType, location) { }
}

public sealed class ClassSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Class;
    public TypeSystem.TsClassType ClassType => (TypeSystem.TsClassType)Type;
    public bool IsExported { get; set; }

    public ClassSymbol(string name, TypeSystem.TsClassType type, SourceRange location)
        : base(name, type, location) { }
}

public sealed class InterfaceSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Interface;
    public TypeSystem.TsInterfaceType InterfaceType => (TypeSystem.TsInterfaceType)Type;
    public bool IsExported { get; set; }

    public InterfaceSymbol(string name, TypeSystem.TsInterfaceType type, SourceRange location)
        : base(name, type, location) { }
}

public sealed class EnumSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Enum;
    public TypeSystem.TsEnumType EnumType => (TypeSystem.TsEnumType)Type;
    public bool IsExported { get; set; }

    public EnumSymbol(string name, TypeSystem.TsEnumType type, SourceRange location)
        : base(name, type, location) { }
}

public sealed class TypeParameterSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.TypeParameter;
    public TypeSystem.TsType? Constraint { get; set; }

    public TypeParameterSymbol(string name, SourceRange location)
        : base(name, new TypeSystem.TsTypeParameter(name), location) { }
}

public sealed class ModuleSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Module;
    public Dictionary<string, Symbol> Exports { get; } = new();
    public string FilePath { get; }

    public ModuleSymbol(string name, string filePath, SourceRange location)
        : base(name, TypeSystem.TsType.Void, location)
    {
        FilePath = filePath;
    }
}
