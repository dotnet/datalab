using System.Data;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using Microsoft.Data.SqlClient;
using Woodstar.Tds;
using Woodstar.Tds.Tds33;
using Woodstar.Tds.Tokens;

namespace Woodstar.Benchmark;

[Config(typeof(Config))]
public class Benchmarks
{
    const int Connections = 10;
    class Config : ManualConfig
    {
        public Config()
        {
            Add(new SimpleJobAttribute(targetCount: 20).Config);
            AddDiagnoser(MemoryDiagnoser.Default);
            AddDiagnoser(ThreadingDiagnoser.Default);
            AddColumn(new TagColumn("Connections", name => (name.EndsWith("Pipelined") ? 1 : Connections).ToString()));
        }
    }

    const string EndPoint = "127.0.0.1:1433";
    const string Username = "sa";
    const string Password = "Abcd5678";
    const string Database = "test";

    const string ConnectionString = $"Data Source=127.0.0.1;User ID={Username};Password={Password};Initial Catalog={Database};Integrated Security=False;TrustServerCertificate=true;";
    // static WoodstarDataSourceOptions Options { get; } = new()
    // {
    //     EndPoint = IPEndPoint.Parse(EndPoint),
    //     Username = Username,
    //     Password = Password,
    //     Database = Database,
    //     PoolSize = Connections
    // };


    string _commandText = string.Empty;

    SqlConnection _sqlclientConn;

    string _connectionString;

    //
    // Woodstar.WoodstarConnection _conn;
    // Woodstar.WoodstarCommand _pipeliningCommand;
    // Woodstar.WoodstarCommand _multiplexedPipeliningCommand;
    // Woodstar.WoodstarCommand _multiplexingCommand;
    //
    // [GlobalSetup(Targets = new[] { nameof(PipelinesPipelined), nameof(PipelinesMultiplexingPipelined), nameof(PipelinesMultiplexing) })]
    // public async ValueTask SetupPipelines()
    // {
    //     _commandText = $"SELECT generate_series(1, {RowsPer})";
    //
    //     // Pipelining
    //     var dataSource = new Woodstar.WoodstarDataSource(Options with {PoolSize = 1}, ProtocolOptions);
    //     _conn = new Woodstar.WoodstarConnection(dataSource);
    //     await _conn.OpenAsync();
    //     _pipeliningCommand = new Woodstar.WoodstarCommand(_commandText, _conn);
    //
    //     var dataSource2 = new Woodstar.WoodstarDataSource(Options with {PoolSize = 1}, ProtocolOptions);
    //     _multiplexedPipeliningCommand = dataSource2.CreateCommand(_commandText);
    //
    //     // Multiplexing
    //     var dataSource3 = new Woodstar.WoodstarDataSource(Options, ProtocolOptions);
    //     _multiplexingCommand = dataSource3.CreateCommand(_commandText);
    // }
    //
    [GlobalSetup(Targets = new []{ nameof(SqlClient) })]
    public async ValueTask SetupSqlClient()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        builder.MinPoolSize = Connections * 10;
        builder.MaxPoolSize = Connections * 10;
        _connectionString = builder.ToString();
        _sqlclientConn = new SqlConnection(builder.ToString());
        await _sqlclientConn.OpenAsync();
        _commandText = $"SELECT 1";

