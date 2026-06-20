using System.Collections.Concurrent;

namespace MX.Platform.Status.App.Caching;

public sealed class InMemoryCache<T>
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _ttl;

    public InMemoryCache(TimeSpan ttl)
    {
        _ttl = ttl;
    }

    public async Task<T> GetOrCreateAsync(string key, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken = default)
    {
        if (TryGetValue(key, out var cached))
        {
            return cached!;
        }

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetValue(key, out cached))
            {
                return cached!;
            }

            var value = await factory(cancellationToken).ConfigureAwait(false);
            _entries[key] = new CacheEntry(value, DateTimeOffset.UtcNow);
            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    public bool TryGetValue(string key, out T? value)
    {
        value = default;
        if (_entries.TryGetValue(key, out var entry) && DateTimeOffset.UtcNow - entry.CreatedAtUtc < _ttl)
        {
            value = entry.Value;
            return true;
        }

        if (entry is not null)
        {
            _entries.TryRemove(key, out _);
        }

        return false;
    }

    public void Set(string key, T value) => _entries[key] = new CacheEntry(value, DateTimeOffset.UtcNow);

    public bool Remove(string key) => _entries.TryRemove(key, out _);

    public void Clear() => _entries.Clear();

    private sealed record CacheEntry(T Value, DateTimeOffset CreatedAtUtc);
}
