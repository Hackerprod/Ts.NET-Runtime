using TypeSharp.VM.Memory;

namespace TypeSharp.Interop.Marshalling;

public static class Marshaller
{
    public static TsValue FromManaged(object? value)
    {
        if (value == null) return TsValue.Null;

        if (value is TsValue tv) return tv;
        if (value is Task task) return FromTask(task);
        if (value is ValueTask valueTask) return FromTask(valueTask.AsTask());

        var runtimeType = value.GetType();
        if (runtimeType.IsGenericType && runtimeType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            return FromTask(ConvertGenericValueTaskToTask(value));

        return value switch
        {
            bool b => new TsBoolValue(b),
            int i => TsValue.FromInt32(i),
            long l => TsValue.FromInt64(l),
            ulong ul => TsValue.FromUInt64(ul),
            uint ui => TsValue.FromInt64(ui),
            ushort us => TsValue.FromInt32(us),
            short s => TsValue.FromInt32(s),
            byte bt => TsValue.FromInt32(bt),
            sbyte sbt => TsValue.FromInt32(sbt),
            float f => TsValue.FromFloat32(f),
            double d => TsValue.FromFloat64(d),
            decimal dec => TsValue.FromDecimal(dec),
            string str => TsValue.FromString(str),
            Guid g => TsValue.FromString(g.ToString()),
            DateTime dt => TsValue.FromString(dt.ToString("O")),
            DateTimeOffset dto => TsValue.FromString(dto.ToString("O")),
            byte[] bytes => FromByteArray(bytes),
            _ => WrapAsObject(value),
        };
    }

    public static TsValue Await(TsValue value, CancellationToken cancellationToken = default) =>
        value is TsPromiseValue promise ? promise.Await(cancellationToken) : value;

    public static async Task<TsValue> AwaitAsync(TsValue value, CancellationToken cancellationToken = default)
    {
        if (value is not TsPromiseValue promise)
            return value;

        var resolved = await promise.AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        return resolved ?? TsValue.Void;
    }

    private static TsValue FromTask(Task task)
    {
        return TsValue.FromPromise(AwaitManagedTaskAsync(task));
    }

    private static Task ConvertGenericValueTaskToTask(object valueTask)
    {
        var asTask = valueTask.GetType().GetMethod("AsTask", Type.EmptyTypes)
            ?? throw new InvalidOperationException($"ValueTask type '{valueTask.GetType()}' does not expose AsTask()");
        return (Task)asTask.Invoke(valueTask, null)!;
    }

    private static async Task<TsValue?> AwaitManagedTaskAsync(Task task)
    {
        await task.ConfigureAwait(false);

        var taskType = task.GetType();
        if (!taskType.IsGenericType)
            return TsValue.Void;

        var genericArguments = taskType.GetGenericArguments();
        if (genericArguments.Length == 1 && genericArguments[0].FullName == "System.Threading.Tasks.VoidTaskResult")
            return TsValue.Void;

        var result = taskType.GetProperty("Result")?.GetValue(task);
        return FromManaged(result);
    }

    private static TsValue WrapAsObject(object value)
    {
        var obj = new TsObject(value.GetType().Name);
        var type = value.GetType();
        var properties = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        foreach (var prop in properties)
        {
            if (prop.CanRead)
            {
                try
                {
                    var propValue = prop.GetValue(value);
                    obj.SetField(prop.Name, FromManaged(propValue));
                }
                catch
                {
                    obj.SetField(prop.Name, TsValue.Null);
                }
            }
        }

        return new TsObjectValue(obj);
    }

    private static TsValue FromByteArray(byte[] bytes)
    {
        var copy = new byte[bytes.Length];
        Array.Copy(bytes, copy, copy.Length);
        return new TsUint8ArrayValue(copy);
    }

    public static object? ToManaged(TsValue value, Type targetType)
    {
        if (value is TsPromiseValue promise)
        {
            if (targetType == typeof(Task))
                return promise.AsTask();
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Task<>))
                return ConvertPromiseToGenericTask(promise, targetType.GetGenericArguments()[0]);
            if (targetType == typeof(ValueTask))
                return new ValueTask(promise.AsTask());
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(ValueTask<>))
                return ConvertPromiseToGenericValueTask(promise, targetType.GetGenericArguments()[0]);

