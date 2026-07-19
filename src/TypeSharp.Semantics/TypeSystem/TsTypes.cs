using TypeSharp.Syntax;

namespace TypeSharp.Semantics.TypeSystem;

public abstract class TsType : IEquatable<TsType>
{
    public abstract string Name { get; }
    public abstract bool IsValueType { get; }
    public abstract bool IsReferenceType { get; }

    public virtual bool Equals(TsType? other) => GetType() == other?.GetType() && Name == other.Name;
    public override bool Equals(object? obj) => Equals(obj as TsType);
    public override int GetHashCode() => HashCode.Combine(GetType(), Name);
    public override string ToString() => Name;

    public virtual bool IsAssignableTo(TsType other) => Equals(other);

    public static bool IsCompatibleWith(TsType source, TsType target)
    {
        if (source is TsAnyType || target is TsAnyType)
            return true;
        // Erased generics: an unconstrained type parameter accepts and
        // provides any value at the boundary.
        if (source is TsTypeParameter || target is TsTypeParameter)
            return true;
        if (source is TsNullType)
            return target is TsNullableType || target is TsUnionType;

        if (source.Equals(target)) return true;

        if (IsImplicitNumericWidening(source, target)) return true;

        if (SatisfiesStructuralContract(source, target)) return true;

        if (source.IsAssignableTo(target)) return true;
        if (target.IsAssignableTo(source)) return true;

        if (source is TsNullableType nullable && nullable.ElementType.IsAssignableTo(target))
            return false;

        if (target is TsNullableType targetNullable &&
            (source.IsAssignableTo(targetNullable.ElementType) ||
             IsImplicitNumericWidening(source, targetNullable.ElementType)))
            return true;

        return false;
    }

    private static bool SatisfiesStructuralContract(TsType source, TsType target)
    {
        source = UnwrapGenericDefinition(source);
        target = UnwrapGenericDefinition(target);

        return target switch
        {
            TsInterfaceType targetInterface => SatisfiesInterface(source, targetInterface),
            TsFunctionType targetFunction when source is TsFunctionType sourceFunction =>
                IsFunctionAssignable(sourceFunction, targetFunction),
            _ => false
        };
    }

    private static TsType UnwrapGenericDefinition(TsType type) =>
        type is TsGenericType generic ? generic.Definition : type;

    public static IReadOnlyDictionary<string, TsType> CreateGenericMap(
        IReadOnlyList<TsTypeParameter> parameters,
        IReadOnlyList<TsType> arguments)
    {
        var map = new Dictionary<string, TsType>(StringComparer.Ordinal);
        for (int i = 0; i < parameters.Count && i < arguments.Count; i++)
            map[parameters[i].Name] = arguments[i];
        return map;
    }

    public static TsType Substitute(TsType type, IReadOnlyDictionary<string, TsType> genericArguments)
    {
        if (genericArguments.Count == 0)
            return type;

        return type switch
        {
            TsTypeParameter parameter when genericArguments.TryGetValue(parameter.Name, out var replacement) => replacement,
            TsArrayType array => new TsArrayType(Substitute(array.ElementType, genericArguments)),
            TsTupleType tuple => new TsTupleType(tuple.ElementTypes.Select(t => Substitute(t, genericArguments)).ToList()),
            TsMapType map => new TsMapType(
                Substitute(map.KeyType, genericArguments),
                Substitute(map.ValueType, genericArguments)),
            TsNullableType nullable => new TsNullableType(Substitute(nullable.ElementType, genericArguments)),
            TsPromiseType promise => new TsPromiseType(Substitute(promise.ElementType, genericArguments)),
            TsUnionType union => new TsUnionType(union.Types.Select(t => Substitute(t, genericArguments)).ToList()),
            TsFunctionType function => new TsFunctionType(
                function.Parameters.Select(p => new TsParameter(p.Name, Substitute(p.Type, genericArguments))
                {
                    HasDefault = p.HasDefault,
                    DefaultValue = p.DefaultValue
                }).ToList(),
                Substitute(function.ReturnType, genericArguments)),
            TsGenericType generic => new TsGenericType(
                Substitute(generic.Definition, genericArguments),
                generic.TypeArguments.Select(t => Substitute(t, genericArguments)).ToList()),
            _ => type
        };
    }

