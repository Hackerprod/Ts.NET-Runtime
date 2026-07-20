using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
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
    public static TsValue FromBigInt(BigInteger value) => new TsBigIntValue(value);
    public static TsValue FromFloat32(float value) => new TsFloat32Value(value);
    public static TsValue FromFloat64(double value) => new TsFloat64Value(value);
    public static TsValue FromString(string value) => new TsStringValue(value);
    public static TsValue FromBool(bool value) => new TsBoolValue(value);
    public static TsValue FromObject(TsObject value) => new TsObjectValue(value);
    public static TsValue FromDecimal(decimal value) => new TsDecimalValue(value);
    public static TsValue FromUint8Array(byte[] value) => new TsUint8ArrayValue(value);
    public static TsValue FromPromise(Task<TsValue?> task) => new TsPromiseValue(task);
    public static TsValue FromNull() => Null;

    public override string ToString() => RawValue?.ToString() ?? "null";
}

public enum TsValueType
{
    Void, Null, Bool, Int32, Int64, UInt64, BigInt, Float32, Float64, Decimal, String, Object, Array, Map, Set, Uint8Array, Promise
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

public sealed class TsBigIntValue : TsValue
{
    public BigInteger Value { get; }
    public override TsValueType ValueType => TsValueType.BigInt;
    public override object? RawValue => Value;
    public TsBigIntValue(BigInteger value) => Value = value;
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

public sealed class TsUint8ArrayValue : TsValue
{
    public byte[] Value { get; }
    public override TsValueType ValueType => TsValueType.Uint8Array;
    public override object? RawValue => Value;
    public int Length => Value.Length;

    public TsUint8ArrayValue(byte[] value)
    {
        Value = value ?? Array.Empty<byte>();
    }

    public byte Get(int index) =>
        index >= 0 && index < Value.Length ? Value[index] : (byte)0;

    public void Set(int index, TsValue value)
    {
        if (index < 0 || index >= Value.Length) return;
        Value[index] = ToByte(value);
    }

    public TsUint8ArrayValue Slice(int start, int end)
    {
        start = Math.Clamp(start, 0, Value.Length);
        end = Math.Clamp(end, start, Value.Length);
        var bytes = new byte[end - start];
        Array.Copy(Value, start, bytes, 0, bytes.Length);
        return new TsUint8ArrayValue(bytes);
    }

    public override string ToString() => string.Join(",", Value);

    private static byte ToByte(TsValue value) => value switch
    {
        TsInt32Value v => (byte)v.Value,
        TsInt64Value v => (byte)v.Value,
        TsUInt64Value v => (byte)v.Value,
        TsFloat32Value v => (byte)v.Value,
        TsFloat64Value v => (byte)v.Value,
        TsDecimalValue v => (byte)v.Value,
        TsBoolValue v => v.Value ? (byte)1 : (byte)0,
        _ => 0
    };
}

// First-class function reference: names a function in the executing module.
// A closure additionally carries the boxes of its captured variables; the VM
// installs them after the declared parameters when the function is invoked.
public sealed class TsFunctionValue : TsValue
{
    public string FunctionName { get; }
    public TsValue[] Captured { get; }
    public override TsValueType ValueType => TsValueType.Object;
    public override object? RawValue => FunctionName;

    public TsFunctionValue(string functionName, TsValue[]? captured = null)
    {
        FunctionName = functionName;
        Captured = captured ?? Array.Empty<TsValue>();
    }

    public override string ToString() => $"[function {FunctionName}]";
}

public sealed class TsMapValue : TsValue
{
    public TsMap Value { get; }
    public override TsValueType ValueType => TsValueType.Map;
    public override object? RawValue => Value;
    public TsMapValue(TsMap value) => Value = value;
}

public sealed class TsSetValue : TsValue
{
    public TsSet Value { get; }
    public override TsValueType ValueType => TsValueType.Set;
    public override object? RawValue => Value;
    public TsSetValue(TsSet value) => Value = value;
}

public sealed class TsPromiseValue : TsValue
{
    private readonly Task<TsValue?> _task;

