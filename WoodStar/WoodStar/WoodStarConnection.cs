using System;
using System.Threading.Tasks;

namespace WoodStar;

public class WoodStarConnection : IDisposable
{
    private readonly WoodStarConnectionInternal _innerConnection;

    public WoodStarConnection(string server, string userName, string password, string database)
    {
        var pool = WoodStarConnectionPoolFactory.Instance.GetPool(new PoolKey(server, userName, password, database));
        _innerConnection = pool.GetInnerConnection();
    }

    public async Task OpenAsync()
    {
        switch (_innerConnection.State)
        {
            case ConnectionState.Open:
                throw new InvalidOperationException();

            case ConnectionState.Closed:
                await _innerConnection.OpenAsync();
                break;

            case ConnectionState.Returned:
                _innerConnection.ResetConnection();
                break;
        }
    }

    public Task<QueryResult> ExecuteSqlAsync(string sql)
    {
        return _innerConnection.ExecuteSqlAsync(sql);
    }

    public void CloseAsync()
    {
        _innerConnection.ReturnToPool();
    }

    public void Dispose()
    {
        _innerConnection.ReturnToPool();
    }
}
