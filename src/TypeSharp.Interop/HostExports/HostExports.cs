using System.Reflection;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Semantics.TypeSystem;
using TypeSharp.Syntax;
using TypeSharp.VM.Memory;

namespace TypeSharp.Interop.HostExports;

public enum ExportMode
{
    ExplicitOnly,
    Public,
    All
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Property)]
public sealed class TsExportAttribute : Attribute
{
    public string? Name { get; }
    public TsExportAttribute(string? name = null) => Name = name;
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class TsModuleAttribute : Attribute
{
    public string Name { get; }
    public TsModuleAttribute(string name) => Name = name;
}

public interface IHostService
{
    string ServiceName { get; }
    object GetService();
}

public sealed class HostFunctionDescriptor
{
    public string ModuleName { get; }
    public string FunctionName { get; }
    public Type ReturnType { get; }
    public Type[]? ParameterTypes { get; }
    public Func<TsValue[], TsValue?> Implementation { get; }

    public HostFunctionDescriptor(string moduleName, string functionName,
        Type returnType, Type[]? parameterTypes, Func<TsValue[], TsValue?> implementation)
    {
        ModuleName = moduleName;
        FunctionName = functionName;
        ReturnType = returnType;
        ParameterTypes = parameterTypes;
        Implementation = implementation;
    }
}

public sealed class HostRegistry
{
    private readonly Dictionary<string, HostFunctionDescriptor> _functions = new();
    private readonly Dictionary<string, IHostService> _services = new();

    private static readonly HashSet<string> ObjectMethodNames = new(StringComparer.Ordinal)
    {
        "ToString", "GetHashCode", "Equals", "GetType",
        "ReferenceEquals", "MemberwiseClone"
    };

    public IReadOnlyDictionary<string, HostFunctionDescriptor> Functions => _functions;
    public IReadOnlyDictionary<string, IHostService> Services => _services;

    public void RegisterFunction(HostFunctionDescriptor descriptor)
    {
        string key = $"{descriptor.ModuleName}.{descriptor.FunctionName}";
        _functions[key] = descriptor;
    }

    public void RegisterService(IHostService service)
    {
        _services[service.ServiceName] = service;
    }

    public HostFunctionDescriptor? GetFunction(string module, string name)
    {
        return _functions.TryGetValue($"{module}.{name}", out var desc) ? desc : null;
    }

    public IReadOnlyDictionary<string, Symbol> CreateGlobalSymbols()
    {
        var symbols = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        var location = new SourceRange(new SourceLocation("<host>", 1, 1, 0), new SourceLocation("<host>", 1, 1, 0));

        foreach (var group in _functions.Values.GroupBy(f => f.FunctionName, StringComparer.Ordinal))
        {
            if (group.Count() != 1)
                continue; // Ambiguous host names require a future qualified import surface.

            var descriptor = group.Single();
            var symbol = new FunctionSymbol(descriptor.FunctionName, MapManagedType(descriptor.ReturnType), location)
            {
                HasDynamicSignature = descriptor.ParameterTypes == null
            };
            if (descriptor.ParameterTypes != null)
            {
                for (int i = 0; i < descriptor.ParameterTypes.Length; i++)
                    symbol.Parameters.Add(new ParameterSymbol($"arg{i}", MapManagedType(descriptor.ParameterTypes[i]), location));
            }
            symbols[symbol.Name] = symbol;
        }
        return symbols;
    }

    private static TsType MapManagedType(Type type)
    {
        if (type == typeof(void)) return TsType.Void;
        if (type == typeof(Task) || type == typeof(ValueTask)) return new TsPromiseType(TsType.Void);
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            return new TsPromiseType(MapManagedType(type.GetGenericArguments()[0]));
        if (type == typeof(bool)) return TsType.Bool;
        if (type == typeof(int)) return TsType.Int32;
        if (type == typeof(long)) return TsType.BigInt;
        if (type == typeof(ulong)) return TsType.BigInt;
        if (type == typeof(float)) return TsType.Float32;
        if (type == typeof(double)) return TsType.Float64;
        if (type == typeof(decimal)) return TsType.Decimal;
        if (type == typeof(string)) return TsType.String;
        if (type == typeof(byte[])) return new TsClassType("Uint8Array");
        return TsType.Any;
    }

    public void RegisterObject<T>(string moduleName, T instance, ExportMode mode = ExportMode.ExplicitOnly) where T : class
    {
        // Reflect over the runtime type: callers often pass the instance as
        // `object`, and typeof(T) would then see no exported methods at all.
        var type = instance.GetType();
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var method in methods)
        {
            bool hasExport = method.GetCustomAttribute<TsExportAttribute>() != null;

            bool shouldExport = mode switch
            {
                ExportMode.ExplicitOnly => hasExport,
                ExportMode.Public => !ObjectMethodNames.Contains(method.Name),
                ExportMode.All => true,
                _ => false
            };

            if (!shouldExport) continue;

            var parameters = method.GetParameters();
            var paramTypes = parameters.Select(p => p.ParameterType).ToArray();

            var exportAttr = method.GetCustomAttribute<TsExportAttribute>();
            string funcName = exportAttr?.Name ?? method.Name;

            var funcDesc = new HostFunctionDescriptor(
                moduleName,
                funcName,
                method.ReturnType,
                paramTypes,
                args => InvokeHostMethod(instance, method, args));

            RegisterFunction(funcDesc);
        }
    }

    private static TsValue? InvokeHostMethod(object instance, System.Reflection.MethodInfo method, TsValue[] args)
    {
        var parameters = method.GetParameters();
        var managedArgs = new object?[args.Length];

        for (int i = 0; i < Math.Min(args.Length, parameters.Length); i++)
        {
            managedArgs[i] = TypeSharp.Interop.Marshalling.Marshaller.ToManaged(args[i], parameters[i].ParameterType);
        }

        try
        {
            var result = method.Invoke(instance, managedArgs);
            return TypeSharp.Interop.Marshalling.Marshaller.FromManaged(result);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }
}
