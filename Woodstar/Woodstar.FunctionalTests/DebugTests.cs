using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Data.SqlClient;
using Woodstar.Buffers;
using Woodstar.Pipelines;
using Woodstar.SqlServer;
using Woodstar.Tds;
using Woodstar.Tds.Messages;
using Woodstar.Tds.Packets;
using Woodstar.Tds.SqlServer;
using Woodstar.Tds.Tds33;
using Woodstar.Tds.Tokens;
using Xunit;
using Xunit.Abstractions;

namespace Woodstar.FunctionalTests;

[Collection("Database")]
public class DebugTests
{
    private readonly ITestOutputHelper _outputHelper;
    readonly DatabaseService _databaseService;

    public DebugTests(DatabaseService databaseService, ITestOutputHelper outputHelper)
    {
        _databaseService = databaseService;
        _outputHelper = outputHelper;
    }

    [Fact]
    public async Task Simple_query()
    {
        var dataSource = new WoodstarDataSource(new WoodstarDataSourceOptions
        {
            EndPoint = IPEndPoint.Parse(DatabaseService.EndPoint),
            Username = DatabaseService.Username,
            Password = DatabaseService.Password,
            Database = DatabaseService.Database
        }, new TdsProtocolOptions());

        var cmd = new LowLevelSqlCommand("SELECT 1");
        var slot = await dataSource.GetSlotAsync(exclusiveUse: true, Timeout.InfiniteTimeSpan);
        var batch = await dataSource.WriteCommandAsync(slot, cmd);

        var op = await batch.Single.GetOperation();
        var reader = ((TdsProtocol)op.Protocol).Reader;
        await reader.ReadAndExpectAsync<EnvChangeToken>();
        var metadata = await reader.ReadAndExpectAsync<ColumnMetadataToken>();
        var resultSetReader = await reader.GetResultSetReaderAsync(metadata.ColumnData);
        var value = await resultSetReader.GetAsync<int>();
        Assert.Equal(1, value);
        Assert.False(await resultSetReader.MoveToNextRow());
        op.Complete();
    }

    [Fact]
    public async Task Fortunes()
    {
        var dataSource = new WoodstarDataSource(new WoodstarDataSourceOptions
        {
            EndPoint = IPEndPoint.Parse(DatabaseService.EndPoint),
            Username = DatabaseService.Username,
            Password = DatabaseService.Password,
            Database = "Fortunes"
        }, new TdsProtocolOptions());

        var cmd = new LowLevelSqlCommand("SELECT id, message FROM fortune");
        var slot = await dataSource.GetSlotAsync(exclusiveUse: true, Timeout.InfiniteTimeSpan);
        var batch = await dataSource.WriteCommandAsync(slot, cmd);
        var op = await batch.Single.GetOperation();
        var reader = ((TdsProtocol)op.Protocol).Reader;

        await reader.ReadAndExpectAsync<EnvChangeToken>();
        var metadata = await reader.ReadAndExpectAsync<ColumnMetadataToken>();
        var resultSetReader = await reader.GetResultSetReaderAsync(metadata.ColumnData);

        var rows = 0;
        do
        {
            var id = await resultSetReader.GetAsync<int>(0);
            var message = await resultSetReader.GetAsync<string>(1);

            if (id == 7)
                Assert.Equal("Any program that runs right is obsolete.", message);
            rows++;
        } while (await resultSetReader.MoveToNextRow());

        Assert.Equal(12, rows);
        op.Complete();
    }

