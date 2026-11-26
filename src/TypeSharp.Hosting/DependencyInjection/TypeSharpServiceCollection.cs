namespace TypeSharp.Hosting.DependencyInjection;

public sealed class TypeSharpServiceCollection
{
    private readonly Dictionary<string, object> _services = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lazy<object>> _pending = new(StringComparer.Ordinal);

    public TypeSharpServiceCollection Register<TInterface>(string name, TInterface implementation)
        where TInterface : class
    {
        _services[name] = implementation;
        _pending.Remove(name);
        return this;
    }

    public TypeSharpServiceCollection RegisterSingleton<TInterface>(string name, Func<TInterface> factory)
        where TInterface : class
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(factory);
        _pending[name] = new Lazy<object>(() => factory());
        _services.Remove(name);
        return this;
    }

    public IReadOnlyDictionary<string, object> GetAll()
    {
        // Visit only the entry roster; callbacks can stage more keys for a later call.
        // A failure propagates without rolling back services already resolved.
        foreach (var name in _pending.Keys.ToArray())
            GetService(name);
        return _services;
    }

    public object? GetService(string name)
    {
        if (!_pending.TryGetValue(name, out var selected))
            return _services.TryGetValue(name, out var service) ? service : null;

        var value = selected.Value;
        // The callback may have replaced the current registration, even for this name.
        if (_pending.TryGetValue(name, out var current) && ReferenceEquals(current, selected))
        {
            _services[name] = value;
            _pending.Remove(name);
        }
        return value;
    }

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