    public override TsValueType ValueType => TsValueType.Promise;
    public override object? RawValue => _task;
    public bool IsCompleted => _task.IsCompleted;

    public TsPromiseValue(Task<TsValue?> task)
    {
        _task = task ?? Task.FromResult<TsValue?>(TsValue.Void);
    }

    public static TsPromiseValue Resolved(TsValue? value) =>
        new(Task.FromResult<TsValue?>(value ?? TsValue.Void));

    public TsValue Await(CancellationToken cancellationToken = default)
    {
        try
        {
            var value = _task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
            return value ?? TsValue.Void;
        }
        catch (AggregateException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    public Task<TsValue?> AsTask() => _task;

    public override string ToString() => "[object Promise]";
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

    public void RemoveLast()
    {
        if (_count > 0) _count--;
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _count) return;
        Array.Copy(_elements, index + 1, _elements, index, _count - index - 1);
        _count--;
    }

    public void Insert(int index, TsValue value)
    {
        if (index < 0) index = 0;
        if (index > _count) index = _count;
        EnsureCapacity(_count + 1);
        Array.Copy(_elements, index, _elements, index + 1, _count - index);
        _elements[index] = value;
        _count++;
    }

    public void Reverse() => Array.Reverse(_elements, 0, _count);

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
    private readonly Dictionary<TsValue, TsValue> _entries = new(TsValueMapKeyComparer.Instance);

    public int Count => _entries.Count;

    public IEnumerable<KeyValuePair<TsValue, TsValue>> Entries => _entries;

    public TsValue Get(TsValue key) =>
        _entries.TryGetValue(key, out var val) ? val : TsValue.Null;

    public void Set(TsValue key, TsValue value) =>
        _entries[key] = value;

    public bool Contains(TsValue key) => _entries.ContainsKey(key);

    public bool Remove(TsValue key) => _entries.Remove(key);

    public void Clear() => _entries.Clear();
}

public sealed class TsSet
{
    private readonly HashSet<TsValue> _entries = new(TsValueMapKeyComparer.Instance);

    public int Count => _entries.Count;

    public bool Add(TsValue value) => _entries.Add(value);

    public bool Contains(TsValue value) => _entries.Contains(value);

    public bool Remove(TsValue value) => _entries.Remove(value);

    public void Clear() => _entries.Clear();
}

internal sealed class TsValueMapKeyComparer : IEqualityComparer<TsValue>
{
    public static TsValueMapKeyComparer Instance { get; } = new();

    private TsValueMapKeyComparer()
    {
    }

    public bool Equals(TsValue? x, TsValue? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x == null || y == null)
            return false;
        if (x is TsNull or TsVoid || y is TsNull or TsVoid)
            return x is TsNull or TsVoid && y is TsNull or TsVoid;
        if (x is TsStringValue xs && y is TsStringValue ys)
            return xs.Value == ys.Value;
        if (x is TsBoolValue xb && y is TsBoolValue yb)
            return xb.Value == yb.Value;
        if (IsNumber(x) && IsNumber(y))
            return SameValueZero(AsNumber(x), AsNumber(y));
        if (IsBigInt(x) && IsBigInt(y))
            return AsBigInt(x) == AsBigInt(y);
        return false;
    }

    public int GetHashCode(TsValue obj)
    {
        if (obj is TsNull or TsVoid)
            return HashCode.Combine("null");
        if (obj is TsStringValue s)
            return HashCode.Combine("string", s.Value);
        if (obj is TsBoolValue b)
            return HashCode.Combine("boolean", b.Value);
        if (IsNumber(obj))
        {
            var value = AsNumber(obj);
            if (double.IsNaN(value))
                return HashCode.Combine("number", "NaN");
            if (value == 0)
                value = 0;
            return HashCode.Combine("number", value);
        }
        if (IsBigInt(obj))
            return HashCode.Combine("bigint", AsBigInt(obj));

        return HashCode.Combine("ref", RuntimeHelpers.GetHashCode(obj.RawValue ?? obj));
    }

