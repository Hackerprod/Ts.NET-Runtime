namespace TypeSharp.Runtime.Collections;

public sealed class TsList<T> where T : class
{
    private T[] _items;
    private int _count;

    public int Count => _count;
    public int Capacity => _items.Length;
    public bool IsEmpty => _count == 0;

    public TsList(int initialCapacity = 4)
    {
        _items = new T[initialCapacity];
        _count = 0;
    }

    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
                throw new IndexOutOfRangeException();
            return _items[index];
        }
        set
        {
            if (index < 0 || index >= _count)
                throw new IndexOutOfRangeException();
            _items[index] = value;
        }
    }

    public void Add(T item)
    {
        EnsureCapacity(_count + 1);
        _items[_count++] = item;
    }

    public bool Remove(T item)
    {
        int idx = Array.IndexOf(_items, item, 0, _count);
        if (idx < 0) return false;

        Array.Copy(_items, idx + 1, _items, idx, _count - idx - 1);
        _count--;
        _items[_count] = default!;
        return true;
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _count) throw new IndexOutOfRangeException();
        Array.Copy(_items, index + 1, _items, index, _count - index - 1);
        _count--;
        _items[_count] = default!;
    }

    public T? FirstOrDefault(Func<T, bool>? predicate = null)
    {
        for (int i = 0; i < _count; i++)
        {
            if (predicate == null || predicate(_items[i]))
                return _items[i];
        }
        return default;
    }

    public T? LastOrDefault(Func<T, bool>? predicate = null)
    {
        for (int i = _count - 1; i >= 0; i--)
        {
            if (predicate == null || predicate(_items[i]))
                return _items[i];
        }
        return default;
    }

    public IReadOnlyList<T> AsReadOnly() => new ArraySegment<T>(_items, 0, _count);

    public void Clear()
    {
        Array.Clear(_items, 0, _count);
        _count = 0;
    }

    public T[] ToArray()
    {
        var result = new T[_count];
        Array.Copy(_items, result, _count);
        return result;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _items.Length) return;
        int newCapacity = Math.Max(_items.Length * 2, required);
        var newItems = new T[newCapacity];
        Array.Copy(_items, newItems, _count);
        _items = newItems;
    }
}

public sealed class TsDictionary<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _inner;

    public int Count => _inner.Count;

    public TsDictionary()
    {
        _inner = new Dictionary<TKey, TValue>();
    }

    public TsDictionary(int capacity)
    {
        _inner = new Dictionary<TKey, TValue>(capacity);
    }

    public TValue? Get(TKey key)
    {
        return _inner.TryGetValue(key, out var val) ? val : default;
    }

    public void Set(TKey key, TValue value)
    {
        _inner[key] = value;
    }

    public bool Contains(TKey key) => _inner.ContainsKey(key);

    public bool Remove(TKey key) => _inner.Remove(key);

    public IReadOnlyDictionary<TKey, TValue> AsReadOnly() => _inner;
}
