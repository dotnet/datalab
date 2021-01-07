# .NET Data Lab

This repo is for experimentation and exploring new ideas involving ADO.NET, EF Core, and other areas realted to .NET data.

## Current projects

### SqlServer.Core (Project Woodstar)

[Microsoft.Data.SqlClient](https://github.com/dotnet/sqlclient) is a fully-featured ADO.NET database provider for SQL Server. It supports a broad range of SQL Server features on both .NET Core and .NET Framework. However, it is also a large and old codebase with many complex interactions between its behaviors. This makes it difficult to investigate the potential gains that could be made using newer .NET Core features. Therefore, we are starting this experiment in collaboration with the community to determine what potential there is for a highly performant SQL Server driver for .NET.

> Important! Investment in Microsoft.Data.SqlClient is not changing. It will continue to be the recommended way to connect to SQL Server and SQL Azure, both with and without EF Core. It will continue to support new SQL Server features as they are introduced.

## License

This project is licensed under the [MIT license](LICENSE).

## .NET Foundation

This project is a part of the [.NET Foundation].

[.NET Foundation]: http://www.dotnetfoundation.org/projects
[.NET Foundation forums]: http://forums.dotnetfoundation.org/

## Other .NET data projects on GitHub.

- [.NET Runtime](https://github.com/dotnet/runtime) - ADO.NET lives here in the .NET BCL
- [Microsoft.Data.SqlClient](https://github.com/dotnet/sqlclient) - Microsoft.Data.SqlClient
- [EF Core](https://github.com/dotnet/efcore) - Entity Framework Core and Microsoft.Data.Sqlite
- [StackExchange.Dapper](https://github.com/StackExchange/Dapper) - The Dapper Micro-OR/M
- TODO...
