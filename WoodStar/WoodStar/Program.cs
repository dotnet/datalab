using System;
using System.Threading.Tasks;

namespace WoodStar
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var connection = new WoodStarConnection("10.130.64.58", "sa", "Password1!", "master");
            await connection.OpenAsync();

            await connection.ExecuteSqlAsync("Select 1, N'Data', 1.2");

            Console.WriteLine("Success");
        }
    }
}
