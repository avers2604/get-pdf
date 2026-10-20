namespace GetPdf;

/// <summary>Bounded LRU cache. Eviction calls the provided disposer exactly once per item.</summary>
public sealed class PdfLruCache<TKey, TValue> : IDisposable where TKey : notnull
{
    private readonly int _capacity;
    private readonly Action<TValue> _onEvict;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _byKey = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _usage = new();
    private bool _disposed;

    public PdfLruCache(int capacity, Action<TValue> onEvict)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _onEvict = onEvict ?? throw new ArgumentNullException(nameof(onEvict));
    }

    public int Count => _byKey.Count;

    public bool TryGet(TKey key, out TValue? value)
    {
        ThrowIfDisposed();
        if (_byKey.TryGetValue(key, out var found))
        {
            _usage.Remove(found);
            _usage.AddFirst(found);
            value = found.Value.Value;
            return true;
        }
        value = default;
        return false;
    }

    public void Add(TKey key, TValue value)
    {
        ThrowIfDisposed();
        if (_byKey.TryGetValue(key, out var existing))
        {
            _usage.Remove(existing);
            _byKey.Remove(key);
            if (!ReferenceEquals(existing.Value.Value, value))
                _onEvict(existing.Value.Value);
        }
        var node = _usage.AddFirst((key, value));
        _byKey.Add(key, node);
        if (_byKey.Count <= _capacity) return;
        var evicted = _usage.Last!;
        _usage.RemoveLast();
        _byKey.Remove(evicted.Value.Key);
        _onEvict(evicted.Value.Value);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _usage) _onEvict(entry.Value);
        _usage.Clear();
        _byKey.Clear();
    }
}