    [Fact]
    public async Task Pipelining()
    {
        const int NumStatements = 10;

        var connection = await _databaseService.OpenConnectionAsync();

        var protocol = await TdsProtocol.StartAsync(connection.Writer, connection.Stream, new SqlServerOptions
        {
            EndPoint = IPEndPoint.Parse(DatabaseService.EndPoint),
            Username = DatabaseService.Username,
            Password = DatabaseService.Password,
            Database = DatabaseService.Database
        }, null);

        var commandWriter = new TdsCommandWriter(new SqlServerDatabaseInfo(), Encoding.Unicode);

        var commandContexts = new CommandContext[2];
        for (var i = 0; i < commandContexts.Length; i++)
        {
            if (!protocol.TryStartOperation(out var slot, OperationBehavior.None, CancellationToken.None))
                throw new InvalidOperationException();
            commandContexts[i] = commandWriter.WriteAsync(slot, new LowLevelSqlCommand("SELECT 1"), flushHint: i == commandContexts.Length - 1);
        }

        foreach (var commandContext in commandContexts)
        {
            var op = await commandContext.GetOperation();
            var reader = ((TdsProtocol)op.Protocol).Reader;
            await reader.ReadAndExpectAsync<EnvChangeToken>();
            var metadata = await reader.ReadAndExpectAsync<ColumnMetadataToken>();
            var resultSetReader = await reader.GetResultSetReaderAsync(metadata.ColumnData);
            var value = await resultSetReader.GetAsync<int>();
            Assert.Equal(1, value);
            Assert.False(await resultSetReader.MoveToNextRow());
            op.Complete();
        }
    }

    [Fact]
    public async Task Multiplexing()
    {
        var dataSource = new WoodstarDataSource(new WoodstarDataSourceOptions
        {
            EndPoint = IPEndPoint.Parse(DatabaseService.EndPoint),
            Username = DatabaseService.Username,
            Password = DatabaseService.Password,
            Database = DatabaseService.Database
        }, new TdsProtocolOptions());

        var cmd = new LowLevelSqlCommand("SELECT 1");
        var readerTasks = new Task[5];
        for (var i = 0; i < readerTasks.Length; i++)
        {
            readerTasks[i] = Execute(dataSource);
        }

        for (var i = 0; i < readerTasks.Length; i++)
        {
            await readerTasks[i];
        }

        async Task Execute(WoodstarDataSource dataSource)
        {
            var batch = await dataSource.WriteMultiplexingCommand(cmd);
            var op = await batch.Single.GetOperation();
            var reader = ((TdsProtocol)op.Protocol).Reader;
            var execution = batch.Single.GetCommandExecution();
            await reader.ReadAndExpectAsync<EnvChangeToken>();
            var metadata = await reader.ReadAndExpectAsync<ColumnMetadataToken>();
            var resultSetReader = await reader.GetResultSetReaderAsync(metadata.ColumnData);
            Assert.Equal(1, await resultSetReader.GetAsync<int>());
            await resultSetReader.MoveToNextRow();
            op.Complete();
        }
    }

    [Fact]
    public async Task SqlClient()
    {
        const string ConnectionString = $"Server=127.0.0.1;User ID={DatabaseService.Username};Password={DatabaseService.Password};Initial Catalog={DatabaseService.Database};Integrated Security=False;TrustServerCertificate=true;Encrypt=false";

        var builder = new SqlConnectionStringBuilder(ConnectionString);
        builder.Encrypt = false;
        builder.Authentication = SqlAuthenticationMethod.SqlPassword;
        await using var conn = new SqlConnection(builder.ToString());
        await conn.OpenAsync();
        var command = $"SELECT value FROM GENERATE_SERIES(1, {10});";
    }

    struct LowLevelSqlCommand : ISqlCommand
    {
        static readonly ISqlCommand.BeginExecutionDelegate BeginExecutionCoreDelegate = BeginExecutionCore;

        public LowLevelSqlCommand(string sqlStatement)
            => SqlStatement = sqlStatement;

        public string SqlStatement { get; }

        public ISqlCommand.Values GetValues()
        {
            return new ISqlCommand.Values
            {
                StatementText = (SizedString)SqlStatement,
                ExecutionFlags = ExecutionFlags.Default,
                ExecutionTimeout = Timeout.InfiniteTimeSpan,
                ParameterContext = ParameterContext.Empty,
            };
        }

        public CommandExecution BeginExecution(in ISqlCommand.Values values) => BeginExecutionCore(values);
        public ISqlCommand.BeginExecutionDelegate BeginExecutionMethod => BeginExecutionCoreDelegate;

        static CommandExecution BeginExecutionCore(in ISqlCommand.Values values)
        {
            return CommandExecution.Create(values.ExecutionFlags, values.CommandFlags);
        }
    }
}
