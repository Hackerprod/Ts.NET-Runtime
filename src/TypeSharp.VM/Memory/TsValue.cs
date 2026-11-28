using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
    public static TsValue FromRegex(string pattern, string flags) => new TsRegexValue(pattern, flags);
    public static TsValue FromGenerator(TsArray values) => new TsGeneratorValue(values);
    public static TsValue FromNull() => Null;

    public override string ToString() => RawValue?.ToString() ?? "null";
}

public enum TsValueType
{
    Void, Null, Bool, Int32, Int64, UInt64, BigInt, Float32, Float64, Decimal, String, Object, Array, Map, Set, Uint8Array, Promise, Regex, Generator
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
    private readonly byte[] _backing;
    private readonly int _offset;
    private readonly bool _isView;

    /// <summary>
    /// Returns the original live storage for a root, or a fresh copy of just
    /// the logical bytes for a view (including full-size and empty views).
    /// </summary>
    public byte[] Value => _isView ? CopyBytes() : _backing;
    public override TsValueType ValueType => TsValueType.Uint8Array;
    public override object? RawValue => Value;
    public int Length { get; }

    /// <summary>Wraps the supplied bytes without copying.</summary>
    public TsUint8ArrayValue(byte[] value)
    {
        _backing = value ?? Array.Empty<byte>();
        Length = _backing.Length;
    }

    private TsUint8ArrayValue(byte[] backing, int offset, int length)
    {
        _backing = backing;
        _offset = offset;
        Length = length;
        _isView = true;
    }

    private Span<byte> Window => _backing.AsSpan(_offset, Length);

    public byte Get(int index) =>
        index >= 0 && index < Length ? _backing[_offset + index] : (byte)0;

    public void Set(int index, TsValue value)
    {
        if (index < 0 || index >= Length) return;
        SetByte(index, Uint8ArrayOperations.ToUint8(value));
    }

    internal void SetByte(int index, byte value) => _backing[_offset + index] = value;

    public TsUint8ArrayValue Slice(int start, int end)
    {
        start = Math.Clamp(start, 0, Length);
        end = Math.Clamp(end, start, Length);
        var bytes = new byte[end - start];
        Window.Slice(start, bytes.Length).CopyTo(bytes);
        return new TsUint8ArrayValue(bytes);
    }

    internal TsUint8ArrayValue Subarray(int start, int end) =>
        new(_backing, _offset + start, end - start);

    internal byte[] CopyBytes()
    {
        // Span.ToArray can return a shared singleton for a zero-length view.
        var bytes = new byte[Length];
        Window.CopyTo(bytes);
        return bytes;
    }

    internal void Reverse() => Window.Reverse();

    internal void CopyTo(TsUint8ArrayValue target, int start) =>
        Window.CopyTo(target.Window.Slice(start, Length)); // Overlap-safe in both directions.

    public override string ToString()
    {
        if (Length == 0) return string.Empty;
        var text = new System.Text.StringBuilder();
        var bytes = Window;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(bytes[i]);
        }
        return text.ToString();
    }
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

public sealed class TsRegexValue : TsValue
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public string Pattern { get; }
    public string Flags { get; }
    public Regex Regex { get; }
    public override TsValueType ValueType => TsValueType.Regex;
    public override object? RawValue => Regex;

    public TsRegexValue(string pattern, string flags)
    {
        Pattern = pattern;
        Flags = flags;
        Regex = new Regex(pattern, ToOptions(flags), MatchTimeout);
    }

    public bool Test(string input) => Regex.IsMatch(input);

    public override string ToString() => $"/{Pattern}/{Flags}";

    private static RegexOptions ToOptions(string flags)
    {
        var options = RegexOptions.CultureInvariant;
        foreach (var flag in flags)
        {
            options |= flag switch
            {
                'i' => RegexOptions.IgnoreCase,
                'm' => RegexOptions.Multiline,
                's' => RegexOptions.Singleline,
                _ => RegexOptions.None
            };
        }
        return options;
    }
}