    private static bool IsNumber(TsValue value) =>
        value is TsInt32Value or TsFloat32Value or TsFloat64Value or TsDecimalValue;

    private static bool IsBigInt(TsValue value) =>
        value is TsInt64Value or TsUInt64Value or TsBigIntValue;

    private static double AsNumber(TsValue value) => value switch
    {
        TsInt32Value v => v.Value,
        TsFloat32Value v => v.Value,
        TsFloat64Value v => v.Value,
        TsDecimalValue v => (double)v.Value,
        _ => double.NaN
    };

    private static BigInteger AsBigInt(TsValue value) => value switch
    {
        TsInt64Value v => v.Value,
        TsUInt64Value v => v.Value,
        TsBigIntValue v => v.Value,
        _ => BigInteger.Zero
    };

    private static bool SameValueZero(double left, double right) =>
        left == right || double.IsNaN(left) && double.IsNaN(right);
}

// Raised by the THROW opcode; carries the thrown script value so
// try/catch handlers can rebind it. Guard-rail exceptions (limits,
// verification) deliberately do NOT use this type and stay uncatchable.
public sealed class TsThrownException : InvalidOperationException
{
    public TsValue Value { get; }

    public TsThrownException(TsValue value)
        : base(DescribeThrown(value))
    {
        Value = value;
    }

    private static string DescribeThrown(TsValue value) => value switch
    {
        TsStringValue s => s.Value,
        TsObjectValue o when o.Value.GetField("message") is TsStringValue msg => msg.Value,
        _ => value.ToString() ?? "error"
    };
}

public readonly record struct TryHandler(int HandlerOffset, int StackDepth);

// Frame for VM execution
public sealed class CallFrame
{
    private const int InitialStackSlots = 256;
    private const int MaxStackSlots = 1_048_576;
    private TsValue[] _stack;

    public BytecodeFunction Function { get; }
    public TsValue[] Locals { get; }
    public TsValue[] Stack => _stack;
    public int StackPointer { get; set; }
    public int InstructionPointer;
    public CallFrame? Caller { get; set; }
    public Stack<TryHandler>? TryHandlers;

    public CallFrame(BytecodeFunction function, CallFrame? caller = null)
    {
        Function = function;
        Locals = new TsValue[function.LocalCount + function.ParameterCount];
        _stack = new TsValue[InitialStackSlots];
        StackPointer = 0;
        InstructionPointer = 0;
        Caller = caller;

        for (int i = 0; i < Locals.Length; i++)
            Locals[i] = TsValue.Void;
    }

    public void Push(TsValue value)
    {
        EnsureStackCapacity(StackPointer + 1);
        Stack[StackPointer++] = value;
    }

    public TsValue Pop()
    {
        if (StackPointer <= 0)
            throw new InvalidOperationException("Stack underflow");

        var value = Stack[--StackPointer];
        Stack[StackPointer] = TsValue.Void;
        return value;
    }

    public TsValue Peek() => StackPointer > 0 ? Stack[StackPointer - 1] : TsValue.Null;

    private void EnsureStackCapacity(int neededSlots)
    {
        if (neededSlots <= _stack.Length)
            return;

        if (neededSlots > MaxStackSlots)
            throw new InvalidOperationException($"Operand stack limit exceeded ({neededSlots} > {MaxStackSlots})");

        var newLength = _stack.Length;
        while (newLength < neededSlots)
        {
            newLength *= 2;
            if (newLength < 0 || newLength > MaxStackSlots)
            {
                newLength = MaxStackSlots;
                break;
            }
        }

        Array.Resize(ref _stack, newLength);
    }
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

    public TsSet AllocateSet()
    {
        Interlocked.Add(ref _logicalBytes, 64);
        Interlocked.Increment(ref _mapsCreated);
        return new TsSet();
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
