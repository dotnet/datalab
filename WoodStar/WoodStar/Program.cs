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

            Console.WriteLine("Success");
        }
    }
}