public sealed class TsGeneratorValue : TsValue
{
    private readonly TsArray? _legacyValues;
    private int _legacyIndex;

    public override TsValueType ValueType => TsValueType.Generator;
    public override object? RawValue => Frame ?? (object?)_legacyValues;

    public BytecodeModule? Module { get; }
    public CallFrame? Frame { get; }
    public bool Done { get; set; }
    public bool Running { get; set; }

    public TsGeneratorValue(BytecodeModule module, CallFrame frame)
    {
        Module = module;
        Frame = frame;
    }

    public TsGeneratorValue(TsArray values)
    {
        _legacyValues = values;
    }

    public bool TryNextLegacy(out TsValue value, out bool done)
    {
        value = TsValue.Void;
        done = false;
        if (_legacyValues == null)
            return false;

        if (_legacyIndex >= _legacyValues.Count)
        {
            done = true;
            return true;
        }

        value = _legacyValues.Get(_legacyIndex++);
        return true;
    }

    public override string ToString() => "[object Generator]";
}

// Runtime objects
public sealed class TsObject
{
    public string TypeName { get; }
    public Dictionary<string, TsValue> Fields { get; } = new();
    private int _generationId;
    // Native Date brand and timestamp, independent of script-visible Fields.
    // Null means unbranded; a present NaN is a branded invalid Date.
    internal double? NativeDateTimestamp { get; }

    internal TsObject(double timestamp) : this("Date") => NativeDateTimestamp = timestamp;

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
    private bool[]? _presence;
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
        // JavaScript array reads outside the current length yield undefined;
        // this is also what makes destructuring defaults observable.
        if (index < 0 || index >= _count) return TsValue.Void;
        if (_presence != null && !_presence[index]) return TsValue.Void;
        return _elements[index];
    }

    public void Set(int index, TsValue value)
    {
        if (index < 0) return;
        EnsureCapacity(index + 1);
        if (index >= _count)
        {
            var presence = EnsurePresence();
            for (int i = _count; i < index; i++)
                presence[i] = false;
            presence[index] = true;
            _count = index + 1;
        }
        else
        {
            var presence = _presence;
            if (presence != null) presence[index] = true;
        }
        _elements[index] = value;
    }

    public void Add(TsValue value)
    {
        EnsureCapacity(_count + 1);
        if (_presence != null) _presence[_count] = true;
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
        if (_presence != null)
        {
            Array.Copy(_presence, index + 1, _presence, index, _count - index - 1);
            _presence[_count - 1] = false;
        }
        _count--;
    }

    public void Insert(int index, TsValue value)
    {
        if (index < 0) index = 0;
        if (index > _count) index = _count;
        EnsureCapacity(_count + 1);
        Array.Copy(_elements, index, _elements, index + 1, _count - index);
        _elements[index] = value;
        if (_presence != null)
        {
            Array.Copy(_presence, index, _presence, index + 1, _count - index);
            _presence[index] = true;
        }
        _count++;
    }

    public void DeleteAt(int index)
    {
        if (index < 0 || index >= _count) return;
        var presence = EnsurePresence();
        _elements[index] = TsValue.Void;
        presence[index] = false;
    }

    // Extends the array when necessary and marks the target slot absent.
    // This is intentionally distinct from Set(index, TsValue.Void): a hole
    // is observable by array iteration methods even though reading it yields
    // undefined.
    public void SetHole(int index)
    {
        if (index < 0) return;
        EnsureCapacity(index + 1);
        var presence = EnsurePresence();

        if (index >= _count)
        {
            Array.Clear(presence, _count, index - _count);
            _count = index + 1;
        }

        _elements[index] = TsValue.Void;
        presence[index] = false;
    }

    public bool IsHole(int index)
    {
        if (index < 0 || index >= _count) return true;
        return _presence != null && !_presence[index];
    }

    public void Reverse()
    {
        Array.Reverse(_elements, 0, _count);
        if (_presence != null) Array.Reverse(_presence, 0, _count);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _elements.Length) return;
        int newCapacity = Math.Max(_elements.Length * 2, required);
        var newElements = new TsValue[newCapacity];
        Array.Copy(_elements, newElements, _count);
        _elements = newElements;
        if (_presence != null)
        {
            var newPresence = new bool[newCapacity];
            Array.Copy(_presence, newPresence, _count);
            _presence = newPresence;
        }
    }

    private bool[] EnsurePresence()
    {
        if (_presence == null)
        {
            _presence = new bool[_elements.Length];
            for (int i = 0; i < _count; i++)
                _presence[i] = true;
        }

        return _presence;
    }
}

