using System.Reflection;
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
    public Type[] ParameterTypes { get; }
    public Func<TsValue[], TsValue?> Implementation { get; }

    public HostFunctionDescriptor(string moduleName, string functionName,
        Type returnType, Type[] parameterTypes, Func<TsValue[], TsValue?> implementation)
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

    public void RegisterObject<T>(string moduleName, T instance, ExportMode mode = ExportMode.ExplicitOnly) where T : class
    {
        var type = typeof(T);
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

        var result = method.Invoke(instance, managedArgs);

        if (result == null) return null;

        if (result is Task<object> taskObj)
        {
            taskObj.Wait();
            var taskResult = taskObj.Result;
            return TypeSharp.Interop.Marshalling.Marshaller.FromManaged(taskResult);
        }

        var resultType = result.GetType();
        if (resultType.IsGenericType)
        {
            var genericDef = resultType.GetGenericTypeDefinition();
            if (genericDef == typeof(Task<>) || genericDef == typeof(ValueTask<>))
            {
                var getResultProperty = resultType.GetProperty("Result");
                var taskResult = getResultProperty?.GetValue(result);
                return TypeSharp.Interop.Marshalling.Marshaller.FromManaged(taskResult);
            }
        }

        if (result is Task nonGenericTask)
        {
            nonGenericTask.Wait();
            return null;
        }

        if (result is ValueTask valueTask)
        {
            valueTask.AsTask().Wait();
            return null;
        }

        return TypeSharp.Interop.Marshalling.Marshaller.FromManaged(result);
    }
}
