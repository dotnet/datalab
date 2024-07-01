using System.Net;
using Woodstar.Tds;
using Xunit;

namespace Woodstar.FunctionalTests;

[CollectionDefinition("Database")]
public class DatabaseService : ICollectionFixture<DatabaseService>
{
    public const string EndPoint = "127.0.0.1:1433";
    public const string Username = "<USERNAME>";
    public const string Password = "PLACEHOLDER";
    public const string Database = "test";

    internal ValueTask<SqlServerStreamConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        => SqlServerStreamConnection.ConnectAsync(IPEndPoint.Parse(EndPoint), cancellationToken);
}
