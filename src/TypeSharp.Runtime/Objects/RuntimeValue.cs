namespace TypeSharp.Runtime.Objects;

public abstract class RuntimeValue
{
    public abstract string TypeName { get; }
    public abstract bool IsNull { get; }

    public static readonly RuntimeNull Null = new();
}

public sealed class RuntimeNull : RuntimeValue
{
    public override string TypeName => "null";
    public override bool IsNull => true;
}

public sealed class RuntimeInt32 : RuntimeValue
{
    public int Value { get; }
    public override string TypeName => "int32";
    public override bool IsNull => false;
    public RuntimeInt32(int value) => Value = value;
}

public sealed class RuntimeInt64 : RuntimeValue
{
    public long Value { get; }
    public override string TypeName => "int64";
    public override bool IsNull => false;
    public RuntimeInt64(long value) => Value = value;
}

public sealed class RuntimeUInt64 : RuntimeValue
{
    public ulong Value { get; }
    public override string TypeName => "uint64";
    public override bool IsNull => false;
    public RuntimeUInt64(ulong value) => Value = value;
}

public sealed class RuntimeFloat32 : RuntimeValue
{
    public float Value { get; }
    public override string TypeName => "float32";
    public override bool IsNull => false;
    public RuntimeFloat32(float value) => Value = value;
}

public sealed class RuntimeFloat64 : RuntimeValue
{
    public double Value { get; }
    public override string TypeName => "float64";
    public override bool IsNull => false;
    public RuntimeFloat64(double value) => Value = value;
}

public sealed class RuntimeDecimal : RuntimeValue
{
    public decimal Value { get; }
    public override string TypeName => "decimal";
    public override bool IsNull => false;
    public RuntimeDecimal(decimal value) => Value = value;
}

public sealed class RuntimeBool : RuntimeValue
{
    public bool Value { get; }
    public override string TypeName => "bool";
    public override bool IsNull => false;
    public RuntimeBool(bool value) => Value = value;
}

public sealed class RuntimeString : RuntimeValue
{
    public string Value { get; }
    public override string TypeName => "string";
    public override bool IsNull => false;
    public RuntimeString(string value) => Value = value;
}

public sealed class RuntimeDateTime : RuntimeValue
{
    public DateTimeOffset Value { get; }
    public override string TypeName => "datetime";
    public override bool IsNull => false;
    public RuntimeDateTime(DateTimeOffset value) => Value = value;
}

public sealed class RuntimeGuid : RuntimeValue
{
    public Guid Value { get; }
    public override string TypeName => "guid";
    public override bool IsNull => false;
    public RuntimeGuid(Guid value) => Value = value;
}

public sealed class RuntimeBytes : RuntimeValue
{
    public byte[] Value { get; }
    public override string TypeName => "bytes";
    public override bool IsNull => false;
    public RuntimeBytes(byte[] value) => Value = value;
}

public sealed class RuntimeObject : RuntimeValue
{
    public override string TypeName { get; }
    public override bool IsNull => false;

    private readonly Dictionary<string, RuntimeValue> _fields = new();
    private readonly int _generationId;

    public RuntimeObject(string typeName, int generationId = 0)
    {
        TypeName = typeName;
        _generationId = generationId;
    }

    public RuntimeValue GetField(string name) =>
        _fields.TryGetValue(name, out var val) ? val : RuntimeNull.Null;

    public void SetField(string name, RuntimeValue value) =>
        _fields[name] = value;

    public IReadOnlyDictionary<string, RuntimeValue> Fields => _fields;
    public int GenerationId => _generationId;
}