    private static bool SatisfiesInterface(TsType source, TsInterfaceType target)
    {
        foreach (var extended in target.ExtendedInterfaces.OfType<TsInterfaceType>())
        {
            if (!SatisfiesInterface(source, extended))
                return false;
        }

        foreach (var required in target.Properties.Values)
        {
            if (!TryGetReadableMemberType(source, required.Name, out var actualType))
                return required.Type is TsNullableType;
            if (!IsCompatibleWith(actualType, required.Type))
                return false;
        }

        foreach (var required in target.Methods.Values)
        {
            if (!TryGetMethod(source, required.Name, out var actualMethod))
                return false;

            var actualFunction = new TsFunctionType(actualMethod.Parameters, actualMethod.ReturnType);
            var requiredFunction = new TsFunctionType(required.Parameters, required.ReturnType);
            if (!IsFunctionAssignable(actualFunction, requiredFunction))
                return false;
        }

        return true;
    }

    private static bool TryGetReadableMemberType(TsType source, string name, out TsType type)
    {
        source = UnwrapGenericDefinition(source);

        switch (source)
        {
            case TsClassType cls:
                for (var current = cls; current != null; current = current.BaseType)
                {
                    if (current.Fields.TryGetValue(name, out var field))
                    {
                        type = field.Type;
                        return true;
                    }
                    if (current.Properties.TryGetValue(name, out var property))
                    {
                        type = property.Type;
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
                foreach (var extended in iface.ExtendedInterfaces)
                {
                    if (TryGetReadableMemberType(extended, name, out type))
                        return true;
                }
                break;
        }

        type = Void;
        return false;
    }

    private static bool TryGetMethod(TsType source, string name, out TsMethod method)
    {
        source = UnwrapGenericDefinition(source);

        switch (source)
        {
            case TsClassType cls:
                for (var current = cls; current != null; current = current.BaseType)
                {
                    if (current.Methods.TryGetValue(name, out method!))
                        return true;
                }
                break;
            case TsInterfaceType iface:
                if (iface.Methods.TryGetValue(name, out method!))
                    return true;
                foreach (var extended in iface.ExtendedInterfaces)
                {
                    if (TryGetMethod(extended, name, out method))
                        return true;
                }
                break;
        }

        method = null!;
        return false;
    }

    private static bool IsFunctionAssignable(TsFunctionType source, TsFunctionType target)
    {
        if (source.Parameters.Count > target.Parameters.Count)
            return false;

        for (int i = 0; i < source.Parameters.Count; i++)
        {
            if (!IsCompatibleWith(target.Parameters[i].Type, source.Parameters[i].Type))
                return false;
        }

        return target.ReturnType.Equals(Void) || IsCompatibleWith(source.ReturnType, target.ReturnType);
    }

    // Lossless implicit widenings only: narrower ints into wider ints of the
    // same signedness family, and any int into the floating types. Narrowing
    // and float→int always stay explicit.
    private static bool IsImplicitNumericWidening(TsType source, TsType target)
    {
        if (source is not TsPrimitiveType s || target is not TsPrimitiveType t)
            return false;
        if (!s.IsNumericType || !t.IsNumericType)
            return false;

        // number ≡ float64 for interop with TypeScript-style annotations.
        string src = s.Name == "number" ? "float64" : s.Name;
        string dst = t.Name == "number" ? "float64" : t.Name;
        if (src == dst) return true;

        return dst switch
        {
            "int16" => src is "int8" or "uint8",
            "int32" => src is "int8" or "uint8" or "int16" or "uint16",
            "int64" => src is "int8" or "uint8" or "int16" or "uint16" or "int32" or "uint32",
            "uint16" => src is "uint8",
            "uint32" => src is "uint8" or "uint16",
            "uint64" => src is "uint8" or "uint16" or "uint32",
            "float32" => src is "int8" or "uint8" or "int16" or "uint16",
            "float64" => src is "int8" or "uint8" or "int16" or "uint16" or "int32" or "uint32" or "float32",
            "decimal" => src is "int8" or "uint8" or "int16" or "uint16" or "int32" or "uint32" or "int64" or "uint64",
            _ => false
        };
    }

    // Primitive type singletons
    public static readonly TsPrimitiveType Void = new("void", true);
    public static readonly TsNullType Null = new();
    public static readonly TsPrimitiveType Bool = new("bool", true);
    public static readonly TsPrimitiveType Int8 = new("int8", true);
    public static readonly TsPrimitiveType UInt8 = new("uint8", true);
    public static readonly TsPrimitiveType Int16 = new("int16", true);
    public static readonly TsPrimitiveType UInt16 = new("uint16", true);
    public static readonly TsPrimitiveType Int32 = new("int32", true);
    public static readonly TsPrimitiveType UInt32 = new("uint32", true);
    public static readonly TsPrimitiveType Int64 = new("int64", true);
    public static readonly TsPrimitiveType UInt64 = new("uint64", true);
    public static readonly TsPrimitiveType Float32 = new("float32", true);
    public static readonly TsPrimitiveType Float64 = new("float64", true);
    public static readonly TsPrimitiveType Decimal = new("decimal", true);
    public static readonly TsPrimitiveType BigInt = new("bigint", true);
    public static readonly TsPrimitiveType String = new("string", false);
    public static readonly TsPrimitiveType Bytes = new("bytes", false);
    public static readonly TsPrimitiveType DateTime = new("datetime", false);
    public static readonly TsPrimitiveType Guid = new("guid", false);
    public static readonly TsPrimitiveType Number = new("number", true); // alias for float64
    public static readonly TsAnyType Any = new();

    public static TsType FromToken(TokenKind kind) => kind switch
    {
        TokenKind.VoidKeyword => Void,
        TokenKind.BoolKeyword => Bool,
        TokenKind.Int8Keyword => Int8,
        TokenKind.UInt8Keyword => UInt8,
        TokenKind.Int16Keyword => Int16,
        TokenKind.UInt16Keyword => UInt16,
        TokenKind.Int32Keyword => Int32,
        TokenKind.UInt32Keyword => UInt32,
        TokenKind.Int64Keyword => Int64,
        TokenKind.UInt64Keyword => UInt64,
        TokenKind.Float32Keyword => Float32,
        TokenKind.Float64Keyword => Float64,
        TokenKind.DecimalKeyword => Decimal,
        TokenKind.BigintKeyword => BigInt,
        TokenKind.StringKeyword => String,
        TokenKind.BytesKeyword => Bytes,
        TokenKind.DateTimeKeyword => DateTime,
        TokenKind.GuidKeyword => Guid,
        TokenKind.NumberKeyword => Number,
        TokenKind.AnyKeyword => Any,
        _ => throw new ArgumentException($"Token kind {kind} is not a primitive type")
    };

    public bool IsNumeric => this is TsPrimitiveType p && p.IsNumericType;
}

public sealed class TsAnyType : TsType
{
    public override string Name => "any";
    public override bool IsValueType => false;
    public override bool IsReferenceType => true;
    public override bool IsAssignableTo(TsType other) => true;
}

public sealed class TsPrimitiveType : TsType
{
    public override string Name { get; }
    public override bool IsValueType { get; }
    public override bool IsReferenceType => !IsValueType;
    public bool IsNumericType { get; }

    internal TsPrimitiveType(string name, bool isValueType)
    {
        Name = name;
        IsValueType = isValueType;
        IsNumericType = name is "int8" or "uint8" or "int16" or "uint16" or
            "int32" or "uint32" or "int64" or "uint64" or
            "float32" or "float64" or "decimal" or "bigint" or "number";
    }
}

public sealed class TsNullType : TsType
{
    public override string Name => "null";
    public override bool IsValueType => false;
    public override bool IsReferenceType => true;

    public override bool IsAssignableTo(TsType other)
    {
        if (other is TsNullableType) return true;
        if (other is TsUnionType u) return u.Types.Any(t => t is TsNullableType);
        return false;
    }
}

public sealed class TsClassType : TsType
{
    public override string Name { get; }
    public override bool IsValueType => false;
    public override bool IsReferenceType => true;
    public TsClassType? BaseType { get; set; }
    public List<TsType> ImplementedInterfaces { get; } = new();
    public Dictionary<string, TsField> Fields { get; } = new();
    public Dictionary<string, TsProperty> Properties { get; } = new();
    public Dictionary<string, TsMethod> Methods { get; } = new();
    public TsMethod? Constructor { get; set; }
    public List<TsTypeParameter> TypeParameters { get; } = new();
    public int GenerationId { get; set; }

    public TsClassType(string name)
    {
        Name = name;
    }

    public override bool IsAssignableTo(TsType other)
    {
        if (Equals(other)) return true;
        if (BaseType != null) return BaseType.IsAssignableTo(other);
        return ImplementedInterfaces.Any(i => i.IsAssignableTo(other));
    }
}

public sealed class TsInterfaceType : TsType
{
    public override string Name { get; }
    public override bool IsValueType => false;
    public override bool IsReferenceType => true;
    public Dictionary<string, TsProperty> Properties { get; } = new();
    public Dictionary<string, TsMethod> Methods { get; } = new();
    public List<TsType> ExtendedInterfaces { get; } = new();
    public List<TsTypeParameter> TypeParameters { get; } = new();
    public bool IsStructural { get; set; } = true;

    public TsInterfaceType(string name)
    {
        Name = name;
    }

    public override bool IsAssignableTo(TsType other)
    {
        if (Equals(other)) return true;
        if (other is TsInterfaceType iface)
        {
            return iface.Properties.All(p =>
                Properties.ContainsKey(p.Key) &&
                IsCompatibleWith(Properties[p.Key].Type, p.Value.Type)) &&
                iface.Methods.All(m =>
                    Methods.TryGetValue(m.Key, out var method) &&
                    new TsFunctionType(method.Parameters, method.ReturnType).IsAssignableTo(
                        new TsFunctionType(m.Value.Parameters, m.Value.ReturnType)));
        }
        return false;
    }
}

public sealed class TsEnumType : TsType
{
    public override string Name { get; }
    public override bool IsValueType => true;
    public override bool IsReferenceType => false;
    public Dictionary<string, TsEnumMember> Members { get; } = new();

    public TsEnumType(string name)
    {
        Name = name;
    }
}

public sealed class TsArrayType : TsType
{
    public TsType ElementType { get; }
    public override string Name => $"{ElementType}[]";
    public override bool IsValueType => false;
    public override bool IsReferenceType => true;

    public TsArrayType(TsType elementType)
    {
        ElementType = elementType;
    }

    public override bool IsAssignableTo(TsType other)
    {
        if (other is TsArrayType arr)
            return ElementType.IsAssignableTo(arr.ElementType) ||
                   arr.ElementType is TsAnyType || ElementType is TsAnyType;
        if (other is TsTupleType tuple)
            return tuple.ElementTypes.All(t => ElementType.IsAssignableTo(t) || t is TsAnyType);
        return base.IsAssignableTo(other);
    }
}

// Fixed-shape tuple `[A, B, …]`. Indexing with a constant yields the exact
// element type; dynamic indexing yields the union of element types.
public sealed class TsTupleType : TsType
{
    public List<TsType> ElementTypes { get; }
    public override string Name => $"[{string.Join(", ", ElementTypes.Select(t => t.Name))}]";
    public override bool IsValueType => false;
    public override bool IsReferenceType => true;

    public TsTupleType(List<TsType> elementTypes)
    {
        ElementTypes = elementTypes;
    }

    public TsType UnifiedElementType()
    {
        if (ElementTypes.Count == 0) return Any;
        var first = ElementTypes[0];
        return ElementTypes.All(t => t.Equals(first)) ? first : Any;
    }

    public override bool IsAssignableTo(TsType other)
    {
        if (other is TsTupleType tuple)
        {
            return ElementTypes.Count == tuple.ElementTypes.Count &&
                   ElementTypes.Zip(tuple.ElementTypes).All(pair => pair.First.IsAssignableTo(pair.Second));
        }
        if (other is TsArrayType arr)
            return ElementTypes.All(t => t.IsAssignableTo(arr.ElementType)) || arr.ElementType is TsAnyType;
        return base.IsAssignableTo(other);
    }
}

public sealed class TsMapType : TsType
{
    public TsType KeyType { get; }
    public TsType ValueType { get; }
    public override string Name => $"Map<{KeyType}, {ValueType}>";
    public override bool IsValueType => false;
    public override bool IsReferenceType => true;

    public TsMapType(TsType keyType, TsType valueType)
    {
        KeyType = keyType;
        ValueType = valueType;
    }
}

public sealed class TsNullableType : TsType
{
    public TsType ElementType { get; }
    public override string Name => $"{ElementType}?";
    public override bool IsValueType => false;
    public override bool IsReferenceType => true;

    public TsNullableType(TsType elementType)
    {
        ElementType = elementType;
    }

    public override bool IsAssignableTo(TsType other)
    {
        if (other is TsNullableType nullable) return ElementType.IsAssignableTo(nullable.ElementType);
        return false;
    }
}

public sealed class TsPromiseType : TsType
{
    public TsType ElementType { get; }
    public override string Name => $"Promise<{ElementType}>";
    public override bool IsValueType => false;
    public override bool IsReferenceType => true;

    public TsPromiseType(TsType elementType)
    {
        ElementType = elementType;
    }
}

public sealed class TsFunctionType : TsType
{
    public List<TsParameter> Parameters { get; }
    public TsType ReturnType { get; }
    public override string Name => $"({string.Join(", ", Parameters.Select(p => p.Type.Name))}) => {ReturnType.Name}";
    public override bool IsValueType => false;
    public override bool IsReferenceType => true;

    public TsFunctionType(List<TsParameter> parameters, TsType returnType)
    {
        Parameters = parameters;
        ReturnType = returnType;
    }

    public override bool IsAssignableTo(TsType other)
    {
        if (Equals(other)) return true;
        if (other is not TsFunctionType target)
            return false;
        if (Parameters.Count > target.Parameters.Count)
            return false;

        for (int i = 0; i < Parameters.Count; i++)
        {
            // A callback that accepts a wider parameter type can stand in for
            // one that will be called with a narrower type.
            if (!IsCompatibleWith(target.Parameters[i].Type, Parameters[i].Type))
                return false;
        }

        // TypeScript allows callbacks that return a value where void is
        // expected; the caller is free to ignore that result.
        return target.ReturnType.Equals(Void) || IsCompatibleWith(ReturnType, target.ReturnType);
    }
}

public sealed class TsUnionType : TsType
{
    public List<TsType> Types { get; }
    public override string Name => string.Join(" | ", Types.Select(t => t.Name));
    public override bool IsValueType => Types.All(t => t.IsValueType);
    public override bool IsReferenceType => Types.Any(t => t.IsReferenceType);

    public TsUnionType(List<TsType> types)
    {
        Types = types;
    }

    public override bool IsAssignableTo(TsType other)
    {
        return Types.Any(t => t.IsAssignableTo(other));
    }
}

public sealed class TsTypeParameter : TsType
{
    public string ParameterName { get; }
    public override string Name => ParameterName;
    public override bool IsValueType => false;
    public override bool IsReferenceType => true;
    public TsType? Constraint { get; set; }

    public TsTypeParameter(string parameterName)
    {
        ParameterName = parameterName;
    }
}

public sealed class TsGenericType : TsType
{
    public TsType Definition { get; }
    public List<TsType> TypeArguments { get; }
    public override string Name => $"{Definition.Name}<{string.Join(", ", TypeArguments.Select(t => t.Name))}>";
    public override bool IsValueType => Definition.IsValueType;
    public override bool IsReferenceType => Definition.IsReferenceType;

    public TsGenericType(TsType definition, List<TsType> typeArguments)
    {
        Definition = definition;
        TypeArguments = typeArguments;
    }
}

// Members
public sealed class TsField
{
    public string Name { get; }
    public TsType Type { get; }
    public bool IsReadonly { get; set; }
    public bool IsStatic { get; set; }
    public TsAccessModifier AccessModifier { get; set; }

    public TsField(string name, TsType type)
    {
        Name = name;
        Type = type;
    }
}

public sealed class TsProperty
{
    public string Name { get; }
    public TsType Type { get; }
    public bool IsReadonly { get; set; }
    public TsAccessModifier AccessModifier { get; set; }

    public TsProperty(string name, TsType type)
    {
        Name = name;
        Type = type;
    }
}

public sealed class TsMethod
{
    public string Name { get; }
    public TsType ReturnType { get; }
    public List<TsParameter> Parameters { get; }
    public bool IsStatic { get; set; }
    public bool IsAsync { get; set; }
    public TsAccessModifier AccessModifier { get; set; }

    public TsMethod(string name, TsType returnType, List<TsParameter> parameters)
    {
        Name = name;
        ReturnType = returnType;
        Parameters = parameters;
    }
}

public sealed class TsParameter
{
    public string Name { get; }
    public TsType Type { get; }
    public bool HasDefault { get; set; }
    public object? DefaultValue { get; set; }

    public TsParameter(string name, TsType type)
    {
        Name = name;
        Type = type;
    }
}

public sealed class TsEnumMember
{
    public string Name { get; }
    public int Value { get; set; }

    public TsEnumMember(string name, int value)
    {
        Name = name;
        Value = value;
    }
}

public enum TsAccessModifier
{
    Public,
    Private,
    Protected,
    Internal,
}