        // //Warmup
        // for (int i = 0; i < 1000; i++)
        // {
        //     using var cmd = new SqlCommand(_commandText, _sqlclientConn);
        //     await using var reader = await cmd.ExecuteReaderAsync();
        //     while (await reader.ReadAsync())
        //     {
        //     }
        // }
    }

    WoodstarDataSource DataSource { get; set;  }

    [GlobalSetup(Targets = new []{ nameof(Woodstar), nameof(WoodstarMultiplexing) })]
    public async ValueTask SetupWoodstar()
    {
        DataSource = new WoodstarDataSource(new WoodstarDataSourceOptions
        {
            EndPoint = IPEndPoint.Parse(EndPoint),
            Username = Username,
            Password = Password,
            Database = Database,
            PoolSize = Connections
        }, new TdsProtocolOptions());
    }

    // [Params(1,10,100,1000)]
    public int RowsPer { get; set; }

    [Params(1000)]
    public int Commands { get; set; }

    // [Benchmark(Baseline = true)]
    public async ValueTask SqlClient()
    {
        var readerTasks = new Task<SqlDataReader>[Commands];
        for (var i = 0; i < Commands; i++)
        {
            readerTasks[i] = Execute();
        }

        for (var i = 0; i < readerTasks.Length; i++)
        {
            await using var reader = await readerTasks[i];
            while (await reader.ReadAsync())
            {
            }
        }

        async Task<SqlDataReader> Execute()
        {
            var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = new SqlCommand(_commandText, conn);
            return await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);
        }

    }

    [Benchmark]
    public async ValueTask Woodstar()
    {
        var cmd = new LowLevelSqlCommand();
        var slot = await DataSource.GetSlotAsync(exclusiveUse: true, Timeout.InfiniteTimeSpan);
        var batch = await DataSource.WriteCommandAsync(slot, cmd);
        // DataSource.WriteMultiplexingCommand(cmd);
        var op = await batch.Single.GetOperation();
        var reader = ((TdsProtocol)op.Protocol).Reader;
        var execution = batch.Single.GetCommandExecution();
        await reader.ReadAndExpectAsync<EnvChangeToken>();
        var metadata = await reader.ReadAndExpectAsync<ColumnMetadataToken>();
        var resultSetReader = await reader.GetResultSetReaderAsync(metadata.ColumnData);
        var value = await resultSetReader.GetAsync<int>();
        // var value2 = await resultSetReader.GetAsync<int>();
        await resultSetReader.MoveToNextRow();
        op.Complete();
        //
        //
        // var multiplexingCommand = _multiplexedPipeliningCommand;
        // var readerTasks = new Task<Slon.SlonDataReader>[Commands];
        // for (var i = 0; i < readerTasks.Length; i++)
        // {
        //     readerTasks[i] = multiplexingCommand.ExecuteReaderAsync();
        // }
        //
        // for (var i = 0; i < readerTasks.Length; i++)
        // {
        //     await using var reader = await readerTasks[i];
        //     while (await reader.ReadAsync())
        //     {
        //     }
        // }

    }

    // [Benchmark]
    public async ValueTask WoodstarMultiplexing()
    {
        var cmd = new LowLevelSqlCommand();
        var readerTasks = new Task[Commands];
        for (var i = 0; i < readerTasks.Length; i++)
        {
            readerTasks[i] = Execute();
        }

        for (var i = 0; i < readerTasks.Length; i++)
        {
            await readerTasks[i];
        }

        async Task Execute()
        {
            var batch = await DataSource.WriteMultiplexingCommand(cmd);
            var op = await batch.Single.GetOperation();
            var reader = ((TdsProtocol)op.Protocol).Reader;
            var execution = batch.Single.GetCommandExecution();
            await reader.ReadAndExpectAsync<EnvChangeToken>();
            var metadata = await reader.ReadAndExpectAsync<ColumnMetadataToken>();
            var resultSetReader = await reader.GetResultSetReaderAsync(metadata.ColumnData);
            var value = await resultSetReader.GetAsync<int>();
            // var value2 = await resultSetReader.GetAsync<int>();
            await resultSetReader.MoveToNextRow();
            await reader.ReadAndExpectAsync<DoneToken>();
            op.Complete();
        }
    }

    struct LowLevelSqlCommand : ISqlCommand
    {
        static readonly ISqlCommand.BeginExecutionDelegate BeginExecutionCoreDelegate = BeginExecutionCore;

        public ISqlCommand.Values GetValues()
        {
            return new ISqlCommand.Values
            {
                StatementText = (SizedString)"SELECT 1",
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
