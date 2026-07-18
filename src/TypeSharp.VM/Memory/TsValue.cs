using System.Collections.Concurrent;
using TypeSharp.VM.Bytecode;

namespace TypeSharp.VM.Memory;

public abstract class TsValue
{
    public abstract TsValueType ValueType { get; }
    public abstract object? RawValue { get; }

    public static readonly TsNull Null = new();
    public static readonly TsVoid Void = new();

    public static TsValue FromInt32(int value) => new TsInt32Value(value);
    public static TsValue FromInt64(long value) => new TsInt64Value(value);
    public static TsValue FromUInt64(ulong value) => new TsUInt64Value(value);
    public static TsValue FromFloat32(float value) => new TsFloat32Value(value);
    public static TsValue FromFloat64(double value) => new TsFloat64Value(value);
    public static TsValue FromString(string value) => new TsStringValue(value);
    public static TsValue FromBool(bool value) => new TsBoolValue(value);
    public static TsValue FromObject(TsObject value) => new TsObjectValue(value);
    public static TsValue FromDecimal(decimal value) => new TsDecimalValue(value);
    public static TsValue FromNull() => Null;

    public override string ToString() => RawValue?.ToString() ?? "null";
}

public enum TsValueType
{
    Void, Null, Bool, Int32, Int64, UInt64, Float32, Float64, Decimal, String, Object, Array, Map
}

public sealed class TsVoid : TsValue
{
    public override TsValueType ValueType => TsValueType.Void;
    public override object? RawValue => null;
}

public sealed class TsNull : TsValue
{
    public override TsValueType ValueType => TsValueType.Null;
    public override object? RawValue => null;
}

public sealed class TsBoolValue : TsValue
{
    public bool Value { get; }
    public override TsValueType ValueType => TsValueType.Bool;
    public override object? RawValue => Value;
    public TsBoolValue(bool value) => Value = value;
}

public sealed class TsInt32Value : TsValue
{
    public int Value { get; }
    public override TsValueType ValueType => TsValueType.Int32;
    public override object? RawValue => Value;
    public TsInt32Value(int value) => Value = value;
}

public sealed class TsInt64Value : TsValue
{
    public long Value { get; }
    public override TsValueType ValueType => TsValueType.Int64;
    public override object? RawValue => Value;
    public TsInt64Value(long value) => Value = value;
}

public sealed class TsUInt64Value : TsValue
{
    public ulong Value { get; }
    public override TsValueType ValueType => TsValueType.UInt64;
    public override object? RawValue => Value;
    public TsUInt64Value(ulong value) => Value = value;
}

public sealed class TsFloat32Value : TsValue
{
    public float Value { get; }
    public override TsValueType ValueType => TsValueType.Float32;
    public override object? RawValue => Value;
    public TsFloat32Value(float value) => Value = value;
}

public sealed class TsFloat64Value : TsValue
{
    public double Value { get; }
    public override TsValueType ValueType => TsValueType.Float64;
    public override object? RawValue => Value;
    public TsFloat64Value(double value) => Value = value;
}

public sealed class TsDecimalValue : TsValue
{
    public decimal Value { get; }
    public override TsValueType ValueType => TsValueType.Decimal;
    public override object? RawValue => Value;
    public TsDecimalValue(decimal value) => Value = value;
}

public sealed class TsStringValue : TsValue
{
    public string Value { get; }
    public override TsValueType ValueType => TsValueType.String;
    public override object? RawValue => Value;
    public TsStringValue(string value) => Value = value;
}

public sealed class TsObjectValue : TsValue
{
    public TsObject Value { get; }
    public override TsValueType ValueType => TsValueType.Object;
    public override object? RawValue => Value;
    public TsObjectValue(TsObject value) => Value = value;
}

public sealed class TsArrayValue : TsValue
{
    public TsArray Value { get; }
    public override TsValueType ValueType => TsValueType.Array;
    public override object? RawValue => Value;
    public TsArrayValue(TsArray value) => Value = value;
}

public sealed class TsMapValue : TsValue
{
    public TsMap Value { get; }
    public override TsValueType ValueType => TsValueType.Map;
    public override object? RawValue => Value;
    public TsMapValue(TsMap value) => Value = value;
}

// Runtime objects
public sealed class TsObject
{
    public string TypeName { get; }
    public Dictionary<string, TsValue> Fields { get; } = new();
    private int _generationId;

    public TsObject(string typeName, int generationId = 0)
    {
        TypeName = typeName;
        _generationId = generationId;
    }

    public TsValue GetField(string name) =>
        Fields.TryGetValue(name, out var val) ? val : TsValue.Null;

    public void SetField(string name, TsValue value) =>
        Fields[name] = value;
}

public sealed class TsArray
{
    private TsValue[] _elements;
    private int _count;

    public int Count => _count;
    public int Capacity => _elements.Length;

    public TsArray(int initialCapacity = 4)
    {
        _elements = new TsValue[initialCapacity];
        _count = 0;
    }

