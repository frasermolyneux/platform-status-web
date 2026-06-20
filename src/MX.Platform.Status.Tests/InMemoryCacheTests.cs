using MX.Platform.Status.App.Caching;
using System.Collections.Concurrent;

namespace MX.Platform.Status.Tests;

public sealed class InMemoryCacheTests
{
    [Fact]
    public async Task ReturnsCachedValueWithinTtl()
    {
        var cache = new InMemoryCache<int>(TimeSpan.FromMinutes(1));
        var first = await cache.GetOrCreateAsync("mx", _ => Task.FromResult(1));
        var second = await cache.GetOrCreateAsync("mx", _ => Task.FromResult(2));
        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public async Task CallsFactoryAfterTtlExpires()
    {
        var cache = new InMemoryCache<int>(TimeSpan.FromMilliseconds(20));
        var first = await cache.GetOrCreateAsync("mx", _ => Task.FromResult(1));
        await Task.Delay(50);
        var second = await cache.GetOrCreateAsync("mx", _ => Task.FromResult(2));
        Assert.Equal(1, first);
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task IsThreadSafeForConcurrentAccess()
    {
        var cache = new InMemoryCache<int>(TimeSpan.FromMinutes(1));
        var calls = 0;
        var results = new ConcurrentBag<int>();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            var value = await cache.GetOrCreateAsync("mx", async __ =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(10);
                return 42;
            });
            results.Add(value);
        }));

        Assert.Equal(1, calls);
        Assert.All(results, value => Assert.Equal(42, value));
    }
}
