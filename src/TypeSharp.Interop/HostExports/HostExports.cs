using System.Reflection;
using TypeSharp.VM.Memory;

namespace TypeSharp.Interop.HostExports;

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

    public void RegisterObject<T>(string moduleName, T instance) where T : class
    {
        var type = typeof(T);
        var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        foreach (var method in methods)
        {
            if (method.GetCustomAttribute<TsExportAttribute>() != null || method.IsPublic)
            {
                var parameters = method.GetParameters();
                var paramTypes = parameters.Select(p => p.ParameterType).ToArray();

                var funcDesc = new HostFunctionDescriptor(
                    moduleName,
                    method.Name,
                    method.ReturnType,
                    paramTypes,
                    args => InvokeHostMethod(instance, method, args));

                RegisterFunction(funcDesc);
            }
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
        return TypeSharp.Interop.Marshalling.Marshaller.FromManaged(result);
    }
}