public sealed class TsMap
{
    private readonly List<KeyValuePair<TsValue, TsValue>> _entries = new();
    private readonly TsValueMapKeyComparer _comparer = TsValueMapKeyComparer.Instance;
    private ForEachCursor? _activeForEach;

    internal sealed class ForEachCursor(ForEachCursor? previous)
    {
        internal ForEachCursor? Previous { get; } = previous;
        internal int NextIndex { get; set; }
    }

    public int Count => _entries.Count;

    public IReadOnlyList<KeyValuePair<TsValue, TsValue>> Entries => _entries;

    public KeyValuePair<TsValue, TsValue> GetEntryAt(int index) => _entries[index];

    internal ForEachCursor BeginForEach()
    {
        var cursor = new ForEachCursor(_activeForEach);
        _activeForEach = cursor;
        return cursor;
    }

    internal bool TryReadForEachEntry(ForEachCursor cursor, out KeyValuePair<TsValue, TsValue> entry)
    {
        if (cursor.NextIndex >= _entries.Count)
        {
            entry = default;
            return false;
        }

        // Commit the unread boundary before invoking a callback that can mutate this map.
        entry = _entries[cursor.NextIndex++];
        return true;
    }

    internal void EndForEach(ForEachCursor cursor)
    {
        // Synchronous reentrant traversals unwind in LIFO order.
        System.Diagnostics.Debug.Assert(ReferenceEquals(_activeForEach, cursor));
        _activeForEach = cursor.Previous;
    }

    public TsValue Get(TsValue key)
    {
        var index = IndexOfKey(key);
        return index >= 0 ? _entries[index].Value : TsValue.Void;
    }

    public void Set(TsValue key, TsValue value)
    {
        if (key is TsFloat64Value float64 && BitConverter.DoubleToInt64Bits(float64.Value) == long.MinValue)
            key = TsValue.FromFloat64(0d);
        else if (key is TsFloat32Value float32 && BitConverter.SingleToInt32Bits(float32.Value) == int.MinValue)
            key = TsValue.FromFloat32(0f);

        var index = IndexOfKey(key);
        if (index >= 0)
        {
            _entries[index] = new KeyValuePair<TsValue, TsValue>(_entries[index].Key, value);
            return;
        }

        _entries.Add(new KeyValuePair<TsValue, TsValue>(key, value));
    }

    public bool Contains(TsValue key) => IndexOfKey(key) >= 0;

    public bool Remove(TsValue key)
    {
        var index = IndexOfKey(key);
        if (index < 0)
            return false;
        _entries.RemoveAt(index);
        for (var cursor = _activeForEach; cursor is not null; cursor = cursor.Previous)
        {
            if (index < cursor.NextIndex)
                cursor.NextIndex--;
        }
        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        for (var cursor = _activeForEach; cursor is not null; cursor = cursor.Previous)
            cursor.NextIndex = 0;
    }

    private int IndexOfKey(TsValue key)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_comparer.Equals(_entries[i].Key, key))
                return i;
        }

        return -1;
    }
}

public sealed class TsSet
{
    private readonly List<TsValue> _entries = new();
    private readonly TsValueMapKeyComparer _comparer = TsValueMapKeyComparer.Instance;

