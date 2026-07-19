using TypeSharp.VM.Memory;

namespace TypeSharp.Interop.Marshalling;

public static class Marshaller
{
    public static TsValue FromManaged(object? value)
    {
        if (value == null) return TsValue.Null;

        if (value is TsValue tv) return tv;

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
        var array = new TsArray(bytes.Length);
        foreach (var value in bytes)
            array.Add(TsValue.FromInt32(value));
        return new TsArrayValue(array);
    }

    public static object? ToManaged(TsValue value, Type targetType)
    {
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
                   value is TsFloat64Value dv ? (float)dv.Value : 0f;

        if (targetType == typeof(double) || targetType == typeof(double?))
            return value is TsFloat64Value dv ? dv.Value :
                   value is TsFloat32Value fv ? fv.Value : 0.0;

        if (targetType == typeof(decimal) || targetType == typeof(decimal?))
            return value is TsDecimalValue decv ? decv.Value :
                   value is TsFloat64Value dv ? (decimal)dv.Value : 0m;

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
