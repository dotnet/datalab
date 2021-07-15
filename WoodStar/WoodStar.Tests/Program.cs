using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Data.SqlClient;

namespace WoodStar.Tests
{
    public class Program
    {
        private static readonly string _connectionString = "Server=SLDS;user=sa;password=Password1!;database=hello_world;TrustServerCertificate=True;Trusted_Connection=False";

        static async Task Main(string[] args)
        {
            BenchmarkRunner.Run<Program>();
        }

        [Benchmark]
        public async Task<List<Fortune>> LoadFortunesRowsSqlClient()
        {
            var result = new List<Fortune>(20);

            using (var db = new SqlConnection(_connectionString))
            {
                await db.OpenAsync();

                using (var cmd = new SqlCommand("SELECT Id, Message FROM fortune", db))
                using (var rdr = await cmd.ExecuteReaderAsync())
                {
                    while (await rdr.ReadAsync())
                    {
                        result.Add(new Fortune
                        (
                            id: rdr.GetInt32(0),
                            message: rdr.GetString(1)
                        ));
                    }
                }
            }

            return result;
        }

        [Benchmark]
        public async Task<List<Fortune>> LoadFortunesRowsWoodstar()
        {
            var result = new List<Fortune>(20);

            using (var db = new WoodStarConnection("SLDS", "sa", "Password1!", "hello_world"))
            {
                await db.OpenAsync();

                var queryResult = await db.ExecuteSqlAsync("SELECT Id, Message FROM fortune");
                while (await queryResult.ReadAsync())
                {
                    result.Add(new Fortune
                    (
                        id: queryResult.GetValue<int>(0),
                        message: queryResult.GetValue<string>(1)
                    ));
                }
            }

            return result;
        }

        public class Fortune
        {
            public Fortune(int id, string message)
            {
                Id = id;
                Message = message;
            }

            public int Id { get; }
            public string Message { get; }
        }
    }
}
