using System.Reflection;
using TypeSharp.Interop.HostExports;
using TypeSharp.Interop.Marshalling;
using TypeSharp.Runtime.Objects;
using TypeSharp.VM.Memory;

namespace TypeSharp.Interop.Proxies;

public sealed class HostProxyGenerator
{
    private readonly HostRegistry _registry;

    public HostProxyGenerator(HostRegistry registry)
    {
        _registry = registry;
    }

    public object GenerateInterfaceProxy(Type interfaceType, string moduleName)
    {
        return new DynamicHostProxy(_registry, interfaceType, moduleName);
    }
}

public sealed class DynamicHostProxy
{
    private readonly HostRegistry _registry;
    private readonly Type _interfaceType;
    private readonly string _moduleName;

    public DynamicHostProxy(HostRegistry registry, Type interfaceType, string moduleName)
    {
        _registry = registry;
        _interfaceType = interfaceType;
        _moduleName = moduleName;
    }

    public object? InvokeMethod(string methodName, params TsValue[] args)
    {
        var desc = _registry.GetFunction(_moduleName, methodName);
        if (desc == null)
            throw new MissingMethodException(_moduleName, methodName);
        return desc.Implementation(args);
    }
}

public sealed class TypeSharpProxy
{
    private readonly HostRegistry _registry;

    public TypeSharpProxy(HostRegistry registry)
    {
        _registry = registry;
    }

    public TsValue? InvokeHostFunction(string module, string function, TsValue[] args)
    {
        var descriptor = _registry.GetFunction(module, function);
        if (descriptor == null)
            return null;

        return descriptor.Implementation(args);
    }

    public TsValue WrapObject(string typeName, object instance)
    {
        var obj = new TsObject(typeName);

        var type = instance.GetType();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in properties)
        {
            if (prop.CanRead)
            {
                var value = prop.GetValue(instance);
                obj.SetField(prop.Name, Marshaller.FromManaged(value));
            }
        }

        return new TsObjectValue(obj);
    }

    public object? UnwrapObject(TsValue value, Type targetType)
    {
        return Marshaller.ToManaged(value, targetType);
    }
}
