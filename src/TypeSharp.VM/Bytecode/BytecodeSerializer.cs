namespace TypeSharp.VM.Bytecode;

public static class BytecodeSerializer
{
    private const uint Magic = 0x43425354; // TSBC
    private const int Version = 2;
    private const int MaxCollectionLength = 1_000_000;

    public static void Serialize(Stream destination, BytecodeModule module)
    {
        ArgumentNullException.ThrowIfNull(destination);
        BytecodeVerifier.Verify(module);

        using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(module.Name);
        writer.Write(module.Functions.Length);
        foreach (var function in module.Functions)
            WriteFunction(writer, function);
    }

    public static BytecodeModule Deserialize(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var reader = new BinaryReader(source, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic)
            throw new BytecodeVerificationException("Invalid bytecode magic");
        if (reader.ReadInt32() != Version)
            throw new BytecodeVerificationException("Unsupported bytecode version");

        var name = reader.ReadString();
        int functionCount = ReadCount(reader, "function");
        var functions = new BytecodeFunction[functionCount];
        for (int i = 0; i < functions.Length; i++)
            functions[i] = ReadFunction(reader);

        var module = new BytecodeModule(name, functions);
        BytecodeVerifier.Verify(module);
        return module;
    }

    private static void WriteFunction(BinaryWriter writer, BytecodeFunction function)
    {
        writer.Write(function.Name);
        writer.Write(function.ParameterCount);
        writer.Write(function.LocalCount);
        writer.Write(function.IsAsync);
        writer.Write(function.IsGenerator);
        writer.Write(function.RestParameterIndex);
        WriteArray(writer, function.Instructions, writer.Write);
        WriteArray(writer, function.StringConstants, writer.Write);
        WriteArray(writer, function.IntegerConstants, writer.Write);
        WriteArray(writer, function.DoubleConstants, writer.Write);
        writer.Write(function.DecimalConstants.Length);
        foreach (var value in function.DecimalConstants)
        {
            foreach (var part in decimal.GetBits(value))
                writer.Write(part);
        }
    }

    private static BytecodeFunction ReadFunction(BinaryReader reader)
    {
        var name = reader.ReadString();
        int parameterCount = reader.ReadInt32();
        int localCount = reader.ReadInt32();
        bool isAsync = reader.ReadBoolean();
        bool isGenerator = reader.ReadBoolean();
        int restParameterIndex = reader.ReadInt32();
        var instructions = ReadArray(reader, "instruction", reader.ReadByte);
        var strings = ReadArray(reader, "string", reader.ReadString);
        var integers = ReadArray(reader, "integer", reader.ReadInt64);
        var doubles = ReadArray(reader, "double", reader.ReadDouble);
        int decimalCount = ReadCount(reader, "decimal");
        var decimals = new decimal[decimalCount];
        for (int i = 0; i < decimals.Length; i++)
            decimals[i] = new decimal(new[] { reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() });

        return new BytecodeFunction(name, instructions, parameterCount, localCount, isAsync,
            strings, integers, doubles, decimals, operandStackCapacity: 0, isGenerator, restParameterIndex);
    }

    private static void WriteArray<T>(BinaryWriter writer, T[] values, Action<T> write)
    {
        writer.Write(values.Length);
        foreach (var value in values)
            write(value);
    }

    private static T[] ReadArray<T>(BinaryReader reader, string name, Func<T> read)
    {
        int count = ReadCount(reader, name);
        var values = new T[count];
        for (int i = 0; i < count; i++)
            values[i] = read();
        return values;
    }

    private static int ReadCount(BinaryReader reader, string name)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaxCollectionLength)
            throw new BytecodeVerificationException($"Invalid {name} count {count}");
        return count;
    }
}
