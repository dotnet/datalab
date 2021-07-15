using System.Collections.Concurrent;

namespace WoodStar;

internal class WoodStarConnectionPoolFactory
{
    internal static WoodStarConnectionPoolFactory Instance = new();

    private readonly ConcurrentDictionary<PoolKey, WoodStarConnectionPool> _cache = new();

    private WoodStarConnectionPoolFactory()
    {
    }

    internal WoodStarConnectionPool GetPool(PoolKey poolKey)
    {
        if (!_cache.TryGetValue(poolKey, out var pool))
        {
            pool = new WoodStarConnectionPool(poolKey);
            _cache[poolKey] = pool;
        }

        return pool;
    }
}