    private ForEachCursor? _activeForEach;

    internal sealed class ForEachCursor(ForEachCursor? previous)
    {
        internal ForEachCursor? Previous { get; } = previous;
        internal int NextIndex;
    }

    public int Count => _entries.Count;

    public IReadOnlyList<TsValue> Entries => _entries;

    public TsValue GetAt(int index) => _entries[index];

    internal ForEachCursor BeginForEach()
    {
        var cursor = new ForEachCursor(_activeForEach);
        _activeForEach = cursor;
        return cursor;
    }

    internal bool TryReadForEachValue(ForEachCursor cursor, out TsValue value)
    {
        if (cursor.NextIndex >= _entries.Count)
        {
            value = TsValue.Void;
            return false;
        }

        // Advance before invoking a callback that can mutate this set.
        value = _entries[cursor.NextIndex++];
        return true;
    }

    internal void EndForEach(ForEachCursor cursor)
    {
        // Synchronous reentrant traversals unwind in LIFO order.
        System.Diagnostics.Debug.Assert(ReferenceEquals(_activeForEach, cursor));
        _activeForEach = cursor.Previous;
    }

    public bool Add(TsValue value)
    {
        if (Contains(value))
            return false;
        if (value is TsFloat64Value f64 && BitConverter.DoubleToInt64Bits(f64.Value) == long.MinValue)
            value = TsValue.FromFloat64(0d);
        else if (value is TsFloat32Value f32 && BitConverter.SingleToInt32Bits(f32.Value) == int.MinValue)
            value = TsValue.FromFloat32(0f);
        _entries.Add(value);
        return true;
    }

    public bool Contains(TsValue value) => IndexOf(value) >= 0;

    public bool Remove(TsValue value)
    {
        var index = IndexOf(value);
        if (index < 0)
            return false;
        _entries.RemoveAt(index);
        for (var cursor = _activeForEach; cursor is not null; cursor = cursor.Previous)
        {
            // Deletion at the boundary already shifts the next unread value into place.
            if (index < cursor.NextIndex)
                cursor.NextIndex--;
        }
        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        for (var cursor = _activeForEach; cursor is not null; cursor = cursor.Previous)
            cursor.NextIndex = 0;
    }

    private int IndexOf(TsValue value)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_comparer.Equals(_entries[i], value))
                return i;
        }

        return -1;
    }
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
            return (x is TsNull && y is TsNull) || (x is TsVoid && y is TsVoid);
        if (x is TsStringValue xs && y is TsStringValue ys)
            return xs.Value == ys.Value;
        if (x is TsBoolValue xb && y is TsBoolValue yb)
            return xb.Value == yb.Value;
        if (IsNumber(x) && IsNumber(y))
            return SameValueZero(AsNumber(x), AsNumber(y));
        if (IsBigInt(x) && IsBigInt(y))
            return AsBigInt(x) == AsBigInt(y);
        if (x is TsObjectValue xo && y is TsObjectValue yo)
            return ReferenceEquals(xo.Value, yo.Value);
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

        // Uint8Array identity is the wrapper, not live storage or a view snapshot.
        if (obj is TsUint8ArrayValue)
            return HashCode.Combine("ref", RuntimeHelpers.GetHashCode(obj));
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
        int capacity = function.OperandStackCapacity > 0
            ? function.OperandStackCapacity
            : InitialStackSlots;
        _stack = new TsValue[capacity];
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

    // The compiler-estimated stack depth is an initial allocation hint. Runtime
    // opcodes can still produce deeper transient stacks, so frames grow on
    // demand up to the VM guardrail instead of treating the estimate as a hard
    // execution limit.
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

    internal TsObject AllocateNativeDate(double timestamp)
    {
        Interlocked.Add(ref _logicalBytes, 128);
        Interlocked.Increment(ref _objectsCreated);
        return new TsObject(timestamp);
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
