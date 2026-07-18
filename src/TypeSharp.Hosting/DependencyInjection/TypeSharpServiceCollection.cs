namespace TypeSharp.Hosting.DependencyInjection;

public sealed class TypeSharpServiceCollection
{
    private readonly Dictionary<string, object> _services = new();
    private readonly List<TypeSharpServiceRegistration> _registrations = new();

    public TypeSharpServiceCollection Register<TInterface>(string name, TInterface implementation)
        where TInterface : class
    {
        _services[name] = implementation;
        _registrations.Add(new TypeSharpServiceRegistration(
            typeof(TInterface), name, implementation));
        return this;
    }

    public TypeSharpServiceCollection RegisterSingleton<TInterface>(string name, Func<TInterface> factory)
        where TInterface : class
    {
        _registrations.Add(new TypeSharpServiceRegistration(
            typeof(TInterface), name, null, factory));
        return this;
    }

    public IReadOnlyDictionary<string, object> GetAll() => _services;

    public object? GetService(string name) =>
        _services.TryGetValue(name, out var service) ? service : null;

    public T? GetService<T>(string name) where T : class =>
        GetService(name) as T;
}

internal sealed class TypeSharpServiceRegistration
{
    public Type ServiceType { get; }
    public string Name { get; }
    public object? Instance { get; }
    public Func<object>? Factory { get; }

    public TypeSharpServiceRegistration(Type serviceType, string name, object? instance = null, Func<object>? factory = null)
    {
        ServiceType = serviceType;
        Name = name;
        Instance = instance;
        Factory = factory;
    }
}
