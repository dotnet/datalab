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

## Other .NET data projects on GitHub

If you're interested in making .NET data better, then consider contributing to one of the many open-source repos hosted on GitHub.

### Microsoft repos

- [.NET Runtime](https://github.com/dotnet/runtime) - ADO.NET lives here in the .NET BCL
- [Microsoft.Data.SqlClient (ADO.NET provider for SQL Server)](https://github.com/dotnet/sqlclient) - Microsoft.Data.SqlClient
- [EF Core](https://github.com/dotnet/efcore) - Entity Framework Core (SQL Server/Sqlite/Cosmos) and Microsoft.Data.Sqlite

## Community repos

- [EF Core Power Tools](https://github.com/ErikEJ/EFCorePowerTools)
- [StackExchange.Dapper](https://github.com/StackExchange/Dapper)
- [NHibernate](https://github.com/nhibernate)
- [Npgsql ADO.NET provider for PostgreSQL](https://github.com/npgsql/npgsql)
- [Npgsql EF Core provider for PostgreSQL](https://github.com/npgsql/efcore.pg)
- [MySqlConnector ADO.NET provider for MySQL](https://github.com/mysql-net/MySqlConnector)
- [Pomelo EF Core provider for MySQL](https://github.com/PomeloFoundation/Pomelo.EntityFrameworkCore.MySql)
- [Firebird .NET Data Provider](https://github.com/cincuranet/FirebirdSql.Data.FirebirdClient)
- [nHydrate ORM for Entity Framework Core](https://github.com/nHydrate/nHydrate)
- [Entity Framework Visual Designer](https://github.com/msawczyn/EFDesigner)
- [CatFactory](https://github.com/hherzl/CatFactory.EntityFrameworkCore)
- [LoreSoft's Entity Framework Core Generator](https://github.com/loresoft/EntityFrameworkCore.Generator)
- [Microsoft.EntityFrameworkCore.AutoHistory](https://github.com/Arch/AutoHistory/)
- [EFCoreSecondLevelCacheInterceptor](https://github.com/VahidN/EFCoreSecondLevelCacheInterceptor)
- [Geco (Generator Console)](https://github.com/iQuarc/Geco)
- [EntityFrameworkCore.Scaffolding.Handlebars](https://github.com/TrackableEntities/EntityFrameworkCore.Scaffolding.Handlebars)
- [NeinLinq.EntityFrameworkCore](https://github.com/axelheer/nein-linq/)
- [Microsoft.EntityFrameworkCore.UnitOfWork](https://github.com/Arch/UnitOfWork/)
- [EFCore.BulkExtensions](https://github.com/borisdj/EFCore.BulkExtensions)
- [Bricelam.EntityFrameworkCore.Pluralizer](https://github.com/bricelam/EFCore.Pluralizer)
- [Toolbelt.EntityFrameworkCore.IndexAttribute](https://github.com/jsakamoto/EntityFrameworkCore.IndexAttribute)
- [Verify.EntityFramework](https://github.com/VerifyTests/Verify.EntityFramework)
- [LocalDb](https://github.com/SimonCropp/LocalDb)
- [EfFluentValidation](https://github.com/SimonCropp/EfFluentValidation)
- [EFCore.TemporalSupport](https://github.com/cpoDesign/EFCore.TemporalSupport)
- [EfCoreTemporalTable](https://github.com/glautrou/EfCoreTemporalTable)
- [EntityFrameworkCore.TemporalTables](https://github.com/findulov/EntityFrameworkCore.TemporalTables)
- [EntityFrameworkCore.Cacheable](https://github.com/SteffenMangold/EntityFrameworkCore.Cacheable)
- [EntityFrameworkCore.NCache](https://www.alachisoft.com/ncache/ef-core-cache.html)
- [EntityFrameworkCore.Triggered](https://github.com/koenbeuk/EntityFrameworkCore.Triggered)
- [Expressionify](https://github.com/ClaveConsulting/Expressionify)
- [Ramses](https://github.com/JValck/Ramses)
- [EFCore.NamingConventions](https://github.com/efcore/EFCore.NamingConventions)
- [SimplerSoftware.EntityFrameworkCore.SqlServer.NodaTime](https://github.com/StevenRasmussen/EFCore.SqlServer.NodaTime)
- [Dabble.EntityFrameworkCore.Temporal.Query](https://github.com/Adam-Langley/efcore-temporal-query)
- [EntityFrameworkCore.SqlServer.HierarchyId](https://github.com/efcore/EFCore.SqlServer.HierarchyId)
- [linq2db.EntityFrameworkCore](https://github.com/linq2db/linq2db.EntityFrameworkCore)
- [EFCore.SoftDelete](https://www.nuget.org/packages/EFCore.SoftDelete)
- [EntityFrameworkCore.ConfigurationManager](https://github.com/efcore/EFCore.ConfigurationManager)
- [EntityFrameworkCore.Sqlite.NodaTime](https://github.com/khellang/EFCore.Sqlite.NodaTime)
- [ErikEJ.EntityFrameworkCore.SqlServer.Dacpac](https://github.com/ErikEJ/EFCorePowerTools/wiki/ErikEJ.EntityFrameworkCore.SqlServer.Dacpac)
- [ErikEJ.EntityFrameworkCore.DgmlBuilder](https://github.com/ErikEJ/EFCorePowerTools/wiki/Inspect-your-DbContext-model)

Feel free to send a pull request to add your .NET data related GitHub repo to this list.