            value = promise.Await();
        }

        if (value is TsNull) return null;
        if (value is TsVoid) return null;

        if (targetType == typeof(TsValue))
            return value;

        if (targetType == typeof(object))
            return value.RawValue ?? value.ToString();

        if (targetType == typeof(bool) || targetType == typeof(bool?))
            return value is TsBoolValue bv ? bv.Value : false;

        if (targetType == typeof(int) || targetType == typeof(int?))
            return value is TsInt32Value iv ? iv.Value :
                   value is TsFloat64Value dv ? (int)dv.Value : 0;

        if (targetType == typeof(long) || targetType == typeof(long?))
            return value is TsInt64Value lv ? lv.Value :
                   value is TsInt32Value iv ? iv.Value :
                   value is TsFloat64Value dv ? (long)dv.Value : 0L;

        if (targetType == typeof(ulong) || targetType == typeof(ulong?))
            return value is TsUInt64Value uv ? uv.Value :
                   value is TsInt64Value lv ? (ulong)lv.Value :
                   value is TsInt32Value iv ? (ulong)iv.Value :
                   value is TsFloat64Value dv ? (ulong)dv.Value : 0UL;

        if (targetType == typeof(uint) || targetType == typeof(uint?))
            return value is TsInt32Value iv ? (uint)iv.Value :
                   value is TsInt64Value lv ? (uint)lv.Value : 0U;

        if (targetType == typeof(short) || targetType == typeof(short?))
            return value is TsInt32Value iv ? (short)iv.Value : (short)0;

        if (targetType == typeof(ushort) || targetType == typeof(ushort?))
            return value is TsInt32Value iv ? (ushort)iv.Value : (ushort)0;

        if (targetType == typeof(byte) || targetType == typeof(byte?))
            return value is TsInt32Value iv ? (byte)iv.Value : (byte)0;

        if (targetType == typeof(sbyte) || targetType == typeof(sbyte?))
            return value is TsInt32Value iv ? (sbyte)iv.Value : (sbyte)0;

        if (targetType == typeof(float) || targetType == typeof(float?))
            return value is TsFloat32Value fv ? fv.Value :
                   value is TsFloat64Value dv ? (float)dv.Value :
                   value is TsInt32Value iv ? iv.Value :
                   value is TsInt64Value lv ? lv.Value :
                   value is TsUInt64Value uv ? uv.Value : 0f;

        if (targetType == typeof(double) || targetType == typeof(double?))
            return value is TsFloat64Value dv ? dv.Value :
                   value is TsFloat32Value fv ? fv.Value :
                   value is TsInt32Value iv ? iv.Value :
                   value is TsInt64Value lv ? lv.Value :
                   value is TsUInt64Value uv ? uv.Value :
                   value is TsDecimalValue decv ? (double)decv.Value : 0.0;

        if (targetType == typeof(decimal) || targetType == typeof(decimal?))
            return value is TsDecimalValue decv ? decv.Value :
                   value is TsFloat64Value dv ? (decimal)dv.Value :
                   value is TsFloat32Value fv ? (decimal)fv.Value :
                   value is TsInt32Value iv ? iv.Value :
                   value is TsInt64Value lv ? lv.Value :
                   value is TsUInt64Value uv ? uv.Value : 0m;

        if (targetType == typeof(string))
            return value is TsStringValue sv ? sv.Value : value.ToString();

        if (targetType == typeof(Guid) || targetType == typeof(Guid?))
        {
            if (value is TsStringValue sv && Guid.TryParse(sv.Value, out var g))
                return g;
            return Guid.Empty;
        }

        if (targetType == typeof(DateTime) || targetType == typeof(DateTime?))
        {
            if (value is TsStringValue sv && DateTime.TryParse(sv.Value, out var dt))
                return dt;
            return DateTime.MinValue;
        }

        if (targetType == typeof(DateTimeOffset) || targetType == typeof(DateTimeOffset?))
        {
            if (value is TsStringValue sv && DateTimeOffset.TryParse(sv.Value, out var dto))
                return dto;
            return DateTimeOffset.MinValue;
        }

        if (targetType == typeof(byte[]))
        {
            if (value is TsUint8ArrayValue bytesValue)
            {
                var copy = new byte[bytesValue.Length];
                Array.Copy(bytesValue.Value, copy, copy.Length);
                return copy;
            }

            if (value is TsArrayValue arr)
            {
                var bytes = new byte[arr.Value.Count];
                for (int i = 0; i < arr.Value.Count; i++)
                    bytes[i] = Convert.ToByte(ToManaged(arr.Value.Get(i), typeof(byte)));
                return bytes;
            }

            if (value is TsStringValue sv)
                return Convert.FromBase64String(sv.Value);
            return Array.Empty<byte>();
        }

        return value.ToString();
    }

    public static async Task<object?> ToManagedAsync(TsValue value, Type targetType, CancellationToken cancellationToken = default)
    {
        var resolved = await AwaitAsync(value, cancellationToken).ConfigureAwait(false);
        return ToManaged(resolved, targetType);
    }

    private static object ConvertPromiseToGenericTask(TsPromiseValue promise, Type resultType)
    {
        var method = typeof(Marshaller)
            .GetMethod(nameof(ConvertPromiseToGenericTaskCore), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(resultType);
        return method.Invoke(null, new object[] { promise })!;
    }

    private static async Task<T> ConvertPromiseToGenericTaskCore<T>(TsPromiseValue promise)
    {
        var resolved = await promise.AsTask().ConfigureAwait(false) ?? TsValue.Void;
        return (T)(ToManaged(resolved, typeof(T)) ?? default(T)!);
    }

    private static object ConvertPromiseToGenericValueTask(TsPromiseValue promise, Type resultType)
    {
        var method = typeof(Marshaller)
            .GetMethod(nameof(ConvertPromiseToGenericValueTaskCore), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(resultType);
        return method.Invoke(null, new object[] { promise })!;
    }

    private static async ValueTask<T> ConvertPromiseToGenericValueTaskCore<T>(TsPromiseValue promise)
    {
        var resolved = await promise.AsTask().ConfigureAwait(false) ?? TsValue.Void;
        return (T)(ToManaged(resolved, typeof(T)) ?? default(T)!);
    }

    public static TsValue[] FromManagedArray(object?[] args, Type[] parameterTypes)
    {
        var result = new TsValue[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            result[i] = FromManaged(args[i]);
        }
        return result;
    }

    public static object?[] ToManagedArray(TsValue[] values, Type[] parameterTypes)
    {
        var result = new object?[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (i < parameterTypes.Length)
                result[i] = ToManaged(values[i], parameterTypes[i]);
            else
                result[i] = ToManaged(values[i], typeof(object));
        }
        return result;
    }
}
