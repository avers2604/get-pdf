namespace GetPdf;

/// <summary>Bounded LRU cache. Eviction calls the provided disposer exactly once per item.</summary>
public sealed class PdfLruCache<TKey, TValue> : IDisposable where TKey : notnull
{
    private readonly int _capacity;
    private readonly long _maxCost;
    private readonly Func<TValue, long> _estimateCost;
    private long _totalCost;
    private readonly Action<TValue> _onEvict;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value, long Cost)>> _byKey = new();
    private readonly LinkedList<(TKey Key, TValue Value, long Cost)> _usage = new();
    private bool _disposed;

    public PdfLruCache(int capacity, Action<TValue> onEvict,
        long maxCost = long.MaxValue, Func<TValue, long>? estimateCost = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxCost < 1) throw new ArgumentOutOfRangeException(nameof(maxCost));
        _capacity = capacity;
        _maxCost = maxCost;
        _estimateCost = estimateCost ?? (_ => 1L);
        _onEvict = onEvict ?? throw new ArgumentNullException(nameof(onEvict));
    }

    public int Count => _byKey.Count;
    public long EstimatedCost => _totalCost;

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
        long cost = _estimateCost(value);
        if (cost <= 0 || cost > _maxCost)
            throw new ArgumentOutOfRangeException(nameof(value), "Элемент превышает бюджет кеша.");
        if (_byKey.TryGetValue(key, out var existing))
        {
            _usage.Remove(existing);
            _byKey.Remove(key);
            _totalCost -= existing.Value.Cost;
            if (!ReferenceEquals(existing.Value.Value, value))
                _onEvict(existing.Value.Value);
        }
        var node = _usage.AddFirst((key, value, cost));
        _byKey.Add(key, node);
        _totalCost += cost;
        while (_byKey.Count > _capacity || _totalCost > _maxCost)
        {
            var evicted = _usage.Last!;
            _usage.RemoveLast();
            _byKey.Remove(evicted.Value.Key);
            _totalCost -= evicted.Value.Cost;
            _onEvict(evicted.Value.Value);
        }
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
        _totalCost = 0;
    }
}
