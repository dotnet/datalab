namespace WoodStar;

internal record PoolKey
{
    public PoolKey(string server, string userName, string password, string database)
    {
        Server = server;
        UserName = userName;
        Password = password;
        Database = database;
    }

    public string Server { get; init; }
    public string UserName { get; init; }
    public string Password { get; init; }
    public string Database { get; init; }
}
