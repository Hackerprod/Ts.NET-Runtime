namespace TypeSharp.Semantics.Symbols;

public sealed class SymbolScope
{
    private readonly Dictionary<string, Symbol> _symbols = new();
    private readonly SymbolScope? _parent;

    public SymbolScope(SymbolScope? parent = null)
    {
        _parent = parent;
    }

    public void Define(Symbol symbol)
    {
        // Redefinition replaces (JS-style shadowing); duplicate `const` in one
        // scope is a linting concern, not a binder crash.
        _symbols[symbol.Name] = symbol;
    }

    public Symbol? Lookup(string name)
    {
        if (_symbols.TryGetValue(name, out var symbol))
            return symbol;
        return _parent?.Lookup(name);
    }

    public bool Contains(string name) => _symbols.ContainsKey(name);

    public IReadOnlyDictionary<string, Symbol> GetAll() => _symbols;

    public SymbolScope CreateChild() => new(this);
}

public sealed class SymbolTable
{
    private readonly Stack<SymbolScope> _scopes = new();
    private readonly List<SymbolScope> _allScopes = new();

    public SymbolScope GlobalScope { get; }

    public SymbolTable()
    {
        GlobalScope = new SymbolScope();
        _scopes.Push(GlobalScope);
        _allScopes.Add(GlobalScope);
    }

    public SymbolScope CurrentScope => _scopes.Peek();

    public void PushScope()
    {
        var child = CurrentScope.CreateChild();
        _scopes.Push(child);
        _allScopes.Add(child);
    }

    public void PopScope()
    {
        if (_scopes.Count > 1)
            _scopes.Pop();
    }

    public void Define(Symbol symbol) => CurrentScope.Define(symbol);

    public Symbol? Lookup(string name) => CurrentScope.Lookup(name);

    public bool IsDefined(string name) => CurrentScope.Contains(name);
}
