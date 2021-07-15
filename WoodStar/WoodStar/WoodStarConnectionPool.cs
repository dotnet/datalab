namespace WoodStar;

internal class WoodStarConnectionPool
{
    private WoodStarConnectionInternal? _cachedConnection;
    private readonly PoolKey _poolKey;

    public WoodStarConnectionPool(PoolKey poolKey)
    {
        _poolKey = poolKey;
    }

    public WoodStarConnectionInternal GetInnerConnection()
    {
        if (_cachedConnection == null)
        {
            _cachedConnection = new WoodStarConnectionInternal(_poolKey.Server, _poolKey.UserName, _poolKey.Password, _poolKey.Database);
        }

        return _cachedConnection;
    }
}
