using System.Reflection;
using System.Reflection.Emit;
using TypeSharp.Interop.HostExports;
using TypeSharp.Interop.Marshalling;
using TypeSharp.Runtime.Objects;
using TypeSharp.VM.Memory;

namespace TypeSharp.Interop.Proxies;

public sealed class HostProxyGenerator
{
    private readonly HostRegistry _registry;
    private readonly ModuleBuilder _moduleBuilder;

    public HostProxyGenerator(HostRegistry registry)
    {
        _registry = registry;
        var assemblyName = new AssemblyName("TypeSharp.DynamicProxies");
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        _moduleBuilder = assemblyBuilder.DefineDynamicModule("Proxies");
    }

    public object GenerateInterfaceProxy(Type interfaceType)
    {
        var typeName = $"Proxy_{interfaceType.Name}_{Guid.NewGuid():N}";
        var typeBuilder = _moduleBuilder.DefineType(typeName,
            TypeAttributes.Public | TypeAttributes.Class,
            null,
            new[] { interfaceType });

        var methods = interfaceType.GetMethods();
        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            var paramTypes = parameters.Select(p => p.ParameterType).ToArray();

            var methodBuilder = typeBuilder.DefineMethod(method.Name,
                MethodAttributes.Public | MethodAttributes.Virtual,
                method.ReturnType,
                paramTypes);

            var il = methodBuilder.GetILGenerator();

            // Create TsValue array from arguments
            il.Emit(OpCodes.Ldc_I4, paramTypes.Length);
            il.Emit(OpCodes.Newarr, typeof(TsValue));

            for (int i = 0; i < paramTypes.Length; i++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Call, typeof(Marshaller).GetMethod("FromManaged")!);
                il.Emit(OpCodes.Stelem_Ref);
            }

            // Call host function
            il.Emit(OpCodes.Ldstr, "");
            il.Emit(OpCodes.Ldstr, method.Name);
            il.Emit(OpCodes.Callvirt, typeof(HostRegistry).GetMethod("GetFunction")!);

            // Extract and convert result
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
        }

        var proxyType = typeBuilder.CreateType()!;
        return Activator.CreateInstance(proxyType)!;
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