    public TsValue Get(int index)
    {
        if (index < 0 || index >= _count) return TsValue.Null;
        return _elements[index];
    }

    public void Set(int index, TsValue value)
    {
        if (index < 0) return;
        EnsureCapacity(index + 1);
        _elements[index] = value;
        if (index >= _count) _count = index + 1;
    }

    public void Add(TsValue value)
    {
        EnsureCapacity(_count + 1);
        _elements[_count++] = value;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _elements.Length) return;
        int newCapacity = Math.Max(_elements.Length * 2, required);
        var newElements = new TsValue[newCapacity];
        Array.Copy(_elements, newElements, _count);
        _elements = newElements;
    }
}

public sealed class TsMap
{
    private readonly Dictionary<string, TsValue> _entries = new();

    public int Count => _entries.Count;

    public TsValue Get(string key) =>
        _entries.TryGetValue(key, out var val) ? val : TsValue.Null;

    public void Set(string key, TsValue value) =>
        _entries[key] = value;

    public bool Contains(string key) => _entries.ContainsKey(key);

    public bool Remove(string key) => _entries.Remove(key);
}

// Raised by the THROW opcode; carries the thrown script value so
// try/catch handlers can rebind it. Guard-rail exceptions (limits,
// verification) deliberately do NOT use this type and stay uncatchable.
public sealed class TsThrownException : InvalidOperationException
{
    public TsValue Value { get; }

    public TsThrownException(TsValue value)
        : base(value is TsStringValue s ? s.Value : value.ToString() ?? "error")
    {
        Value = value;
    }
}

public readonly record struct TryHandler(int HandlerOffset, int StackDepth);

// Frame for VM execution
public sealed class CallFrame
{
    public BytecodeFunction Function { get; }
    public TsValue[] Locals { get; }
    public TsValue[] Stack { get; }
    public int StackPointer { get; set; }
    public int InstructionPointer;
    public CallFrame? Caller { get; set; }
    public Stack<TryHandler>? TryHandlers;

    public CallFrame(BytecodeFunction function, CallFrame? caller = null)
    {
        Function = function;
        Locals = new TsValue[function.LocalCount + function.ParameterCount];
        Stack = new TsValue[256]; // fixed stack size for now
        StackPointer = 0;
        InstructionPointer = 0;
        Caller = caller;

        for (int i = 0; i < Locals.Length; i++)
            Locals[i] = TsValue.Null;
    }

    public void Push(TsValue value)
    {
        if (StackPointer >= Stack.Length)
            throw new InvalidOperationException("Stack overflow");
        Stack[StackPointer++] = value;
    }

    public TsValue Pop()
    {
        if (StackPointer <= 0)
            throw new InvalidOperationException("Stack underflow");
        return Stack[--StackPointer];
    }

    public TsValue Peek() => StackPointer > 0 ? Stack[StackPointer - 1] : TsValue.Null;
}

// Heap for object allocation
public sealed class TsHeap
{
    private long _logicalBytes;
    private readonly long _maxBytes;
    private long _objectsCreated;
    private long _arraysCreated;
    private long _mapsCreated;

    public long LogicalBytes => Interlocked.Read(ref _logicalBytes);
    public long ObjectsCreated => Interlocked.Read(ref _objectsCreated);
    public long ArraysCreated => Interlocked.Read(ref _arraysCreated);
    public long MapsCreated => Interlocked.Read(ref _mapsCreated);
    public long MaxBytes => _maxBytes;

    public TsHeap(long maxBytes = 64 * 1024 * 1024)
    {
        _maxBytes = maxBytes;
    }

    public TsObject AllocateObject(string typeName)
    {
        Interlocked.Add(ref _logicalBytes, 128);
        Interlocked.Increment(ref _objectsCreated);
        return new TsObject(typeName);
    }

    public TsObject AllocateObject(string typeName, int id)
    {
        Interlocked.Add(ref _logicalBytes, 128);
        Interlocked.Increment(ref _objectsCreated);
        return new TsObject(typeName, id);
    }

    public TsArray AllocateArray(int initialCapacity = 4)
    {
        Interlocked.Add(ref _logicalBytes, 64 + (long)initialCapacity * 8);
        Interlocked.Increment(ref _arraysCreated);
        return new TsArray(initialCapacity);
    }

    public TsMap AllocateMap()
    {
        Interlocked.Add(ref _logicalBytes, 64);
        Interlocked.Increment(ref _mapsCreated);
        return new TsMap();
    }

    public bool IsOverLimit() => Interlocked.Read(ref _logicalBytes) > _maxBytes;

    public void Reset()
    {
        Interlocked.Exchange(ref _logicalBytes, 0);
        Interlocked.Exchange(ref _objectsCreated, 0);
        Interlocked.Exchange(ref _arraysCreated, 0);
        Interlocked.Exchange(ref _mapsCreated, 0);
    }
}
