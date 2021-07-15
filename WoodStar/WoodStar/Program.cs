using System;
using System.Threading.Tasks;

namespace WoodStar
{
    class Program
    {
        static async Task Main(string[] args)
        {
            using (var connection = new WoodStarConnection("SLDS", "sa", "Password1!", "hello_world"))
            {
                await connection.OpenAsync();
                var queryResult = await connection.ExecuteSqlAsync("SELECT id, message FROM fortune");

                while (await queryResult.ReadAsync())
                {
                    Console.WriteLine(queryResult.GetValue<int>(0));
                    Console.WriteLine(queryResult.GetValue<string>(1));
                }
            }

            using (var connection = new WoodStarConnection("SLDS", "sa", "Password1!", "hello_world"))
            {
                await connection.OpenAsync();
                var queryResult = await connection.ExecuteSqlAsync("SELECT id, message FROM fortune");

                while (await queryResult.ReadAsync())
                {
                    Console.WriteLine(queryResult.GetValue<int>(0));
                    Console.WriteLine(queryResult.GetValue<string>(1));
                }
            }

            Console.WriteLine("Success");
        }
    }
}
